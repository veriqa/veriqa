// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The one input of the calls of Veriqa: POST on the base URL of the adapter.
//
// A call goes through the same steps in the same order, and the platform is reached only after all of them:
//
// 1. the signature of the transport — anything else is 403 without an envelope and without an effect;
// 2. the envelope — the major version, the operation, the channel type; unknown fields are ignored;
// 3. the credentials of the tenant of the call;
// 4. the operation, once per idempotency key when it has an effect the user sees.

import { describeTenant, logger, readBoundedBody, sendEmpty, sendJson } from "../common.js";
import { PlatformCannotContinueError, PlatformError } from "./seam.js";
import {
  ADAPTER_VERSION,
  ALL_LINK_PURPOSES,
  ALL_OPERATIONS,
  ErrorCode,
  Field,
  LinkPurpose,
  Operation,
  WEBHOOK_ID_HEADER,
  WIRE_MAJOR_VERSION,
  WIRE_VERSION,
  errorEnvelope,
  isTransactionId,
  majorVersionOf,
  resultEnvelope,
} from "./wire.js";

const log = logger("core.calls");

// The display intent that replaces the prompt with the outcome; any other value — known or not — is a new message.
const REPLACE_PROMPT_INTENT = "replace_prompt";

/** A signed body that is not a call of this wire: answered 400 without an envelope. */
class MalformedCall extends Error {}

/** Handles the calls of Veriqa for the registration ``channelType``. */
export class CallInput {
  constructor(channelType, signature, platform, idempotentCalls, store) {
    this.channelType = channelType;
    this.signature = signature;
    this.platform = platform;
    this.idempotentCalls = idempotentCalls;
    this.store = store;
  }

  /** ``POST /``: one call of Veriqa. */
  async handle(request, response) {
    const body = await readBoundedBody(request);
    if (body === null) {
      sendEmpty(response, 413);
      return;
    }
    if (!this.signature.verify(request.headers, body)) {
      sendEmpty(response, 403);
      return;
    }

    let answer;
    try {
      answer = await this.answer(jsonObject(body), request.headers[WEBHOOK_ID_HEADER]);
    } catch (error) {
      if (!(error instanceof MalformedCall)) {
        throw error;
      }
      log.warning(`A signed call was refused: ${error.message}.`);
      sendEmpty(response, 400);
      return;
    }
    sendJson(response, 200, answer);
  }

  async answer(envelope, webhookId) {
    const majorVersion = majorVersionOf(envelope[Field.VERSION]);
    if (majorVersion === null) {
      throw new MalformedCall("no wire version");
    }
    if (majorVersion !== WIRE_MAJOR_VERSION) {
      return errorEnvelope(ErrorCode.UNSUPPORTED_WIRE_VERSION, `The adapter speaks major version ${WIRE_MAJOR_VERSION} of the wire.`);
    }

    const operation = envelope[Field.OPERATION];
    if (typeof operation !== "string") {
      // An event envelope reflected onto this input has no operation: it is not a call.
      throw new MalformedCall("no operation");
    }
    if (!ALL_OPERATIONS.has(operation)) {
      return errorEnvelope(ErrorCode.UNSUPPORTED_OPERATION, "The adapter does not know the operation.");
    }
    if (envelope[Field.CHANNEL_TYPE] !== this.channelType) {
      return errorEnvelope(ErrorCode.CHANNEL_NOT_AVAILABLE, "The adapter does not serve this channel type.");
    }

    const payload = envelope[Field.PAYLOAD];
    if (!isObject(payload)) {
      throw new MalformedCall("no payload");
    }
    if (operation === Operation.HEALTH) {
      // Health probes every credential of the adapter; it belongs to no tenant.
      return this.health();
    }

    const tenant = optionalString(envelope, Field.TENANT);
    const settings = optionalString(envelope, Field.ADAPTER_SETTINGS);
    const account = this.platform.accountFor(tenant);
    if (account === null) {
      const code = operation === Operation.DEEP_LINK ? ErrorCode.DEEP_LINK_GENERATION_FAILED : ErrorCode.CHANNEL_NOT_AVAILABLE;
      log.warning(`A ${operation} call of ${describeTenant(tenant)} was refused: no credentials.`);
      return errorEnvelope(code, "The tenant of the call has no credentials on the platform.");
    }

    if (operation === Operation.DEEP_LINK) {
      return this.deepLink(account, settings, tenant, deepLinkRequestOf(payload));
    }

    // The signature check has required the webhook-id: it is the idempotency key of the call.
    const effect = this.effect(operation, account, settings, payload);
    if (operation === Operation.SEND_PROMPT) {
      // Kept before the prompt goes out: an answer to it may come in before the call returns.
      this.keepRoute(transactionIdOf(payload), tenant);
    }
    return this.idempotentCalls.run(operation, webhookId, effect);
  }

