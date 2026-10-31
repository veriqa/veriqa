// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Checks of the stand that the conformance suite does not see, run inside the network of the stand.
//
// Subcommands, all driven by run.sh:
//
// - own — the checks of the adapter itself: the messages that are events and those that are not, the first
//   vote of a poll, the own setting of the adapter and the text of a deep link Veriqa rendered, the health and
//   the sendings of a record whose session is not in order, the service path of the stub mode closed in the
//   baileys mode, the rules of the side of Veriqa (idempotency keys, credentials per tenant, the route of an
//   event, the envelope) and the shape of what the adapter sends. Each prints "PASS <check>" or "FAIL <check>:
//   <reason>"; what counts is what the recorder of the stub saw, not only the status the adapter answered;
// - survive-before <key> <transaction> and survive-after <key> <transaction> <first> — the two phases of the
//   step state.survives-recreate of run.sh, around a recreation of the container of the adapter of the
//   checks. The first sends a message under the idempotency key and asks a deep link of the transaction for
//   the tenant shop-b, and prints what the second needs; the second repeats the message and brings the
//   transaction in on the default record;
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

import { Chat, INBOUND_PATH, digitsOf, newMessageId, postInbound, textMessage, voteMessage } from "./whatsapp.mjs";

const CHANNEL_TYPE = "whatsapp-baileys";
const WIRE_VERSION = "1.0";
const REQUEST_TIMEOUT_MS = 30_000;
const POLL_MS = 2_000;

// Markers of Veriqa and the defaults of the adapter the checks expect.
const AUTH_MARKER = "auth_";
const DEFAULT_PREFILL_TEXT = "Send this message to sign in.";

// The states of a record as the details of health name them, and as the switch of the stub sets them.
const NOT_PAIRED = "not paired";
const NOT_CONNECTED = "not connected";
const RecordState = Object.freeze({ NOT_PAIRED: "not_paired", NOT_CONNECTED: "not_connected" });

// Error codes of the wire the checks expect.
const CHANNEL_NOT_AVAILABLE = "channel_not_available";
const DEEP_LINK_GENERATION_FAILED = "deep_link_generation_failed";
const MESSAGE_SEND_FAILED = "message_send_failed";

// The deep link host of WhatsApp.
const DEEP_LINK_HOST = "wa.me";

// Tenants of the stand: two with records of their own, one with none anywhere.
const SHOP_A = "shop-a";
const SHOP_B = "shop-b";
const TENANT_WITHOUT_RECORD = "shop-z";

// The labels and the texts of a prompt the checks send.
const CONFIRM_LABEL = "Confirm";
const DECLINE_LABEL = "Decline";
const STATUS_TEXT = "You are signed in to the stand.";

// A sender WhatsApp addresses by a LID, with no phone number beside it.
const LID_SENDER = "123@lid";

// How long the refused deliveries of one event must stay unchanged before the adapter counts as having given
// up — longer than the longest pause between its attempts — and the bound of that wait, in milliseconds.
const DELIVERY_SETTLED_MS = 4_000;
const DELIVERY_GIVE_UP_MS = 120_000;
const DELIVERY_LOOK_MS = 500;

// A body of a call over the limit of 1 MB the adapter must refuse.
const OVERSIZED_TEXT_LENGTH = 1_100_000;

