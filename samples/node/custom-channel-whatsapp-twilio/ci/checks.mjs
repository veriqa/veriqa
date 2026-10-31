// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Checks of the stand that the conformance suite does not see, run inside the network of the stand.
//
// Subcommands, all driven by run.sh:
//
// - own — the checks of the adapter itself: the protection of the inbound webhook, the messages that are not
//   events of Veriqa, the own setting of the adapter, the text of a deep link Veriqa rendered, the refusals of
//   the platform, the rules of the side of Veriqa (idempotency keys, credentials per tenant, the route of an
//   event, the envelope) and the shape of what the adapter sends to Twilio and to Veriqa. Each prints "PASS
//   <check>" or "FAIL <check>: <reason>"; what counts is what the recorder of the stub saw, not only the
//   status the adapter answered;
// - survive-before <key> <transaction> and survive-after <key> <transaction> <first> — the two phases of the
//   step state.survives-recreate of run.sh, around a recreation of the container of the adapter of the
//   checks. The first sends a message under the idempotency key and asks a deep link of the transaction for
//   the tenant shop-b, and prints what the second needs; the second repeats the message and brings the
//   transaction in on the webhook of the default record;
// - wait-stand <seconds> — waits until the stub and every adapter of the stand answer;
// - suite-report — reads the JSON report of the conformance suite from the standard input and checks the
//   outcome of every scenario;
// - wait-channel <url> <channel> <seconds> — waits until GET /health/channels of Veriqa reports the channel
//   healthy.
//
// Node alone without packages; exit code 0 when everything held, 1 when a check failed, 2 when the checks
// could not run.

import { createHmac, randomBytes, randomInt } from "node:crypto";
import { readFileSync } from "node:fs";

import {
  DELIVERY_TIMEOUT_MS,
  SIGNATURE_HEADER,
  WEBHOOK_PATH_PREFIX,
  digitsOf,
  newMessageSid,
  postWebhook,
  twilioSignature,
  whatsAppAddress,
} from "./twilio.mjs";

const CHANNEL_TYPE = "whatsapp-twilio";
const WIRE_VERSION = "1.0";
const REQUEST_TIMEOUT_MS = 30_000;
const POLL_MS = 2_000;

// Markers of Veriqa and the defaults of the adapter the checks expect.
const AUTH_MARKER = "auth_";
const DEFAULT_PREFILL_TEXT = "Send this message to sign in.";

// Error codes of the wire the checks expect.
const CHANNEL_CANNOT_CONTINUE = "channel_cannot_continue";
const CHANNEL_NOT_AVAILABLE = "channel_not_available";
const DEEP_LINK_GENERATION_FAILED = "deep_link_generation_failed";
const MESSAGE_SEND_FAILED = "message_send_failed";

// A failure of a message other than the closed customer service window (30008: unknown error).
const OTHER_FAILURE_CODE = 30008;

// The deep link host of WhatsApp.
const DEEP_LINK_HOST = "wa.me";

// Tenants of the stand: two with records of their own, one with none anywhere.
const SHOP_A = "shop-a";
const SHOP_B = "shop-b";
const TENANT_WITHOUT_RECORD = "shop-z";

// The prompt of the checks and the limits of the quick-reply content, as the README of the adapter names them:
// the body in {{1}}, the title of the confirm button in {{2}} and its id in {{3}}, the decline button in {{4}}.
const CONFIRM_LABEL = "Confirm";
const DECLINE_LABEL = "Decline";
const PROMPT_TEXT = "Sign in to the stand?";
const PROMPT_TEXT_LIMIT = 1024;
const BUTTON_TITLE_LIMIT = 20;
const CONFIRM_TITLE_VARIABLE = "2";
const CONFIRM_ID_VARIABLE = "3";
const DECLINE_TITLE_VARIABLE = "4";
const LONG_LABEL_LENGTH = 30;
const STATUS_TEXT = "You are signed in to the stand.";

// A body of a call over the limit of 1 MB the adapter must refuse.
const OVERSIZED_TEXT_LENGTH = 1_100_000;

// The empty TwiML document the adapter answers a webhook with, and its media type.
const EMPTY_TWIML = /^(<\?xml[^>]*\?>)?\s*(<Response\/>|<Response>\s*<\/Response>)\s*$/;
const TWIML_CONTENT_TYPE = "text/xml";

// Scenarios the gate names on their own: each must be in the report and passed, as every other one. The
// check of the phone number joins a run only while the registration declares the number obtained
// automatically, so a stand that lost the declaration would drop it without a failure.
const REQUIRED_SCENARIOS = [
  "platform.cannot-continue",
  "platform.credentials-by-tenant",
  "identity.phone-automatic",
  "events.all-valid",
];

const BASE62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

/** A check did not hold; the message says what was seen. */
class CheckFailed extends Error {}

/** The checks cannot start: a setting is missing or the stand is not as run.sh built it. */
class SetupError extends Error {}

function env(name) {
  const value = process.env[name] ?? "";
  if (!value) {
    throw new SetupError(`${name} is not set`);
  }
  return value;
}

function sleep(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

// ── HTTP ────────────────────────────────────────────────────────────────────

async function http(method, url, body = undefined, headers = {}) {
  const response = await fetch(url, { method, headers, body, redirect: "manual", signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS) });
  return { status: response.status, text: await response.text() };
}