  /** The call with an effect, its payload read before anything reaches the platform. */
  effect(operation, account, settings, payload) {
    if (operation === Operation.SEND_PROMPT) {
      const rendered = payload[Field.RENDERED];
      if (!isObject(payload[Field.PROMPT]) || !isObject(rendered)) {
        throw new MalformedCall("no prompt");
      }
      const prompt = {
        transactionId: transactionIdOf(payload),
        channelUserId: string(payload, Field.CHANNEL_USER_ID),
        text: string(rendered, Field.TEXT),
        confirmLabel: string(rendered, Field.CONFIRM_LABEL),
        declineLabel: string(rendered, Field.DECLINE_LABEL),
      };
      return () => this.messageCall(operation, this.platform.sendPrompt(account, settings, prompt));
    }

    if (operation === Operation.SEND_MESSAGE) {
      const channelUserId = string(payload, Field.CHANNEL_USER_ID);
      const text = string(payload, Field.TEXT);
      const kind = string(payload, Field.KIND);
      return () => this.messageCall(operation, this.platform.sendMessage(account, settings, channelUserId, text, kind));
    }

    // The transaction and the outcome are required members, checked but not used: an unknown outcome is
    // shown by its status text like any other, and an unknown intent is a new message.
    transactionIdOf(payload);
    string(payload, Field.OUTCOME);
    const outcome = {
      channelUserId: string(payload, Field.CHANNEL_USER_ID),
      statusText: string(payload, Field.STATUS_TEXT),
      inboundToken: optionalString(payload, Field.INBOUND_TOKEN),
      promptMessage: messageRef(payload[Field.PROMPT_MESSAGE]),
      replacePrompt: string(payload, Field.DISPLAY_INTENT) === REPLACE_PROMPT_INTENT,
    };
    return () => this.outcomeCall(account, settings, outcome);
  }

  async messageCall(operation, sending) {
    let message;
    try {
      message = await sending;
    } catch (error) {
      return refusal(operation, error, "The platform did not take the message.");
    }
    return resultEnvelope({ [Field.MESSAGE]: { [Field.CHAT_ID]: message.chatId, [Field.MESSAGE_ID]: message.messageId } });
  }

  async outcomeCall(account, settings, outcome) {
    try {
      await this.platform.reportOutcome(account, settings, outcome);
    } catch (error) {
      return refusal(Operation.REPORT_OUTCOME, error, "The platform did not take the outcome.");
    }
    return resultEnvelope({ [Field.SHOWN]: true });
  }

  async deepLink(account, settings, tenant, request) {
    const { transactionId, purpose, renderedText } = request;
    // One link per transaction and purpose — the text of the link depends on the purpose: a repeat, whichever
    // tenant it comes with, gets the link made first.
    let url = this.store.deepLinkOf(transactionId, purpose);
    if (url === null) {
      let made;
      try {
        made = await this.platform.deepLink(account, settings, transactionId, renderedText);
      } catch (error) {
        if (!(error instanceof PlatformError)) {
          throw error;
        }
        log.warning(`A deep-link call failed on the platform: ${error.message}.`);
        return errorEnvelope(ErrorCode.DEEP_LINK_GENERATION_FAILED, "The platform did not give a deep link.");
      }
      // A concurrent call on the same transaction and purpose may have kept its link meanwhile: the kept one wins.
      this.store.keepDeepLink(transactionId, purpose, made);
      url = this.store.deepLinkOf(transactionId, purpose) ?? made;
    }
    this.keepRoute(transactionId, tenant);
    return resultEnvelope({ [Field.URL]: url });
  }