// The scenario of the suite that has no case on this platform, and the ones the gate names on their own:
// each must be in the report — the first not applicable, every other one passed.
const NOT_APPLICABLE_SCENARIO = "platform.cannot-continue";
const REQUIRED_SCENARIOS = [NOT_APPLICABLE_SCENARIO, "platform.credentials-by-tenant", "events.all-valid"];

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
    this.baileysUrl = env("STAND_BAILEYS_ADAPTER_URL").replace(/\/+$/, "");
    this.baileysLabel = env("STAND_BAILEYS_LABEL");
    this.user = env("STAND_USER");
    this.stranger = env("STAND_STRANGER");
    this.transportSecret = env("STAND_TRANSPORT_SECRET");
    this.defaultLabel = env("STAND_DEFAULT_LABEL");
    this.defaultNumber = env("STAND_DEFAULT_NUMBER");
    this.shopALabel = env("STAND_SHOP_A_LABEL");
    this.shopBLabel = env("STAND_SHOP_B_LABEL");
    // adapter_settings with a marker of the run: the step logs.no-secrets of run.sh looks for it in the logs.
    this.markedSettings = JSON.stringify({ stand_marker: env("STAND_SETTINGS_MARKER") });
    this.eventsPath = `/registrar/api/channels/${CHANNEL_TYPE}/webhook`;
  }

  async run() {
    const checks = [
      ["inbound.start", () => this.inboundStart()],
      ["inbound.vote-once", () => this.inboundVoteOnce()],
      ["inbound.foreign", () => this.inboundForeign()],
      ["settings.prefill-text", () => this.settingsPrefillText()],
      ["deep-link.rendered-text", () => this.deepLinkRenderedText()],
      ["deep-link.rendered-text-without-code", () => this.deepLinkRenderedTextWithoutCode()],
      ["deep-link.per-purpose", () => this.deepLinkPerPurpose()],
      ["deep-link.rendered-null", () => this.deepLinkRenderedNull()],
      ["deep-link.unknown-purpose-is-button", () => this.deepLinkUnknownPurposeIsButton()],
      ["health.unpaired", () => this.healthUnpaired()],
      ["stub-path.closed", () => this.stubPathClosed()],
      ["idempotency.send-message-once", () => this.idempotencySendMessageOnce()],
      ["idempotency.report-outcome-once", () => this.idempotencyReportOutcomeOnce()],
      ["idempotency.failure-unbinds", () => this.idempotencyFailureUnbinds()],
      ["tenant.fallback", () => this.tenantFallback()],
      ["tenant.unknown", () => this.tenantUnknown()],
      ["tenant.event-route", () => this.tenantEventRoute()],
      ["wire.channel-type", () => this.wireChannelType()],
      ["wire.body-limit", () => this.wireBodyLimit()],
      ["inbound.no-number", () => this.inboundNoNumber()],
      ["send.unpaired", () => this.sendUnpaired()],
      ["health.disconnected", () => this.healthDisconnected()],
      ["send-prompt.same-labels", () => this.sendPromptSameLabels()],
      ["report-outcome.new-message", () => this.reportOutcomeNewMessage()],
      ["inbound.vote-after-failed-delivery", () => this.inboundVoteAfterFailedDelivery()],
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

  async expectNoEvents() {
    const { events } = await this.records();
    if (events.length > 0) {
      throw new CheckFailed(`the recorder saw ${events.length} event(s)`);
    }
  }

  async expectSends(expected, what, kind = undefined) {
    const sends = (await this.records()).sends.filter((entry) => kind === undefined || entry.kind === kind);
    if (sends.length !== expected) {
      throw new CheckFailed(`${what} gave ${sends.length} sending(s)${kind === undefined ? "" : ` of kind ${kind}`}, not ${expected}`);
    }
    return sends;
  }

  /** Posts one message to the service path of the adapter of the checks; it must be taken with 200. */
  async inbound(message) {
    const status = await postInbound(this.adapterUrl, message);
    if (status !== 200) {
      throw new CheckFailed(`a ${message.type} message of record ${message.credential} was answered ${status}`);
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

  sendPrompt(transaction, { confirmLabel = CONFIRM_LABEL, declineLabel = DECLINE_LABEL, ...options } = {}) {
    return this.call(
      "send-prompt",
      {
        transaction_id: transaction,
        channel_user_id: this.user,
        prompt: { variant: "suppressed", reason: "display_disabled_by_configuration", ownership: {}, recipient_locale: null, recipient_time_zone: null },
        rendered: { text: "Sign in to the stand?", confirm_label: confirmLabel, decline_label: declineLabel, locale: "en" },
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

  /** A prompt of a new transaction to the user from the default record; the transaction and the poll. */
  async prompt() {
    const transaction = transactionId();
    const answer = await this.sendPrompt(transaction);
    const pollId = answer?.result?.message?.message_id;
    if (typeof pollId !== "string") {
      throw new CheckFailed(`send-prompt was answered ${JSON.stringify(answer?.error ?? answer)} instead of a message`);
    }
    return { transaction, pollId, message: answer.result.message };
  }

  async expectOne(transaction, event, route = this.eventsPath) {
    const { events } = await this.records();
    const seen = events.map((entry) => [entry.path, entry.body.event, entry.body.transaction_id]);
    if (JSON.stringify(seen) !== JSON.stringify([[route, event, transaction]])) {
      throw new CheckFailed(`expected one ${event} of the transaction on ${route}, the recorder saw ${JSON.stringify(seen)}`);
    }
  }

  // ── The inbound checks ───────────────────────────────────────────────────

  async inboundStart() {
    const transaction = transactionId();
    const message = textMessage(this.shopALabel, this.user, `${DEFAULT_PREFILL_TEXT}\n${AUTH_MARKER}${transaction}`);
    // The same message twice, as WhatsApp may deliver it again, then its id on another record.
    for (const delivery of [message, message, { ...message, credential: this.defaultLabel }]) {
      await this.inbound(delivery);
    }
    const { events } = await this.records();
    const route = `${this.eventsPath}/${SHOP_A}`;
    const seen = events.map((event) => [event.body.event, event.body.transaction_id]);
    if (JSON.stringify(seen) !== JSON.stringify(Array(3).fill(["auth_start", transaction]))) {
      throw new CheckFailed(`expected three auth_start of the transaction, the recorder saw ${JSON.stringify(seen)}`);
    }
    if (events[0].path !== route || events[1].path !== route) {
      throw new CheckFailed(`the message of record ${this.shopALabel} went to ${events[0].path} and ${events[1].path}, not ${route}`);
    }
    const [first, repeat, other] = events.map((event) => event.webhook_id);
    if (!first || first !== repeat) {
      throw new CheckFailed(`one message delivered twice gave the webhook-ids ${first} and ${repeat}`);
    }
    if (other === first) {
      throw new CheckFailed("the same message id on two records gave one webhook-id");
    }
    if (events[0].body.identity?.channel_user_id !== this.user) {
      throw new CheckFailed("the identity of the event is not the number of the sender of the message");
    }
  }

  async inboundVoteOnce() {
    const { transaction, pollId } = await this.prompt();
    // A vote taken back before any vote, the first vote, the same vote again, and a changed vote.
    await this.inbound(voteMessage(this.defaultLabel, this.user, pollId, []));
    await this.inbound(voteMessage(this.defaultLabel, this.user, pollId, [CONFIRM_LABEL]));
    await this.inbound(voteMessage(this.defaultLabel, this.user, pollId, [CONFIRM_LABEL]));
    await this.inbound(voteMessage(this.defaultLabel, this.user, pollId, [DECLINE_LABEL]));
    await this.expectOne(transaction, "auth_confirm");
  }

  async inboundForeign() {
    const { pollId } = await this.prompt();
    const code = `${AUTH_MARKER}${transactionId()}`;
    const messages = [
      textMessage(this.shopALabel, this.user, "Hello"),
      voteMessage(this.defaultLabel, this.user, newMessageId(), [CONFIRM_LABEL]),
      voteMessage(this.defaultLabel, this.stranger, pollId, [CONFIRM_LABEL]),
      textMessage(this.shopALabel, this.user, code, { chat: Chat.GROUP }),
      textMessage(this.shopALabel, this.user, code, { from_me: true }),
    ];
    for (const message of messages) {
      await this.inbound(message);
    }
    await this.expectNoEvents();
  }

  async settingsPrefillText() {
    const custom = "Sign in to the stand.";
    for (const [settings, opening] of [[JSON.stringify({ prefill_text: custom }), custom], [null, DEFAULT_PREFILL_TEXT]]) {
      const transaction = transactionId();
      const text = (await this.deepLinkUrl(transaction, { settings })).searchParams.get("text") ?? "";
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

  async expectHealthNames(label, stateName) {
    const answer = OwnChecks.result(await this.call("health", {}), "health");
    if (answer.healthy !== false) {
      throw new CheckFailed(`health was answered healthy = ${JSON.stringify(answer.healthy)} with record ${label} ${stateName}`);
    }
    if (typeof answer.details !== "string" || !answer.details.includes(`${label} `) || !answer.details.includes(stateName)) {
      throw new CheckFailed(`the details of health do not name record ${label} as ${stateName}: ${JSON.stringify(answer.details)}`);
    }
  }

  async healthUnpaired() {
    await this.stub("POST", "/stand/record-state", { credential: this.shopBLabel, state: RecordState.NOT_PAIRED });
    await this.expectHealthNames(this.shopBLabel, NOT_PAIRED);
  }

  /** The instance in the baileys mode is up — it refuses an unsigned call — and has no service path. */
  async stubPathClosed() {
    const call = await http("POST", `${this.baileysUrl}/`, "");
    if (call.status !== 403) {
      throw new CheckFailed(`the instance in the baileys mode answered an unsigned call with ${call.status}, not 403`);
    }
    const status = await postInbound(this.baileysUrl, textMessage(this.baileysLabel, this.user, `${AUTH_MARKER}${transactionId()}`));
    if (status !== 404) {
      throw new CheckFailed(`the instance in the baileys mode answered ${INBOUND_PATH} with ${status}, not 404`);
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
    await this.expectSends(1, "two send-message calls under one key");
  }

  async idempotencyReportOutcomeOnce() {
    const key = idempotencyKey();
    const transaction = transactionId();
    const first = OwnChecks.result(await this.reportOutcome(transaction, { key }), "report-outcome");
    const second = OwnChecks.result(await this.reportOutcome(transaction, { key }), "report-outcome");
    if (JSON.stringify(first) !== JSON.stringify(second)) {
      throw new CheckFailed(`the repeat under the same key was answered ${JSON.stringify(second)}, the first call ${JSON.stringify(first)}`);
    }
    await this.expectSends(1, "two report-outcome calls under one key");
  }

  async idempotencyFailureUnbinds() {
    const key = idempotencyKey();
    await this.stub("POST", "/stand/refuse-send");
    const refused = await this.sendMessage({ key });
    if (refused.result !== undefined || errorCode(refused) === null) {
      throw new CheckFailed(`a send-message the platform refused was answered ${JSON.stringify(refused)}, not an error`);
    }
    await this.stub("POST", "/stand/clear");
    OwnChecks.result(await this.sendMessage({ key }), "send-message");
    await this.expectSends(1, "the repeat after a failed attempt");
  }

  // ── The side of Veriqa: tenants ──────────────────────────────────────────

  async tenantFallback() {
    const url = await this.deepLinkUrl(transactionId(), { tenant: TENANT_WITHOUT_RECORD });
    if (url.hostname !== DEEP_LINK_HOST || url.pathname !== `/${digitsOf(this.defaultNumber)}`) {
      throw new CheckFailed(`a tenant without a record got a link to ${url.hostname}${url.pathname}, not to the number of the default record`);
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
    await this.expectSends(0, "the calls of a tenant without any record");
  }

  async tenantEventRoute() {
    const transaction = transactionId();
    const text = (await this.deepLinkUrl(transaction, { tenant: SHOP_B })).searchParams.get("text") ?? "";
    await this.inbound(textMessage(this.defaultLabel, this.user, text));
    await this.expectOne(transaction, "auth_start", `${this.eventsPath}/${SHOP_B}`);
  }

  // ── The side of Veriqa: the envelope ─────────────────────────────────────

  async wireChannelType() {
    const answer = await this.sendMessage({ channelType: "other-channel" });
    if (errorCode(answer) !== CHANNEL_NOT_AVAILABLE) {
      throw new CheckFailed(`a call of another channel type was answered ${JSON.stringify(answer)}, not ${CHANNEL_NOT_AVAILABLE}`);
    }
    await this.expectSends(0, "a call of another channel type");
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
    await this.expectSends(0, "a call over 1 MB");
  }

  // ── The side of WhatsApp ─────────────────────────────────────────────────

  async inboundNoNumber() {
    for (const sender of [LID_SENDER, null]) {
      await this.inbound(textMessage(this.shopALabel, sender, `${AUTH_MARKER}${transactionId()}`));
    }
    await this.expectNoEvents();
  }

  async sendUnpaired() {
    await this.stub("POST", "/stand/record-state", { credential: this.defaultLabel, state: RecordState.NOT_PAIRED });
    const message = await this.sendMessage();
    if (errorCode(message) !== MESSAGE_SEND_FAILED) {
      throw new CheckFailed(`send-message from a record that is not paired was answered ${JSON.stringify(message)}, not ${MESSAGE_SEND_FAILED}`);
    }
    await this.deepLinkUrl(transactionId());
  }

  async healthDisconnected() {
    await this.stub("POST", "/stand/record-state", { credential: this.defaultLabel, state: RecordState.NOT_CONNECTED });
    await this.expectHealthNames(this.defaultLabel, NOT_CONNECTED);
  }

  async sendPromptSameLabels() {
    const answer = await this.sendPrompt(transactionId(), { confirmLabel: CONFIRM_LABEL, declineLabel: CONFIRM_LABEL });
    if (errorCode(answer) !== MESSAGE_SEND_FAILED) {
      throw new CheckFailed(`send-prompt with equal labels was answered ${JSON.stringify(answer)}, not ${MESSAGE_SEND_FAILED}`);
    }
    await this.expectSends(0, "send-prompt with equal labels", "poll");
  }

  async reportOutcomeNewMessage() {
    const { transaction, message } = await this.prompt();
    await this.stub("POST", "/stand/clear");
    OwnChecks.result(await this.reportOutcome(transaction, { replace: true, promptMessage: message }), "report-outcome");
    const [send] = await this.expectSends(1, "report-outcome that asks to replace the prompt");
    if (send.kind !== "text" || send.text !== STATUS_TEXT) {
      throw new CheckFailed(`report-outcome sent a ${send.kind} with ${JSON.stringify(send.text)}, not a new text of the status`);
    }
  }

  async inboundVoteAfterFailedDelivery() {
    const { transaction, pollId } = await this.prompt();
    await this.stub("POST", "/stand/refuse-events");
    // The inbound path may answer the refused delivery as it sees fit; what counts is what Veriqa got.
    await postInbound(this.adapterUrl, voteMessage(this.defaultLabel, this.user, pollId, [CONFIRM_LABEL]));
    const refused = await this.deliveriesSettled();
    if (refused.refused_events === 0 || refused.events.length !== 0) {
      throw new CheckFailed(`while Veriqa was down the vote gave ${refused.refused_events} refused and ${refused.events.length} taken delivery(ies)`);
    }
    await this.stub("POST", "/stand/clear");
    await this.inbound(voteMessage(this.defaultLabel, this.user, pollId, [CONFIRM_LABEL]));
    await this.expectOne(transaction, "auth_confirm");
  }

  /** The records once the adapter has stopped retrying a refused event: the count unchanged for a while. */
  async deliveriesSettled() {
    const deadline = Date.now() + DELIVERY_GIVE_UP_MS;
    let records = await this.records();
    let since = Date.now();
    while (Date.now() < deadline) {
      await sleep(DELIVERY_LOOK_MS);
      const next = await this.records();
      if (next.refused_events !== records.refused_events) {
        since = Date.now();
      } else if (next.refused_events > 0 && Date.now() - since >= DELIVERY_SETTLED_MS) {
        return next;
      }
      records = next;
    }
    throw new CheckFailed(`the adapter did not stop delivering the refused event within ${DELIVERY_GIVE_UP_MS / 1000} s`);
  }

  // ── The state across a recreation of the container ───────────────────────

  /** Phase 1: a message under ``key`` and the deep link of ``transaction`` for shop-b; what phase 2 needs. */
  async surviveBefore(key, transaction) {
    await this.stub("POST", "/stand/clear");
    const result = OwnChecks.result(await this.sendMessage({ key }), "send-message");
    const text = (await this.deepLinkUrl(transaction, { tenant: SHOP_B })).searchParams.get("text") ?? "";
    return { result, text };
  }

  /** Phase 2: the repeat under ``key`` and the transaction brought in on the default record. */
  async surviveAfter(key, transaction, before) {
    const second = OwnChecks.result(await this.sendMessage({ key }), "send-message");
    if (JSON.stringify(second) !== JSON.stringify(before.result)) {
      throw new CheckFailed(`the repeat after the recreation was answered ${JSON.stringify(second)}, the first call ${JSON.stringify(before.result)}`);
    }
    await this.expectSends(1, "the call before and its repeat after the recreation");
    await this.inbound(textMessage(this.defaultLabel, this.user, before.text));
    await this.expectOne(transaction, "auth_start", `${this.eventsPath}/${SHOP_B}`);
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

/** Every scenario passed, but the one this platform has no case of, which must be not applicable. */
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
    const expected = scenario.id === NOT_APPLICABLE_SCENARIO ? "not_applicable" : "passed";
    if (scenario.status !== expected) {
      problems.push(`${scenario.id} is ${JSON.stringify(scenario.status)} (${scenario.reason}), expected ${expected}`);
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
