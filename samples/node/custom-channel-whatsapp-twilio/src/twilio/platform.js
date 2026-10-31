// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The operations of the core on WhatsApp through Twilio: the implementation of the Platform of the seam.

import { describeTenant, fingerprint, logger, sleep } from "../common.js";
import { PlatformCannotContinueError, PlatformError } from "../core/seam.js";
import { AUTH_MARKER, CONFIRM_PREFIX, DECLINE_PREFIX } from "./inbound.js";
import { normalizePhoneToE164, toWhatsAppAddress } from "./phone.js";
import { REQUEST_TIMEOUT_MS, TwilioError } from "./rest.js";
import { prefillText } from "./settings.js";

const log = logger("twilio.platform");

/** Where a deep link to a WhatsApp sender leads. */
export const DEEP_LINK_BASE_URL = "https://wa.me/";

/** The one kind of content this channel sends. */
export const PLAIN_TEXT_KIND = "plain_text";

/**
 * The variables of the ``twilio/quick-reply`` content of the prompt: its body and, for each of its two
 * buttons, the title and the id. The operator creates the content with these placeholders.
 */
export const PromptVariable = Object.freeze({
  BODY: "1",
  CONFIRM_TITLE: "2",
  CONFIRM_ID: "3",
  DECLINE_TITLE: "4",
  DECLINE_ID: "5",
});

// Limits of twilio/quick-reply: the body and the title of a button, in characters.
const MAX_PROMPT_BODY_LENGTH = 1024;
const MAX_BUTTON_TITLE_LENGTH = 20;

/** The code of Twilio for a message outside the 24-hour customer service window of WhatsApp. */
export const OUTSIDE_SERVICE_WINDOW_CODE = 63016;

// Statuses of a message that the platform has not decided yet, and the statuses of a refused one.
const INTERMEDIATE_STATUSES = new Set(["queued", "accepted", "sending"]);
const REFUSED_STATUSES = new Set(["failed", "undelivered"]);

// Pause between two reads of the status of a prompt.
const STATUS_POLL_INTERVAL_MS = 500;

/**
 * Sends the prompts, messages and outcomes of Veriqa from the WhatsApp sender of the tenant of the call.
 *
 * A code 63016 of Twilio — the customer service window of the user is closed — is a refusal the adapter
 * cannot get past: it answers ``channel_cannot_continue``. The code is looked for in the answer to the
 * creation of a message and, for a prompt, in the status of the created message while the status wait
 * lasts. The status wait is counted from the start of the call, the creation included, so send-prompt answers
 * within it unless the creation alone takes longer.
 */
export class TwilioPlatform {
  constructor(config, rest) {
    this.rest = rest;
    this.accounts = config.accounts;
    this.statusWaitMs = config.statusWaitSeconds * 1000;
    this.byTenant = new Map(config.accounts.map((account) => [account.tenant, account]));
  }

  /** The sender of the tenant; a tenant without a sender of its own uses the default one, if there is one. */
  accountFor(tenant) {
    return this.byTenant.get(tenant) ?? this.byTenant.get(null) ?? null;
  }

  async sendPrompt(account, settings, request) {
    const deadline = Date.now() + this.statusWaitMs;
    const recipient = recipientOf(request.channelUserId);
    if ([...request.text].length > MAX_PROMPT_BODY_LENGTH) {
      throw new PlatformError(`the prompt text is longer than ${MAX_PROMPT_BODY_LENGTH} characters`);
    }
    const variables = {
      [PromptVariable.BODY]: request.text,
      [PromptVariable.CONFIRM_TITLE]: truncate(request.confirmLabel, MAX_BUTTON_TITLE_LENGTH),
      [PromptVariable.CONFIRM_ID]: CONFIRM_PREFIX + request.transactionId,
      [PromptVariable.DECLINE_TITLE]: truncate(request.declineLabel, MAX_BUTTON_TITLE_LENGTH),
      [PromptVariable.DECLINE_ID]: DECLINE_PREFIX + request.transactionId,
    };
    const created = await this.create(account, recipient, {
      ContentSid: account.promptContentSid,
      ContentVariables: JSON.stringify(variables),
    });
    await this.awaitVerdict(account, created, deadline);
    log.info(`Prompt ${created.sid} sent to user ${fingerprint(recipient)} for ${describeTenant(account.tenant)}.`);
    return { chatId: recipient, messageId: created.sid };
  }

  async sendMessage(account, settings, channelUserId, text, kind) {
    if (kind !== PLAIN_TEXT_KIND) {
      throw new PlatformError(`the kind of content ${kind} is not supported`);
    }
    const recipient = recipientOf(channelUserId);
    const created = await this.create(account, recipient, { Body: text });
    return { chatId: recipient, messageId: created.sid };
  }

