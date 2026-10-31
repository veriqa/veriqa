// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The operations of the core on WhatsApp through the port of the platform: the implementation of the
// Platform of the seam.

import { randomBytes } from "node:crypto";

import { describeTenant, fingerprint, logger } from "../common.js";
import { PlatformError } from "../core/seam.js";
import { AUTH_MARKER } from "./inbound.js";
import { normalizePhoneToE164 } from "./phone.js";
import { RecordState } from "./port.js";
import { prefillText } from "./settings.js";

const log = logger("whatsapp.platform");

/** Where a deep link to a WhatsApp number leads. */
export const DEEP_LINK_BASE_URL = "https://wa.me/";

/** The one kind of content this channel sends. */
export const PLAIN_TEXT_KIND = "plain_text";

/** Length of the secret the votes on a poll are encrypted with, in bytes. */
export const POLL_SECRET_BYTES = 32;

/**
 * Sends the prompts, messages and outcomes of Veriqa from the WhatsApp number of the tenant of the call.
 *
 * A prompt is a poll with two options — WhatsApp Web has no buttons — and the poll is kept, with the
 * transaction and the answer of each option, before it goes out: the vote names the poll and the option,
 * nothing else. A number with no open session refuses every sending; its deep link still works, since a
 * link needs no session.
 */
export class WhatsAppPlatform {
  constructor(config, port, polls) {
    this.port = port;
    this.polls = polls;
    this.accounts = config.accounts;
    this.byTenant = new Map(config.accounts.map((account) => [account.tenant, account]));
  }

  /** The number of the tenant; a tenant without a number of its own uses the default one, if there is one. */
  accountFor(tenant) {
    return this.byTenant.get(tenant) ?? this.byTenant.get(null) ?? null;
  }

  async sendPrompt(account, settings, request) {
    const recipient = recipientOf(request.channelUserId);
    if (request.confirmLabel === request.declineLabel) {
      // A vote names the option by its text: two options with one text make the answer unreadable.
      throw new PlatformError("the confirm and decline labels are the same, and a poll with equal options is ambiguous");
    }
    await this.requireSession(account);
    const poll = {
      messageId: this.port.newMessageId(account),
      question: request.text,
      options: [request.confirmLabel, request.declineLabel],
      secret: randomBytes(POLL_SECRET_BYTES),
    };
    this.polls.keep({
      label: account.label,
      messageId: poll.messageId,
      transactionId: request.transactionId,
      recipient,
      confirmOption: request.confirmLabel,
      declineOption: request.declineLabel,
      secret: poll.secret,
    });
    await this.port.sendPoll(account, recipient, poll);
    log.info(`Prompt ${poll.messageId} sent to user ${fingerprint(recipient)} from record ${account.label}.`);
    return { chatId: recipient, messageId: poll.messageId };
  }

  async sendMessage(account, settings, channelUserId, text, kind) {
    if (kind !== PLAIN_TEXT_KIND) {
      throw new PlatformError(`the kind of content ${kind} is not supported`);
    }
    const recipient = recipientOf(channelUserId);
    await this.requireSession(account);
    const messageId = await this.port.sendText(account, recipient, text);
    return { chatId: recipient, messageId };
  }

  async reportOutcome(account, settings, request) {
    // WhatsApp does not edit a delivered message, as the built-in channel says of it: the outcome is always
    // a new message, whatever the display intent, the prompt message or the inbound token of the call.
    const recipient = recipientOf(request.channelUserId);
    await this.requireSession(account);
    await this.port.sendText(account, recipient, request.statusText);
  }

  /**
   * The wa.me link of a transaction. The prefilled text is the one Veriqa rendered for the link, when the call
   * carries it, and ``prefill_text`` otherwise; the sign-in code ``auth_<transaction>`` goes on a last line of
   * its own unless the text already has it — the inbound side finds the transaction by it.
   */
  async deepLink(account, settings, transactionId, renderedText) {
    const prefill = withSignInCode(renderedText ?? prefillText(settings), transactionId);
    return `${DEEP_LINK_BASE_URL}${account.digits}?text=${encodeURIComponent(prefill)}`;
  }

  /** Refuses a sending from a record whose session is not open and paired with the number of the record. */
  async requireSession(account) {
    const state = await this.port.stateOf(account);
    if (state !== RecordState.OK) {
      throw new PlatformError(`record ${account.label} is ${state}`);
    }
  }

  /** Healthy when the session of every record is open and paired with the number of the record. */
  async health() {
    const states = await Promise.all(this.accounts.map((account) => this.port.stateOf(account)));
    if (states.every((state) => state === RecordState.OK)) {
      return { healthy: true, details: null };
    }
    const lines = this.accounts.map((account, index) => `record ${account.label} (${describeTenant(account.tenant)}): ${states[index]}`);
    return { healthy: false, details: `WhatsApp sessions: ${lines.join("; ")}.` };
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
