// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The port of the platform in the ``stub`` mode: WhatsApp played by the platform stub of the stand.
//
// WhatsApp Web is an encrypted WebSocket protocol a stub cannot play, so in this mode the adapter does not
// speak it: it asks the stub over HTTP to send, and the stub posts the messages of the users to a service
// path of the adapter. The path exists in this mode alone. The protocol is internal to this sample:
//
//   adapter → stub   POST {stub}/platform/send-text  {credential, from, to, text}
//                        → 2xx {"message_id": "…"}
//                    POST {stub}/platform/send-poll  {credential, from, to, message_id, question, options}
//                        → 2xx; options are [the option of the confirmation, the option of the refusal]
//                    GET  {stub}/platform/health/{credential}
//                        → 200 {"state": "ok" | "not_paired" | "not_connected"}
//   stub → adapter   POST /stub/inbound  one message of a user — the fields of ``InboundField`` —
//                        → 200 once it is handled, 400 for a malformed body, 404 for an unknown credential
//
// ``credential`` is the label of the record; ``from``, ``to`` and ``sender`` are numbers in E.164.

import { randomBytes } from "node:crypto";

import { logger, readBoundedBody, sendEmpty } from "../../common.js";
import { PlatformError } from "../../core/seam.js";
import { normalizePhoneToE164 } from "../phone.js";
import { ChatKind, MessageType, PLATFORM_TIMEOUT_MS, RecordState } from "../port.js";

const log = logger("whatsapp.stub");

/** The service path the stub posts the messages of the users to. */
export const STUB_INBOUND_PATH = "/stub/inbound";

/** Paths of the stub the adapter calls. */
export const StubPath = Object.freeze({
  SEND_TEXT: "/platform/send-text",
  SEND_POLL: "/platform/send-poll",
  HEALTH: "/platform/health/",
});

/** The states of a record the stub reports, and the state each of them is. */
const STUB_STATES = Object.freeze({
  ok: RecordState.OK,
  not_paired: RecordState.NOT_PAIRED,
  not_connected: RecordState.NOT_CONNECTED,
});

/** Members of an inbound message of the stub. */
export const InboundField = Object.freeze({
  CREDENTIAL: "credential",
  MESSAGE_ID: "message_id",
  CHAT: "chat",
  FROM_ME: "from_me",
  SENDER: "sender",
  PUSH_NAME: "push_name",
  TYPE: "type",
  TEXT: "text",
  POLL_MESSAGE_ID: "poll_message_id",
  SELECTED_OPTIONS: "selected_options",
});

// Bytes of randomness in an identifier of a message the adapter makes.
const MESSAGE_ID_BYTES = 16;

export class StubPort {
  constructor(config) {
    this.stubUrl = config.stubUrl;
    this.byLabel = new Map(config.accounts.map((account) => [account.label, account]));
    this.onMessage = null;
    this.servicePath = STUB_INBOUND_PATH;
  }

  async start(onMessage) {
    this.onMessage = onMessage;
    log.info(`Platform stub mode: ${this.byLabel.size} record(s), inbound messages on ${STUB_INBOUND_PATH}.`);
  }

  async stop() {
    this.onMessage = null;
  }

  newMessageId() {
    return randomBytes(MESSAGE_ID_BYTES).toString("hex").toUpperCase();
  }

  async sendText(account, recipient, text) {
    const answer = await this.call("send text", StubPath.SEND_TEXT, {
      credential: account.label,
      from: account.number,
      to: recipient,
      text,
    });
    if (typeof answer?.message_id !== "string" || !answer.message_id) {
      throw new PlatformError("send text: the stub gave no message_id");
    }
    return answer.message_id;
  }

  async sendPoll(account, recipient, poll) {
    await this.call("send poll", StubPath.SEND_POLL, {
      credential: account.label,
      from: account.number,
      to: recipient,
      message_id: poll.messageId,
      question: poll.question,
      options: poll.options,
    });
  }

  async stateOf(account) {
    let response;
    try {
      response = await fetch(`${this.stubUrl}${StubPath.HEALTH}${encodeURIComponent(account.label)}`, {
        redirect: "manual",
        signal: AbortSignal.timeout(PLATFORM_TIMEOUT_MS),
      });
    } catch (error) {
      log.warning(`Health of record ${account.label}: the stub was not reached (${error.name}).`);
      return RecordState.NOT_CONNECTED;
    }
    const answer = response.ok ? await jsonOf(response) : null;
    return STUB_STATES[answer?.state] ?? RecordState.NOT_CONNECTED;
  }

  /** ``POST /stub/inbound``: one message of a user, handled before the answer. */
  async serve(request, response) {
    if (request.method !== "POST") {
      sendEmpty(response, 405);
      return;
    }
    const body = await readBoundedBody(request);
    if (body === null) {
      sendEmpty(response, 413);
      return;
    }
    let fields;
    try {
      fields = JSON.parse(body.toString("utf8"));
    } catch {
      fields = null;
    }
    if (typeof fields !== "object" || fields === null || Array.isArray(fields)) {
      sendEmpty(response, 400);
      return;
    }
    const account = this.byLabel.get(fields[InboundField.CREDENTIAL]);
    if (account === undefined) {
      sendEmpty(response, 404);
      return;
    }
    const message = platformMessage(account, fields);
    if (message === null) {
      sendEmpty(response, 400);
      return;
    }
    if (this.onMessage !== null) {
      await this.onMessage(message);
    }
    sendEmpty(response, 200);
  }

  /** Posts ``body`` to the stub; the JSON answer, or a ``PlatformError`` for a refusal. */
  async call(action, path, body) {
    let response;
    try {
      response = await fetch(this.stubUrl + path, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
        redirect: "manual",
        signal: AbortSignal.timeout(PLATFORM_TIMEOUT_MS),
      });
    } catch (error) {
      throw new PlatformError(`${action}: ${error.name}`);
    }
    const answer = await jsonOf(response);
    if (!response.ok) {
      throw new PlatformError(`${action}: status ${response.status}`);
    }
    return answer;
  }
}

/** The JSON answer of the stub, or ``null``. */
async function jsonOf(response) {
  try {
    return JSON.parse(await response.text());
  } catch {
    return null;
  }
}

/** An inbound message of the stub as the classification reads it, or ``null`` when the body is not one. */
function platformMessage(account, fields) {
  const messageId = fields[InboundField.MESSAGE_ID];
  const chat = fields[InboundField.CHAT];
  const type = fields[InboundField.TYPE];
  const selected = fields[InboundField.SELECTED_OPTIONS] ?? null;
  if (
    typeof messageId !== "string" || !messageId
    || !Object.values(ChatKind).includes(chat)
    || !Object.values(MessageType).includes(type)
    || (selected !== null && !(Array.isArray(selected) && selected.every((option) => typeof option === "string")))
  ) {
    return null;
  }
  return {
    account,
    messageId,
    chat,
    fromMe: fields[InboundField.FROM_ME] === true,
    sender: normalizePhoneToE164(fields[InboundField.SENDER]),
    pushName: stringOrNull(fields[InboundField.PUSH_NAME]),
    type,
    text: stringOrNull(fields[InboundField.TEXT]),
    pollMessageId: stringOrNull(fields[InboundField.POLL_MESSAGE_ID]),
    selectedOptions: selected,
  };
}

function stringOrNull(value) {
  return typeof value === "string" ? value : null;
}
