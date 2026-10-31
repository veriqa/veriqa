// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// What the stand says the way Twilio says it: the inbound message webhook of a WhatsApp sender, signed with
// X-Twilio-Signature. Shared by the platform stub (the user following a link) and the own checks.
//
// The signature is the one Twilio documents for a webhook with a form body: HMAC-SHA1 with the Auth Token of
// the account over the full URL followed by every parameter name and value, the names in sorted order,
// Base64-encoded. The stand makes its own signatures here; the adapter checks them with the SDK of Twilio.

import { createHmac, randomBytes } from "node:crypto";

/** The header Twilio puts its signature of a webhook into. */
export const SIGNATURE_HEADER = "x-twilio-signature";

/** The prefix Twilio puts before a number to address it on WhatsApp. */
export const WHATSAPP_ADDRESS_PREFIX = "whatsapp:";

/** Path prefix of the inbound webhook of one sender of the adapter: the rest is the digits of the sender. */
export const WEBHOOK_PATH_PREFIX = "/twilio/";

/** How long one delivery of a webhook may take: the adapter answers once Veriqa has taken the event. */
export const DELIVERY_TIMEOUT_MS = 60_000;

/** The signature Twilio would put on a webhook to ``url`` with the form ``parameters``. */
export function twilioSignature(authToken, url, parameters) {
  const data = Object.keys(parameters)
    .sort()
    .reduce((text, name) => text + name + parameters[name], url);
  return createHmac("sha1", authToken).update(Buffer.from(data, "utf8")).digest("base64");
}

/** A new SID of a message: the prefix of a message resource and 32 random hexadecimal digits. */
export function newMessageSid() {
  return `SM${randomBytes(16).toString("hex")}`;
}

/** The digits of a phone number, with or without its ``+``. */
export function digitsOf(number) {
  return String(number).replace(/[^0-9]/g, "");
}

/** The WhatsApp address of a phone number in E.164. */
export function whatsAppAddress(e164) {
  return WHATSAPP_ADDRESS_PREFIX + e164;
}

/**
 * Posts one inbound message to a webhook of the adapter as Twilio does: a form body and, unless ``signature``
 * is ``null``, the signature header. Returns the HTTP status of the answer.
 */
export async function postWebhook(url, parameters, signature) {
  const headers = { "content-type": "application/x-www-form-urlencoded" };
  if (signature !== null) {
    headers[SIGNATURE_HEADER] = signature;
  }
  const response = await fetch(url, {
    method: "POST",
    headers,
    body: new URLSearchParams(parameters).toString(),
    redirect: "manual",
    signal: AbortSignal.timeout(DELIVERY_TIMEOUT_MS),
  });
  await response.body?.cancel().catch(() => {});
  return response.status;
}