function parseJson(text) {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function transactionId() {
  return Array.from({ length: 43 }, () => BASE62[randomInt(BASE62.length)]).join("");
}

function idempotencyKey() {
  return `msg_${randomBytes(16).toString("hex")}`;
}

/** Standard Webhooks headers of a call (scheme v1): HMAC-SHA256 over ``id.timestamp.body``. */
function signCall(secret, body, messageId = idempotencyKey()) {
  const timestamp = String(Math.floor(Date.now() / 1000));
  const key = Buffer.from(secret.slice("whsec_".length), "base64");
  const digest = createHmac("sha256", key).update(`${messageId}.${timestamp}.${body}`).digest("base64");
  return {
    "content-type": "application/json",
    "webhook-id": messageId,
    "webhook-timestamp": timestamp,
    "webhook-signature": `v1,${digest}`,
  };
}

function errorCode(answer) {
  return answer?.error?.code ?? null;
}

function contentVariables(text) {
  const parsed = parseJson(text ?? "");
  return typeof parsed === "object" && parsed !== null ? parsed : {};
}

/** The ``rendered`` member of a deep-link call: a text Veriqa rendered for the link. */
function renderedPrefill(text) {
  return { text, locale: "en" };
}

// ── The own checks ──────────────────────────────────────────────────────────

class OwnChecks {
  constructor() {
    this.stubUrl = env("STAND_STUB_URL");
    this.adapterUrl = env("STAND_ADAPTER_URL").replace(/\/+$/, "");
    this.tenantsUrl = env("STAND_TENANTS_ADAPTER_URL").replace(/\/+$/, "");
    this.user = env("STAND_USER");
    this.transportSecret = env("STAND_TRANSPORT_SECRET");
    this.defaultSender = digitsOf(env("STAND_DEFAULT_FROM"));
    this.defaultToken = env("STAND_DEFAULT_TOKEN");
    this.shopASender = digitsOf(env("STAND_SHOP_A_FROM"));
    this.shopAToken = env("STAND_SHOP_A_TOKEN");
    this.shopBToken = env("STAND_SHOP_B_TOKEN");
    // adapter_settings with a marker of the run: the step logs.no-secrets of run.sh looks for it in the logs.
    this.markedSettings = JSON.stringify({ stand_marker: env("STAND_SETTINGS_MARKER") });
    this.shopAWebhook = this.webhookOf(this.shopASender);
    this.defaultWebhook = this.webhookOf(this.defaultSender);
    this.eventsPath = `/registrar/api/channels/${CHANNEL_TYPE}/webhook`;
  }

  async run() {
    const checks = [
      ["inbound.start", () => this.inboundStart()],
      ["inbound.no-signature", () => this.inboundNoSignature()],
      ["inbound.wrong-signature", () => this.inboundWrongSignature()],
      ["inbound.foreign", () => this.inboundForeign()],
      ["settings.prefill-text", () => this.settingsPrefillText()],
      ["deep-link.rendered-text", () => this.deepLinkRenderedText()],
      ["deep-link.rendered-text-without-code", () => this.deepLinkRenderedTextWithoutCode()],
      ["deep-link.per-purpose", () => this.deepLinkPerPurpose()],
      ["deep-link.rendered-null", () => this.deepLinkRenderedNull()],
      ["deep-link.unknown-purpose-is-button", () => this.deepLinkUnknownPurposeIsButton()],
      ["platform.create-63016", () => this.platformCreate63016()],
      ["idempotency.send-message-once", () => this.idempotencySendMessageOnce()],
      ["idempotency.report-outcome-once", () => this.idempotencyReportOutcomeOnce()],
      ["idempotency.failure-unbinds", () => this.idempotencyFailureUnbinds()],
      ["tenant.fallback", () => this.tenantFallback()],
      ["tenant.unknown", () => this.tenantUnknown()],
      ["tenant.event-route", () => this.tenantEventRoute()],
      ["wire.channel-type", () => this.wireChannelType()],
      ["wire.body-limit", () => this.wireBodyLimit()],
      ["platform.create-63016-others", () => this.platformCreate63016Others()],
      ["status.other-failure", () => this.statusOtherFailure()],
      ["report-outcome.new-message", () => this.reportOutcomeNewMessage()],
      ["send-prompt.limits", () => this.sendPromptLimits()],
      ["inbound.not-e164", () => this.inboundNotE164()],
      ["inbound.twiml", () => this.inboundTwiml()],
      ["inbound.identity", () => this.inboundIdentity()],
    ];
    let passed = true;
    for (const [name, check] of checks) {
      try {
        await this.stub("POST", "/stand/clear");
        await check();
        console.log(`PASS ${name}`);
      } catch (error) {
        const reason = error instanceof CheckFailed ? error.message : `the stand did not answer (${error.name})`;
        console.log(`FAIL ${name}: ${reason}`);
        passed = false;
      }
    }
    await this.stub("POST", "/stand/clear").catch(() => {});
    return passed;
  }

  // ── The recorder ─────────────────────────────────────────────────────────

  async stub(method, path, body = undefined) {
    const headers = body === undefined ? {} : { "content-type": "application/json" };
    const { status, text } = await http(method, this.stubUrl + path, body === undefined ? undefined : JSON.stringify(body), headers);
    if (status < 200 || status >= 300) {
      throw new CheckFailed(`the stub answered ${method} ${path} with ${status}`);
    }
    return parseJson(text);
  }

  async records() {
    return this.stub("GET", "/stand/records");
  }

  async creations() {
    return (await this.records()).rest.filter((entry) => entry.resource === "create");
  }

  async expectNoEvents() {
    const { events } = await this.records();
    if (events.length > 0) {
      throw new CheckFailed(`the recorder saw ${events.length} event(s)`);
    }
  }

  async expectCreations(expected, what) {
    const creations = await this.creations();
    if (creations.length !== expected) {
      throw new CheckFailed(`${what} gave ${creations.length} creation(s) of a message, not ${expected}`);
    }
    return creations;
  }

  // ── The inbound webhook ──────────────────────────────────────────────────

  webhookOf(senderDigits) {
    return `${this.adapterUrl}${WEBHOOK_PATH_PREFIX}${senderDigits}`;
  }

  /** One message of the user of the stand to the sender ``senderDigits``, as Twilio posts it. */
  message(content, senderDigits = this.shopASender) {
    return {
      MessageSid: newMessageSid(),
      From: whatsAppAddress(this.user),
      To: whatsAppAddress(`+${senderDigits}`),
      ProfileName: "Stand User",
      NumMedia: "0",
      ...content,
    };
  }

  /** Posts a message to the webhook of shop-a signed with ``token``, or unsigned when it is ``null``. */
  async post(parameters, token) {
    const signature = token === null ? null : twilioSignature(token, this.shopAWebhook, parameters);
    return postWebhook(this.shopAWebhook, parameters, signature);
  }

  /** Posts a message to ``webhook`` signed with ``token``; it must be taken with 200. */
  async postSigned(webhook, parameters, token) {
    const status = await postWebhook(webhook, parameters, twilioSignature(token, webhook, parameters));
    if (status !== 200) {
      throw new CheckFailed(`a signed message was answered ${status}`);
    }
  }

  // ── Calls of Veriqa ──────────────────────────────────────────────────────

  /** A call of Veriqa signed with the transport secret of the stand; its answer, which must be a 200. */
  async call(operation, payload, { url = this.adapterUrl, tenant = null, settings = this.markedSettings, channelType = CHANNEL_TYPE, key } = {}) {
    const body = JSON.stringify({
      version: WIRE_VERSION,
      operation,
      channel_type: channelType,
      tenant,
      adapter_settings: settings,
      payload,
    });
    const { status, text } = await http("POST", `${url}/`, body, signCall(this.transportSecret, body, key));
    if (status !== 200) {
      throw new CheckFailed(`the ${operation} call was answered ${status}`);
    }
    const answer = parseJson(text);
    if (typeof answer !== "object" || answer === null) {
      throw new CheckFailed(`the ${operation} call was answered with no envelope`);
    }
    return answer;
  }

  static result(answer, operation) {
    if (typeof answer.result !== "object" || answer.result === null) {
      throw new CheckFailed(`the ${operation} call was answered ${JSON.stringify(answer.error ?? null)} instead of a result`);
    }
    return answer.result;
  }

  sendMessage(options = {}) {
    return this.call("send-message", { channel_user_id: this.user, text: "A message of the stand", kind: "plain_text" }, options);
  }

  sendPrompt(transaction, { text = PROMPT_TEXT, confirmLabel = CONFIRM_LABEL, declineLabel = DECLINE_LABEL, ...options } = {}) {
    return this.call(
      "send-prompt",
      {
        transaction_id: transaction,
        channel_user_id: this.user,
        prompt: { variant: "suppressed", reason: "display_disabled_by_configuration", ownership: {}, recipient_locale: null, recipient_time_zone: null },
        rendered: { text, confirm_label: confirmLabel, decline_label: declineLabel, locale: "en" },
      },
      options,
    );
  }

  reportOutcome(transaction, { replace = false, promptMessage = null, ...options } = {}) {
    return this.call(
      "report-outcome",
      {
        transaction_id: transaction,
        outcome: "confirmed",
        channel_user_id: this.user,
        status_text: STATUS_TEXT,
        inbound_token: null,
        prompt_message: promptMessage,
        display_intent: replace ? "replace_prompt" : "new_message",
      },
      options,
    );
  }

  async deepLinkUrl(transaction, { purpose, rendered, ...options } = {}) {
    const payload = { transaction_id: transaction };
    if (purpose !== undefined) {
      payload.purpose = purpose;
    }
    if (rendered !== undefined) {
      payload.rendered = rendered;
    }
    const url = OwnChecks.result(await this.call("deep-link", payload, options), "deep-link").url;
    if (typeof url !== "string") {
      throw new CheckFailed(`the deep link of tenant ${options.tenant ?? null} carried no url`);
    }
    return new URL(url);
  }

  // ── The inbound checks ───────────────────────────────────────────────────

  async inboundStart() {
    const transaction = transactionId();
    const parameters = this.message({ Body: `${DEFAULT_PREFILL_TEXT}\n${AUTH_MARKER}${transaction}`, MessageType: "text" });
    // The same message twice, as Twilio redelivers it, then the same text under another MessageSid.
    for (const message of [parameters, parameters, { ...parameters, MessageSid: newMessageSid() }]) {
      const status = await this.post(message, this.shopAToken);
      if (status !== 200) {
        throw new CheckFailed(`a signed message was answered ${status}`);
      }
    }
    const { events } = await this.records();
    const route = `${this.eventsPath}/${SHOP_A}`;
    const seen = events.map((event) => [event.path, event.body.event, event.body.transaction_id]);
    const expected = Array(3).fill([route, "auth_start", transaction]);
    if (JSON.stringify(seen) !== JSON.stringify(expected)) {
      throw new CheckFailed(`expected three auth_start of the transaction on ${route}, the recorder saw ${JSON.stringify(seen)}`);
    }
    const [first, repeat, other] = events.map((event) => event.webhook_id);
    if (!first || first !== repeat) {
      throw new CheckFailed(`one MessageSid delivered twice gave the webhook-ids ${first} and ${repeat}`);
    }
    if (other === first) {
      throw new CheckFailed("two MessageSids gave one webhook-id");
    }
    if (events[0].body.identity?.channel_user_id !== this.user) {
      throw new CheckFailed("the identity of the event is not the number of the sender of the message");
    }
  }

  async inboundNoSignature() {
    const status = await this.post(this.message({ Body: `${AUTH_MARKER}${transactionId()}`, MessageType: "text" }), null);
    if (status !== 403) {
      throw new CheckFailed(`a message without a signature was answered ${status}`);
    }
    await this.expectNoEvents();
  }

  async inboundWrongSignature() {
    // The Auth Token of another account of the stand, and one nobody holds.
    for (const token of [this.shopBToken, randomBytes(16).toString("hex")]) {
      const status = await this.post(this.message({ Body: `${AUTH_MARKER}${transactionId()}`, MessageType: "text" }), token);
      if (status !== 403) {
        throw new CheckFailed(`a message signed with a wrong Auth Token was answered ${status}`);
      }
    }
    await this.expectNoEvents();
  }

  /** The id of the confirm button of a prompt the adapter sent to the stub, and the transaction it carries. */
  async confirmButtonId() {
    const transaction = transactionId();
    OwnChecks.result(await this.sendPrompt(transaction), "send-prompt");
    const creations = await this.creations();
    const id = String(contentVariables(creations.at(-1)?.form?.ContentVariables)[CONFIRM_ID_VARIABLE] ?? "");
    if (!id.endsWith(transaction) || id.length === transaction.length) {
      throw new CheckFailed(`the confirm button of the prompt has the id ${JSON.stringify(id)}, which does not end with its transaction`);
    }
    return { id, transaction };
  }

  async inboundForeign() {
    const button = await this.confirmButtonId();
    const confirmPrefix = button.id.slice(0, button.id.length - button.transaction.length);
    await this.stub("POST", "/stand/clear");
    const messages = [
      this.message({ Body: "Hello", MessageType: "text" }),
      this.message({ Body: "Buy", ButtonText: "Buy", ButtonPayload: "shop_item_42", MessageType: "button" }),
      this.message({ Body: `${AUTH_MARKER}${transactionId().slice(1)}`, MessageType: "text" }),
      this.message({ Body: CONFIRM_LABEL, ButtonText: CONFIRM_LABEL, ButtonPayload: `${confirmPrefix}not-a-transaction`, MessageType: "button" }),
      this.message({ NumMedia: "1", MessageType: "image" }),
    ];
    for (const parameters of messages) {
      const status = await this.post(parameters, this.shopAToken);
      if (status !== 200) {
        throw new CheckFailed(`a signed message ${parameters.MessageType} was answered ${status}`);
      }
    }
    await this.expectNoEvents();
  }

  async settingsPrefillText() {
    const custom = "Sign in to the stand.";
    for (const [settings, opening] of [[JSON.stringify({ prefill_text: custom }), custom], [null, DEFAULT_PREFILL_TEXT]]) {
      const transaction = transactionId();
      const url = await this.deepLinkUrl(transaction, { settings });
      const text = url.searchParams.get("text") ?? "";
      if (!text.startsWith(opening) || !text.includes(`${AUTH_MARKER}${transaction}`)) {
        throw new CheckFailed(`deep-link with adapter_settings ${settings} gave a text that does not start with "${opening}" or lacks the code`);
      }
    }
  }

  /** A text Veriqa rendered with the code in it goes into the link as it is: neither prefill_text nor a second code. */
  async deepLinkRenderedText() {
    const transaction = transactionId();
    const rendered = `Sign in to the stand from Veriqa.\n\n${AUTH_MARKER}${transaction}`;
    const settings = JSON.stringify({ prefill_text: "Sign in to the stand." });
    const url = await this.deepLinkUrl(transaction, { settings, rendered: renderedPrefill(rendered) });
    const text = url.searchParams.get("text") ?? "";
    if (text !== rendered) {
      throw new CheckFailed(`deep-link with a rendered text that has the code gave ${JSON.stringify(text)}, not the rendered text as it is`);
    }
  }

  /**
   * A text Veriqa rendered without the code goes into the link instead of prefill_text and gets the code on a
   * last line of its own. Both texts are synthetic and differ from DEFAULT_PREFILL_TEXT, so an adapter that
   * ignores rendered cannot pass by its fallback.
   */
  async deepLinkRenderedTextWithoutCode() {
    const transaction = transactionId();
    const rendered = "Open the chat to sign in 9b4d.";
    const settings = JSON.stringify({ prefill_text: "Sign in to the stand 9b4d." });
    const url = await this.deepLinkUrl(transaction, { settings, rendered: renderedPrefill(rendered) });
    const text = url.searchParams.get("text") ?? "";
    const expected = `${rendered}\n${AUTH_MARKER}${transaction}`;
    if (text !== expected) {
      throw new CheckFailed(`deep-link with a rendered text without the code gave ${JSON.stringify(text)}, not ${JSON.stringify(expected)}`);
    }
  }

  /** The links of one transaction are kept per purpose: each purpose gets the link of its text, a repeat the kept link. */
  async deepLinkPerPurpose() {
    const transaction = transactionId();
    const button = await this.deepLinkUrl(transaction, { purpose: "button", rendered: renderedPrefill("Tap to sign in.") });
    const directQr = await this.deepLinkUrl(transaction, { purpose: "direct_qr", rendered: renderedPrefill("Scan to sign in.") });
    const repeat = await this.deepLinkUrl(transaction, { purpose: "button", rendered: renderedPrefill("Tap here to sign in.") });
    for (const [purpose, link, opening] of [["button", button, "Tap to sign in."], ["direct_qr", directQr, "Scan to sign in."]]) {
      const text = link.searchParams.get("text") ?? "";
      const expected = `${opening}\n${AUTH_MARKER}${transaction}`;
      if (text !== expected) {
        throw new CheckFailed(`the ${purpose} link of a transaction that has links of two purposes gave ${JSON.stringify(text)}, not ${JSON.stringify(expected)}`);
      }
    }
    if (repeat.href !== button.href) {
      throw new CheckFailed("a repeated deep-link call with the same purpose gave another link");
    }
  }

  /** A deep-link call whose rendered is null takes prefill_text, and the code on a last line of its own. */
  async deepLinkRenderedNull() {
    const transaction = transactionId();
    // Synthetic text of the setting of the adapter.
    const custom = "Sign in to the stand 2e7a.";
    const url = await this.deepLinkUrl(transaction, { settings: JSON.stringify({ prefill_text: custom }), rendered: null });
    const text = url.searchParams.get("text") ?? "";
    const expected = `${custom}\n${AUTH_MARKER}${transaction}`;
    if (text !== expected) {
      throw new CheckFailed(`deep-link with rendered null gave ${JSON.stringify(text)}, not ${JSON.stringify(expected)}`);
    }
  }

  /** A purpose the adapter does not know, and a call without one, are read as button: the kept button link. */
  async deepLinkUnknownPurposeIsButton() {
    const transaction = transactionId();
    // Synthetic texts; "carousel" is a synthetic purpose nobody defines.
    const button = await this.deepLinkUrl(transaction, { purpose: "button", rendered: renderedPrefill("Tap 5d1f.") });
    const unknown = await this.deepLinkUrl(transaction, { purpose: "carousel", rendered: renderedPrefill("Other 5d1f.") });
    const without = await this.deepLinkUrl(transaction);
    for (const [label, link] of [["the purpose carousel", unknown], ["no purpose", without]]) {
      if (link.href !== button.href) {
        throw new CheckFailed(`deep-link with ${label} gave ${link.href}, not the button link ${button.href}`);
      }
    }
  }

  async platformCreate63016() {
    await this.stub("POST", "/stand/refuse-create");
    const answer = await this.sendPrompt(transactionId());
    if (errorCode(answer) !== CHANNEL_CANNOT_CONTINUE) {
      throw new CheckFailed(`send-prompt was answered ${JSON.stringify(errorCode(answer) ?? answer?.result ?? null)} instead of ${CHANNEL_CANNOT_CONTINUE}`);
    }
    const { rest } = await this.records();
    const creations = rest.filter((entry) => entry.resource === "create").length;
    const statusReads = rest.filter((entry) => entry.resource === "message").length;
    if (creations !== 1 || statusReads !== 0) {
      throw new CheckFailed(`expected one creation and no read of the status, the stub saw ${creations} and ${statusReads}`);
    }
  }

  // ── The side of Veriqa: idempotency ──────────────────────────────────────

  async idempotencySendMessageOnce() {
    const key = idempotencyKey();
    const first = OwnChecks.result(await this.sendMessage({ key }), "send-message");
    const second = OwnChecks.result(await this.sendMessage({ key }), "send-message");
    if (JSON.stringify(first) !== JSON.stringify(second)) {
      throw new CheckFailed(`the repeat under the same key was answered ${JSON.stringify(second)}, the first call ${JSON.stringify(first)}`);
    }
    await this.expectCreations(1, "two send-message calls under one key");
  }

  async idempotencyReportOutcomeOnce() {
    const key = idempotencyKey();
    const transaction = transactionId();
    const first = OwnChecks.result(await this.reportOutcome(transaction, { key }), "report-outcome");
    const second = OwnChecks.result(await this.reportOutcome(transaction, { key }), "report-outcome");
    if (JSON.stringify(first) !== JSON.stringify(second)) {
      throw new CheckFailed(`the repeat under the same key was answered ${JSON.stringify(second)}, the first call ${JSON.stringify(first)}`);
    }
    await this.expectCreations(1, "two report-outcome calls under one key");
  }

  async idempotencyFailureUnbinds() {
    const key = idempotencyKey();
    await this.stub("POST", "/stand/refuse-create");
    const refused = await this.sendMessage({ key });
    if (refused.result !== undefined || errorCode(refused) === null) {
      throw new CheckFailed(`a send-message the platform refused was answered ${JSON.stringify(refused)}, not an error`);
    }
    await this.stub("POST", "/stand/clear");
    OwnChecks.result(await this.sendMessage({ key }), "send-message");
    await this.expectCreations(1, "the repeat after a failed attempt");
  }

  // ── The side of Veriqa: tenants ──────────────────────────────────────────

  async tenantFallback() {
    const url = await this.deepLinkUrl(transactionId(), { tenant: TENANT_WITHOUT_RECORD });
    if (url.hostname !== DEEP_LINK_HOST || url.pathname !== `/${this.defaultSender}`) {
      throw new CheckFailed(`a tenant without a record got a link to ${url.hostname}${url.pathname}, not to the sender of the default record`);
    }
  }

  async tenantUnknown() {
    const link = await this.call("deep-link", { transaction_id: transactionId() }, { url: this.tenantsUrl, tenant: TENANT_WITHOUT_RECORD });
    if (errorCode(link) !== DEEP_LINK_GENERATION_FAILED) {
      throw new CheckFailed(`deep-link of a tenant without any record was answered ${JSON.stringify(link)}, not ${DEEP_LINK_GENERATION_FAILED}`);
    }
    const message = await this.sendMessage({ url: this.tenantsUrl, tenant: TENANT_WITHOUT_RECORD });
    if (errorCode(message) !== CHANNEL_NOT_AVAILABLE) {
      throw new CheckFailed(`send-message of a tenant without any record was answered ${JSON.stringify(message)}, not ${CHANNEL_NOT_AVAILABLE}`);
    }
    await this.expectCreations(0, "the calls of a tenant without any record");
  }

  async tenantEventRoute() {
    const transaction = transactionId();
    const text = (await this.deepLinkUrl(transaction, { tenant: SHOP_B })).searchParams.get("text") ?? "";
    await this.postSigned(this.defaultWebhook, this.message({ Body: text, MessageType: "text" }, this.defaultSender), this.defaultToken);
    await this.expectRoute(transaction, SHOP_B);
  }

  async expectRoute(transaction, tenant) {
    const { events } = await this.records();
    const route = `${this.eventsPath}/${tenant}`;
    const seen = events.map((event) => [event.path, event.body.event, event.body.transaction_id]);
    if (JSON.stringify(seen) !== JSON.stringify([[route, "auth_start", transaction]])) {
      throw new CheckFailed(`expected one auth_start of the transaction on ${route}, the recorder saw ${JSON.stringify(seen)}`);
    }
  }

  // ── The side of Veriqa: the envelope ─────────────────────────────────────

  async wireChannelType() {
    const answer = await this.sendMessage({ channelType: "other-channel" });
    if (errorCode(answer) !== CHANNEL_NOT_AVAILABLE) {
      throw new CheckFailed(`a call of another channel type was answered ${JSON.stringify(answer)}, not ${CHANNEL_NOT_AVAILABLE}`);
    }
    await this.expectCreations(0, "a call of another channel type");
  }

  async wireBodyLimit() {
    const body = JSON.stringify({
      version: WIRE_VERSION,
      operation: "send-message",
      channel_type: CHANNEL_TYPE,
      tenant: null,
      adapter_settings: null,
      payload: { channel_user_id: this.user, text: "a".repeat(OVERSIZED_TEXT_LENGTH), kind: "plain_text" },
    });
    let answer = null;
    try {
      const { status, text } = await http("POST", `${this.adapterUrl}/`, body, signCall(this.transportSecret, body));
      answer = status === 200 ? parseJson(text) : null;
    } catch {
      // The adapter may close the connection without reading the rest of the body: a refusal as well.
    }
    if (answer?.result !== undefined && answer?.result !== null) {
      throw new CheckFailed("a call over 1 MB was answered 200 with a result");
    }
    await this.expectCreations(0, "a call over 1 MB");
  }

  // ── The side of Twilio ───────────────────────────────────────────────────

  async platformCreate63016Others() {
    await this.stub("POST", "/stand/refuse-create");
    const message = await this.sendMessage();
    const outcome = await this.reportOutcome(transactionId());
    for (const [operation, answer] of [["send-message", message], ["report-outcome", outcome]]) {
      if (errorCode(answer) !== CHANNEL_CANNOT_CONTINUE) {
        throw new CheckFailed(`${operation} refused with 63016 was answered ${JSON.stringify(answer)}, not ${CHANNEL_CANNOT_CONTINUE}`);
      }
    }
  }

  async statusOtherFailure() {
    await this.stub("POST", "/stand/fail-status", { error_code: OTHER_FAILURE_CODE });
    const answer = await this.sendPrompt(transactionId());
    if (errorCode(answer) !== MESSAGE_SEND_FAILED) {
      throw new CheckFailed(`send-prompt of a message that failed with ${OTHER_FAILURE_CODE} was answered ${JSON.stringify(answer)}, not ${MESSAGE_SEND_FAILED}`);
    }
  }

  async reportOutcomeNewMessage() {
    const transaction = transactionId();
    const prompt = OwnChecks.result(await this.sendPrompt(transaction), "send-prompt").message;
    if (typeof prompt !== "object" || prompt === null) {
      throw new CheckFailed("send-prompt gave no message to replace");
    }
    await this.stub("POST", "/stand/clear");
    OwnChecks.result(await this.reportOutcome(transaction, { replace: true, promptMessage: prompt }), "report-outcome");
    const creations = await this.expectCreations(1, "report-outcome that asks to replace the prompt");
    if (creations[0].form?.Body !== STATUS_TEXT) {
      throw new CheckFailed(`the new message has the body ${JSON.stringify(creations[0].form?.Body)}, not the status text`);
    }
  }

  async sendPromptLimits() {
    const tooLong = await this.sendPrompt(transactionId(), { text: "a".repeat(PROMPT_TEXT_LIMIT + 1) });
    if (errorCode(tooLong) !== MESSAGE_SEND_FAILED) {
      throw new CheckFailed(`a prompt of ${PROMPT_TEXT_LIMIT + 1} characters was answered ${JSON.stringify(tooLong)}, not ${MESSAGE_SEND_FAILED}`);
    }
    await this.expectCreations(0, `a prompt of ${PROMPT_TEXT_LIMIT + 1} characters`);

    const confirmLabel = "C".repeat(LONG_LABEL_LENGTH);
    const declineLabel = "D".repeat(LONG_LABEL_LENGTH);
    OwnChecks.result(await this.sendPrompt(transactionId(), { confirmLabel, declineLabel }), "send-prompt");
    const [creation] = await this.expectCreations(1, "a prompt with long labels");
    const variables = contentVariables(creation.form?.ContentVariables);
    const titles = [variables[CONFIRM_TITLE_VARIABLE], variables[DECLINE_TITLE_VARIABLE]];
    const expected = [confirmLabel.slice(0, BUTTON_TITLE_LIMIT), declineLabel.slice(0, BUTTON_TITLE_LIMIT)];
    if (JSON.stringify(titles) !== JSON.stringify(expected)) {
      throw new CheckFailed(`labels of ${LONG_LABEL_LENGTH} characters gave the titles ${JSON.stringify(titles)}, not their first ${BUTTON_TITLE_LIMIT} characters`);
    }
  }

  async inboundNotE164() {
    const status = await this.post(this.message({ From: "whatsapp:abc", Body: `${AUTH_MARKER}${transactionId()}`, MessageType: "text" }), this.shopAToken);
    if (status !== 200) {
      throw new CheckFailed(`a signed message from an address that is not a number was answered ${status}`);
    }
    await this.expectNoEvents();
  }

  async inboundTwiml() {
    const parameters = this.message({ Body: "Hello", MessageType: "text" });
    const response = await fetch(this.shopAWebhook, {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded", [SIGNATURE_HEADER]: twilioSignature(this.shopAToken, this.shopAWebhook, parameters) },
      body: new URLSearchParams(parameters).toString(),
      redirect: "manual",
      signal: AbortSignal.timeout(DELIVERY_TIMEOUT_MS),
    });
    const text = await response.text();
    const contentType = response.headers.get("content-type") ?? "";
    if (response.status !== 200 || !EMPTY_TWIML.test(text) || !contentType.startsWith(TWIML_CONTENT_TYPE)) {
      throw new CheckFailed(`a signed message was answered ${response.status}, ${JSON.stringify(contentType)}, ${JSON.stringify(text)} — not 200 with an empty TwiML document`);
    }
  }

  async inboundIdentity() {
    for (const withName of [true, false]) {
      await this.stub("POST", "/stand/clear");
      const transaction = transactionId();
      const parameters = this.message({ Body: `${AUTH_MARKER}${transaction}`, MessageType: "text" });
      if (!withName) {
        delete parameters.ProfileName;
      }
      await this.postSigned(this.shopAWebhook, parameters, this.shopAToken);
      const { events } = await this.records();
      const identity = events.find((event) => event.body.transaction_id === transaction)?.body.identity;
      if (identity === undefined) {
        throw new CheckFailed(`the message ${withName ? "with" : "without"} a profile name gave no event`);
      }
      if (identity.phone_number !== this.user) {
        throw new CheckFailed(`identity.phone_number is ${JSON.stringify(identity.phone_number)}, not the number of the sender`);
      }
      const expectedName = withName ? parameters.ProfileName : this.user;
      if (identity.display_name !== expectedName) {
        throw new CheckFailed(`the message ${withName ? "with" : "without"} a profile name gave the display name ${JSON.stringify(identity.display_name)}, not ${JSON.stringify(expectedName)}`);
      }
    }
  }

  // ── The state across a recreation of the container ───────────────────────

  /** Phase 1: a message under ``key`` and the deep link of ``transaction`` for shop-b; what phase 2 needs. */
  async surviveBefore(key, transaction) {
    await this.stub("POST", "/stand/clear");
    const result = OwnChecks.result(await this.sendMessage({ key }), "send-message");
    const text = (await this.deepLinkUrl(transaction, { tenant: SHOP_B })).searchParams.get("text") ?? "";
    return { result, text };
  }

  /** Phase 2: the repeat under ``key`` and the transaction brought in on the webhook of the default record. */
  async surviveAfter(key, transaction, before) {
    const second = OwnChecks.result(await this.sendMessage({ key }), "send-message");
    if (JSON.stringify(second) !== JSON.stringify(before.result)) {
      throw new CheckFailed(`the repeat after the recreation was answered ${JSON.stringify(second)}, the first call ${JSON.stringify(before.result)}`);
    }
    await this.expectCreations(1, "the call before and its repeat after the recreation");
    await this.postSigned(this.defaultWebhook, this.message({ Body: before.text, MessageType: "text" }, this.defaultSender), this.defaultToken);
    await this.expectRoute(transaction, SHOP_B);
  }
}

// ── Waiting and the report ──────────────────────────────────────────────────

/** Whether the stub and every adapter answer; an adapter answers an unsigned call with 403. */
async function standAnswers(stubUrl, adapterUrls) {
  try {
    if ((await http("GET", `${stubUrl}/conformance/platform-log`)).status !== 200) {
      return false;
    }
    for (const url of adapterUrls) {
      if ((await http("POST", url, "")).status !== 403) {
        return false;
      }
    }
    return true;
  } catch {
    return false;
  }
}

async function waitStand(seconds) {
  const stubUrl = env("STAND_STUB_URL");
  const adapterUrls = env("STAND_ADAPTER_URLS").split(",");
  const deadline = Date.now() + seconds * 1000;
  while (Date.now() < deadline) {
    if (await standAnswers(stubUrl, adapterUrls)) {
      return true;
    }
    await sleep(POLL_MS);
  }
  console.error(`checks: the stub and the adapters did not all answer within ${seconds} s`);
  return false;
}

function suiteReport(document) {
  const report = parseJson(document);
  if (report === null || !Array.isArray(report.scenarios)) {
    console.error("checks: the report of the suite is not a report");
    return false;
  }
  const problems = [];
  if (report.outcome !== "passed") {
    problems.push(`outcome is ${JSON.stringify(report.outcome)}`);
  }
  const ids = new Set(report.scenarios.map((scenario) => scenario.id));
  for (const id of REQUIRED_SCENARIOS.filter((required) => !ids.has(required))) {
    problems.push(`${id} is missing`);
  }
  for (const scenario of report.scenarios) {
    if (scenario.status !== "passed") {
      problems.push(`${scenario.id} is ${JSON.stringify(scenario.status)} (${scenario.reason}), expected passed`);
    }
  }
  for (const problem of problems) {
    console.error(`checks: ${problem}`);
  }
  return problems.length === 0;
}

async function channelState(url, channel) {
  try {
    const { status, text } = await http("GET", url);
    const report = parseJson(text);
    for (const check of status === 200 && Array.isArray(report?.checks) ? report.checks : []) {
      const value = check?.data?.[channel];
      if (value !== undefined) {
        return value;
      }
    }
  } catch {
    // Not up yet.
  }
  return null;
}

async function waitChannel(url, channel, seconds) {
  const deadline = Date.now() + seconds * 1000;
  let value = null;
  while (Date.now() < deadline) {
    value = await channelState(url, channel);
    if (value === "healthy") {
      return true;
    }
    await sleep(POLL_MS);
  }
  console.error(`checks: ${channel} is ${value ?? "not reported"} after ${seconds} s`);
  return false;
}

/** The phases of state.survives-recreate: 0 when the phase held, 1 when it did not. */
async function survive([command, key, transaction, before]) {
  const checks = new OwnChecks();
  try {
    if (command === "survive-before") {
      console.log(JSON.stringify(await checks.surviveBefore(key, transaction)));
    } else {
      await checks.surviveAfter(key, transaction, JSON.parse(before));
    }
  } catch (error) {
    if (!(error instanceof CheckFailed)) {
      throw error;
    }
    console.error(`checks: ${error.message}`);
    return 1;
  }
  return 0;
}

async function main(args) {
  const [command] = args;
  try {
    if (command === "own" && args.length === 1) {
      return (await new OwnChecks().run()) ? 0 : 1;
    }
    if ((command === "survive-before" && args.length === 3) || (command === "survive-after" && args.length === 4)) {
      return await survive(args);
    }
    if (command === "wait-stand" && args.length === 2) {
      return (await waitStand(Number(args[1]))) ? 0 : 1;
    }
    if (command === "suite-report" && args.length === 1) {
      return suiteReport(readFileSync(0, "utf8")) ? 0 : 1;
    }
    if (command === "wait-channel" && args.length === 4) {
      return (await waitChannel(args[1], args[2], Number(args[3]))) ? 0 : 1;
    }
  } catch (error) {
    console.error(`checks: ${error instanceof SetupError || error instanceof CheckFailed ? error.message : error.name}`);
    return 2;
  }
  console.error(
    "usage: checks.mjs own | survive-before <key> <transaction> | survive-after <key> <transaction> <first> "
      + "| wait-stand <seconds> | suite-report | wait-channel <url> <channel> <seconds>",
  );
  return 2;
}

process.exitCode = await main(process.argv.slice(2));
