// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Names and envelopes of the HTTP channel wire of Veriqa, version 1. The names are the ones of the wire
// contract verbatim; this is the one place the adapter spells them.

/** Version of the wire the adapter speaks, written into every envelope it sends: major.minor. */
export const WIRE_VERSION = "1.0";
export const WIRE_MAJOR_VERSION = 1;

/** Version of this adapter (semver), reported by health and written into every event. */
export const ADAPTER_VERSION = "1.0.0";

/** Headers of the Standard Webhooks signature of the transport. */
export const WEBHOOK_ID_HEADER = "webhook-id";
export const WEBHOOK_TIMESTAMP_HEADER = "webhook-timestamp";
export const WEBHOOK_SIGNATURE_HEADER = "webhook-signature";

// A transaction identifier: 43 Base62 characters.
const TRANSACTION_ID_PATTERN = /^[0-9A-Za-z]{43}$/;

/** Length of a transaction identifier. */
export const TRANSACTION_ID_LENGTH = 43;

// A wire version: major.minor in ASCII digits.
const VERSION_PATTERN = /^([0-9]+)\.[0-9]+$/;

/** Values of ``operation`` of a call of the core. */
export const Operation = Object.freeze({
  SEND_PROMPT: "send-prompt",
  SEND_MESSAGE: "send-message",
  REPORT_OUTCOME: "report-outcome",
  DEEP_LINK: "deep-link",
  HEALTH: "health",
});

/** Every operation of the wire. */
export const ALL_OPERATIONS = new Set(Object.values(Operation));

/** Values of ``event`` of an event the adapter posts to the core. */
export const Event = Object.freeze({
  AUTH_START: "auth_start",
  AUTH_CONFIRM: "auth_confirm",
  AUTH_DECLINE: "auth_decline",
});

/**
 * Values of ``purpose`` of a deep-link call — where the link is going to be used. A value the adapter does not
 * know, and a call without the field, is read as ``button``.
 */
export const LinkPurpose = Object.freeze({
  BUTTON: "button",
  HOP_QR: "hop_qr",
  DIRECT_QR: "direct_qr",
});

/** Every purpose of a link the wire names. */
export const ALL_LINK_PURPOSES = new Set(Object.values(LinkPurpose));

/** Codes of ``error`` in an answer of the adapter. */
export const ErrorCode = Object.freeze({
  CHANNEL_CANNOT_CONTINUE: "channel_cannot_continue",
  MESSAGE_SEND_FAILED: "message_send_failed",
  CHANNEL_NOT_AVAILABLE: "channel_not_available",
  DEEP_LINK_GENERATION_FAILED: "deep_link_generation_failed",
  UNSUPPORTED_WIRE_VERSION: "unsupported_wire_version",
  UNSUPPORTED_OPERATION: "unsupported_operation",
});

/** Names of the members of the envelopes the adapter reads and writes. */
export const Field = Object.freeze({
  VERSION: "version",
  OPERATION: "operation",
  CHANNEL_TYPE: "channel_type",
  TENANT: "tenant",
  ADAPTER_SETTINGS: "adapter_settings",
  PAYLOAD: "payload",
  RESULT: "result",
  ERROR: "error",
  CODE: "code",
  MESSAGE: "message",
  EVENT: "event",
  ADAPTER_VERSION: "adapter_version",

  TRANSACTION_ID: "transaction_id",
  CHANNEL_USER_ID: "channel_user_id",
  PROMPT: "prompt",
  PURPOSE: "purpose",
  RENDERED: "rendered",
  TEXT: "text",
  CONFIRM_LABEL: "confirm_label",
  DECLINE_LABEL: "decline_label",
  KIND: "kind",
  OUTCOME: "outcome",
  STATUS_TEXT: "status_text",
  INBOUND_TOKEN: "inbound_token",
  PROMPT_MESSAGE: "prompt_message",
  DISPLAY_INTENT: "display_intent",
  CHAT_ID: "chat_id",
  MESSAGE_ID: "message_id",
  SHOWN: "shown",
  URL: "url",
  HEALTHY: "healthy",
  DETAILS: "details",
  WIRE_VERSION: "wire_version",
  IDENTITY: "identity",
});

/** An answer of the adapter with the data of the operation. */
export function resultEnvelope(result) {
  return { [Field.VERSION]: WIRE_VERSION, [Field.RESULT]: result };
}

/** An answer of the adapter with a refusal of the operation. */
export function errorEnvelope(code, message) {
  return { [Field.VERSION]: WIRE_VERSION, [Field.ERROR]: { [Field.CODE]: code, [Field.MESSAGE]: message } };
}

/** The major part of a wire version ``major.minor``; ``null`` for anything else. */
export function majorVersionOf(value) {
  const match = typeof value === "string" ? VERSION_PATTERN.exec(value) : null;
  return match ? Number(match[1]) : null;
}

/** Whether a value is a transaction identifier of Veriqa. */
export function isTransactionId(value) {
  return typeof value === "string" && TRANSACTION_ID_PATTERN.test(value);
}