  /** Records the tenant of a transaction as the route of its events; the tenant recorded first stands. */
  keepRoute(transactionId, tenant) {
    this.store.keepRoute(transactionId, { tenant });
    const route = this.store.routeOf(transactionId);
    if (route !== null && route.tenant !== tenant) {
      log.info(
        `A call of ${describeTenant(tenant)} named a transaction recorded for ${describeTenant(route.tenant)}; `
          + "its events stay on that route.",
      );
    }
  }

  async health() {
    const report = await this.platform.health();
    return resultEnvelope({
      [Field.HEALTHY]: report.healthy,
      [Field.DETAILS]: report.details,
      [Field.ADAPTER_VERSION]: ADAPTER_VERSION,
      [Field.WIRE_VERSION]: WIRE_VERSION,
    });
  }
}

/**
 * The answer to a call the platform refused: ``channel_cannot_continue`` when the platform declared the
 * refusal final for the user — Veriqa reads it as terminal on send-prompt alone and as a plain failure to
 * send on the other operations — ``message_send_failed`` otherwise.
 */
function refusal(operation, error, message) {
  if (!(error instanceof PlatformError)) {
    throw error;
  }
  if (error instanceof PlatformCannotContinueError) {
    log.warning(`A ${operation} call cannot continue on the platform: ${error.message}.`);
    return errorEnvelope(ErrorCode.CHANNEL_CANNOT_CONTINUE, "The platform cannot carry the operation out for this user.");
  }
  log.warning(`A ${operation} call failed on the platform: ${error.message}.`);
  return errorEnvelope(ErrorCode.MESSAGE_SEND_FAILED, message);
}

function jsonObject(body) {
  let parsed;
  try {
    parsed = JSON.parse(body.toString("utf8"));
  } catch {
    throw new MalformedCall("the body is not JSON");
  }
  if (!isObject(parsed)) {
    throw new MalformedCall("the body is not a JSON object");
  }
  return parsed;
}

function isObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function string(container, name) {
  const value = container[name];
  if (typeof value !== "string") {
    throw new MalformedCall(`no ${name}`);
  }
  return value;
}

function optionalString(container, name) {
  const value = container[name];
  if (value === undefined || value === null) {
    return null;
  }
  if (typeof value !== "string") {
    throw new MalformedCall(`${name} is not a string`);
  }
  return value;
}

function transactionIdOf(payload) {
  const value = payload[Field.TRANSACTION_ID];
  if (!isTransactionId(value)) {
    throw new MalformedCall(`no valid ${Field.TRANSACTION_ID}`);
  }
  return value;
}

/**
 * The request of a deep-link call: the transaction, the purpose of the link — ``button`` for a value the adapter
 * does not know and for a call without the field — and the text Veriqa rendered for the link, ``null`` when the
 * call carries none.
 */
function deepLinkRequestOf(payload) {
  const purpose = payload[Field.PURPOSE];
  const rendered = payload[Field.RENDERED];
  if (rendered !== undefined && rendered !== null && !isObject(rendered)) {
    throw new MalformedCall(`${Field.RENDERED} is not an object`);
  }
  return {
    transactionId: transactionIdOf(payload),
    purpose: ALL_LINK_PURPOSES.has(purpose) ? purpose : LinkPurpose.BUTTON,
    renderedText: isObject(rendered) ? string(rendered, Field.TEXT) : null,
  };
}

function messageRef(value) {
  if (value === undefined || value === null) {
    return null;
  }
  if (!isObject(value)) {
    throw new MalformedCall(`${Field.PROMPT_MESSAGE} is not an object`);
  }
  return { chatId: string(value, Field.CHAT_ID), messageId: string(value, Field.MESSAGE_ID) };
}
