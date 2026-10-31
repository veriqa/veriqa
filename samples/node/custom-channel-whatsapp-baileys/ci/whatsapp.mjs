// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// What the stand says the way the adapter in the stub mode hears WhatsApp: one message of a user, posted to
// the service path of the adapter. Shared by the platform stub (the user following a link) and the own
// checks. The form of the message is the one the adapter reads there — internal to this sample.

import { randomBytes } from "node:crypto";

/** The service path the adapter opens in the stub mode alone. */
export const INBOUND_PATH = "/stub/inbound";

/** How long one delivery of a message may take: the adapter answers once Veriqa has taken the event. */
export const DELIVERY_TIMEOUT_MS = 60_000;

/** The profile name of the users of the stand. */
export const PROFILE_NAME = "Stand User";

/** Kinds of the chat and types of a message, as the adapter reads them. */
export const Chat = Object.freeze({ PERSONAL: "personal", GROUP: "group" });
export const MessageType = Object.freeze({ TEXT: "text", POLL_VOTE: "poll_vote" });

/** A new identifier of a WhatsApp message: 16 random bytes in uppercase hexadecimal digits. */
export function newMessageId() {
  return randomBytes(16).toString("hex").toUpperCase();
}

/** The digits of a phone number, with or without its ``+``. */
export function digitsOf(number) {
  return String(number).replace(/[^0-9]/g, "");
}

/** A text of ``sender`` in a personal chat with the record ``credential``. */
export function textMessage(credential, sender, text, overrides = {}) {
  return {
    credential,
    message_id: newMessageId(),
    chat: Chat.PERSONAL,
    from_me: false,
    sender,
    push_name: PROFILE_NAME,
    type: MessageType.TEXT,
    text,
    ...overrides,
  };
}

/** A vote of ``sender`` on the poll ``pollMessageId`` of the record ``credential``. */
export function voteMessage(credential, sender, pollMessageId, selectedOptions) {
  return {
    credential,
    message_id: newMessageId(),
    chat: Chat.PERSONAL,
    from_me: false,
    sender,
    push_name: PROFILE_NAME,
    type: MessageType.POLL_VOTE,
    poll_message_id: pollMessageId,
    selected_options: selectedOptions,
  };
}

/** Posts one message to the service path of the adapter at ``adapterUrl``; the HTTP status of the answer. */
export async function postInbound(adapterUrl, message) {
  const response = await fetch(`${adapterUrl.replace(/\/+$/, "")}${INBOUND_PATH}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(message),
    redirect: "manual",
    signal: AbortSignal.timeout(DELIVERY_TIMEOUT_MS),
  });
  await response.body?.cancel().catch(() => {});
  return response.status;
}