  async reportOutcome(account, settings, request) {
    // WhatsApp does not edit a delivered message, as the built-in channel says of it: the outcome is always
    // a new message, whatever the display intent, the prompt message or the inbound token of the call.
    await this.create(account, recipientOf(request.channelUserId), { Body: request.statusText });
  }

  /**
   * The wa.me link of a transaction. The prefilled text is the one Veriqa rendered for the link, when the call
   * carries it, and ``prefill_text`` otherwise; the sign-in code ``auth_<transaction>`` goes on a last line of
   * its own unless the text already has it — the inbound side finds the transaction by it.
   */
  async deepLink(account, settings, transactionId, renderedText) {
    const prefill = withSignInCode(renderedText ?? prefillText(settings), transactionId);
    return `${DEEP_LINK_BASE_URL}${account.senderDigits}?text=${encodeURIComponent(prefill)}`;
  }

  async health() {
    const probes = await Promise.all(this.accounts.map((account) => this.probe(account)));
    const failed = this.accounts.filter((_, index) => !probes[index]).map((account) => describeTenant(account.tenant));
    if (failed.length === 0) {
      return { healthy: true, details: null };
    }
    return { healthy: false, details: `The Twilio account is not active for ${failed.join(", ")}.` };
  }

  /** Creates a message from the sender of ``account`` to ``recipient``; refusals of Twilio are thrown. */
  async create(account, recipient, content) {
    let created;
    try {
      created = await this.rest.createMessage(account, {
        From: toWhatsAppAddress(account.sender),
        To: toWhatsAppAddress(recipient),
        ...content,
      });
    } catch (error) {
      if (error instanceof TwilioError && error.code === OUTSIDE_SERVICE_WINDOW_CODE) {
        throw new PlatformCannotContinueError(`${error.message}: the customer service window is closed`);
      }
      throw error;
    }
    refuseIfFailed(created);
    return created;
  }

  /**
   * Reads the status of a created prompt while it is intermediate, up to ``deadline``: the end of the status
   * wait of the call. A refusal found in that time is thrown; a message still intermediate at the deadline is
   * taken as sent, since the call has to answer inside the operation timeout of Veriqa.
   */
  async awaitVerdict(account, created, deadline) {
    let message = created;
    while (INTERMEDIATE_STATUSES.has(message.status)) {
      const remaining = deadline - Date.now();
      if (remaining <= 0) {
        log.info(`Prompt ${created.sid} is still ${message.status} after the status wait; it is taken as sent.`);
        return;
      }
      try {
        message = await this.rest.fetchMessage(account, created.sid, Math.min(remaining, REQUEST_TIMEOUT_MS));
      } catch (error) {
        if (!(error instanceof TwilioError)) {
          throw error;
        }
        log.warning(`The status of prompt ${created.sid} was not read: ${error.message}.`);
      }
      if (INTERMEDIATE_STATUSES.has(message.status)) {
        await sleep(Math.max(0, Math.min(STATUS_POLL_INTERVAL_MS, deadline - Date.now())));
      }
    }
    refuseIfFailed(message);
  }

  async probe(account) {
    try {
      const { status } = await this.rest.fetchAccount(account);
      if (status === "active") {
        return true;
      }
      log.warning(`Health probe for ${describeTenant(account.tenant)}: the account is ${status || "of no status"}.`);
    } catch (error) {
      if (!(error instanceof TwilioError)) {
        throw error;
      }
      log.warning(`Health probe for ${describeTenant(account.tenant)} failed: ${error.message}.`);
    }
    return false;
  }
}

/** ``text`` with the sign-in code of the transaction on a last line of its own, unless it has the code already. */
function withSignInCode(text, transactionId) {
  const code = `${AUTH_MARKER}${transactionId}`;
  return text.includes(code) ? text : `${text}\n${code}`;
}

/** The recipient of a message in E.164; the identifier of a WhatsApp user of Veriqa is the phone number. */
function recipientOf(channelUserId) {
  const recipient = normalizePhoneToE164(channelUserId);
  if (recipient === null) {
    throw new PlatformError("the channel user identifier is not a phone number in E.164");
  }
  return recipient;
}

/** Throws the refusal a failed or undelivered message carries. */
function refuseIfFailed(message) {
  if (!REFUSED_STATUSES.has(message.status)) {
    return;
  }
  const reason = `message ${message.sid} is ${message.status}${message.errorCode === null ? "" : ` with code ${message.errorCode}`}`;
  if (message.errorCode === OUTSIDE_SERVICE_WINDOW_CODE) {
    throw new PlatformCannotContinueError(`${reason}: the customer service window is closed`);
  }
  throw new TwilioError(reason, message.errorCode);
}

/** The first ``length`` characters of a text. */
function truncate(text, length) {
  return [...text].slice(0, length).join("");
}
