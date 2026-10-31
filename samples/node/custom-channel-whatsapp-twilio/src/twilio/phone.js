// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Phone numbers the way the built-in WhatsApp channel of Veriqa reads them, and the address form of Twilio.

/** The prefix Twilio puts before a number to address it on WhatsApp. */
export const WHATSAPP_ADDRESS_PREFIX = "whatsapp:";

// Shortest and longest E.164 number the built-in channel accepts, in digits.
const MIN_PHONE_DIGITS = 8;
const MAX_PHONE_DIGITS = 15;

/**
 * A phone number in E.164 — ``+`` and 8 to 15 digits — or ``null``.
 *
 * Separators are dropped and the digits kept, as the built-in channel does; a string with ``:`` is refused
 * before that, since a channel user identifier of Veriqa never contains one. A Twilio address
 * ``whatsapp:+…`` is therefore refused here: its prefix is taken off by ``fromWhatsAppAddress`` first.
 */
export function normalizePhoneToE164(rawPhone) {
  if (typeof rawPhone !== "string" || !rawPhone.trim()) {
    return null;
  }
  const trimmed = rawPhone.trim();
  if (trimmed.includes(":")) {
    return null;
  }
  const digits = trimmed.replace(/[^0-9]/g, "");
  if (digits.length < MIN_PHONE_DIGITS || digits.length > MAX_PHONE_DIGITS) {
    return null;
  }
  return `+${digits}`;
}

/** The number of a Twilio WhatsApp address in E.164, or ``null`` when it is not such an address. */
export function fromWhatsAppAddress(address) {
  if (typeof address !== "string" || !address.startsWith(WHATSAPP_ADDRESS_PREFIX)) {
    return null;
  }
  return normalizePhoneToE164(address.slice(WHATSAPP_ADDRESS_PREFIX.length));
}

/** The Twilio WhatsApp address of a number in E.164. */
export function toWhatsAppAddress(e164) {
  return WHATSAPP_ADDRESS_PREFIX + e164;
}
