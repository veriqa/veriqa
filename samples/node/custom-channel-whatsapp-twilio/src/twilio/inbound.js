// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Which inbound WhatsApp messages of Twilio are events of Veriqa, and the identity of their sender.
//
// The rules are the ones of the built-in WhatsApp channel of Veriqa; this adapter applies them itself, before
// anything goes to Veriqa, so a message that is not an event of Veriqa never leaves the adapter.

import { Event, TRANSACTION_ID_LENGTH, isTransactionId } from "../core/wire.js";
import { fromWhatsAppAddress } from "./phone.js";

/** Markers Veriqa puts into its deep links and buttons — the same as the built-in WhatsApp channel uses. */
export const AUTH_MARKER = "auth_";
export const CONFIRM_PREFIX = "vq_confirm_";
export const DECLINE_PREFIX = "vq_decline_";

/** Parameters of the inbound message webhook of Twilio the adapter reads. */
export const InboundParameter = Object.freeze({
  MESSAGE_SID: "MessageSid",
  FROM: "From",
  BODY: "Body",
  BUTTON_PAYLOAD: "ButtonPayload",
  PROFILE_NAME: "ProfileName",
  MESSAGE_TYPE: "MessageType",
});

/**
 * @typedef {object} Classification What one inbound message is.
 * @property {string|null} event The event of Veriqa, or ``null`` when the message is not one.
 * @property {string|null} transactionId
 * @property {string|null} phone The number of the sender in E.164, or ``null`` when ``From`` is not one.
 */

const NOT_AN_EVENT = Object.freeze({ event: null, transactionId: null, phone: null });

/**
 * The classification of one inbound message: its parameters as Twilio posted them.
 *
 * A button press — a message with a ``ButtonPayload`` — is read by its payload alone; any other message by
 * the ``auth_`` marker in its text. A sender that is not a number in E.164 makes no event.
 */
export function classify(parameters) {
  const phone = fromWhatsAppAddress(single(parameters, InboundParameter.FROM));
  if (phone === null) {
    return NOT_AN_EVENT;
  }

  const payload = single(parameters, InboundParameter.BUTTON_PAYLOAD);
  if (payload) {
    for (const [prefix, event] of [[CONFIRM_PREFIX, Event.AUTH_CONFIRM], [DECLINE_PREFIX, Event.AUTH_DECLINE]]) {
      if (payload.startsWith(prefix)) {
        const transactionId = payload.slice(prefix.length);
        return isTransactionId(transactionId) ? { event, transactionId, phone } : { ...NOT_AN_EVENT, phone };
      }
    }
    return { ...NOT_AN_EVENT, phone };
  }

  const text = (single(parameters, InboundParameter.BODY) ?? "").trim();
  const transactionId = text ? extractAuthTransactionId(text) : null;
  return transactionId === null ? { ...NOT_AN_EVENT, phone } : { event: Event.AUTH_START, transactionId, phone };
}

/**
 * The transaction of the ``auth_`` marker anywhere in a text, or ``null``: the prefilled message of a deep
 * link carries the marker on a line of its own, below the opening line. The identifier has a fixed length
 * and alphabet, which keeps the surrounding text from making a false match; overlapping occurrences such as
 * ``auth_auth_…`` are all tried.
 */
export function extractAuthTransactionId(text) {
  let from = 0;
  for (;;) {
    const index = text.indexOf(AUTH_MARKER, from);
    if (index < 0) {
      return null;
    }
    const candidate = text.slice(index + AUTH_MARKER.length, index + AUTH_MARKER.length + TRANSACTION_ID_LENGTH);
    if (isTransactionId(candidate)) {
      return candidate;
    }
    from = index + 1;
  }
}

/**
 * The ``identity`` member of an event: the number is the user, the profile name — or the number — is the name.
 * The number is the sender's own — WhatsApp delivers the message from it — so it goes out as verified. The
 * built-in WhatsApp channel of Veriqa knows the user by the business-scoped user ID (BSUID) instead; this
 * sample keys the user by the number, so a message without one is dropped.
 */
export function identityOf(parameters, phone) {
  const profileName = (single(parameters, InboundParameter.PROFILE_NAME) ?? "").trim();
  const displayName = profileName || phone;
  return {
    channel_user_id: phone,
    display_name: displayName,
    first_name: displayName,
    phone_number: phone,
    phone_number_verified: true,
    is_bot: false,
    raw_metadata: {
      whatsapp_message_id: single(parameters, InboundParameter.MESSAGE_SID),
      message_type: single(parameters, InboundParameter.MESSAGE_TYPE),
    },
  };
}

/** The value of a parameter posted once; a missing or repeated parameter reads as ``null``. */
export function single(parameters, name) {
  const value = parameters[name];
  return typeof value === "string" ? value : null;
}
