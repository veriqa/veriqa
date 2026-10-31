// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Phone numbers the way the built-in WhatsApp channel of Veriqa reads them.

// Shortest and longest E.164 number the built-in channel accepts, in digits.
const MIN_PHONE_DIGITS = 8;
const MAX_PHONE_DIGITS = 15;

/**
 * A phone number in E.164 — ``+`` and 8 to 15 digits — or ``null``.
 *
 * Separators are dropped and the digits kept, as the built-in channel does; a string with ``:`` or ``@`` is
 * refused before that, since a channel user identifier of Veriqa never contains one: an address of WhatsApp
 * Web such as ``…@s.whatsapp.net`` is read by the platform, not here.
 */
export function normalizePhoneToE164(rawPhone) {
  if (typeof rawPhone !== "string" || !rawPhone.trim()) {
    return null;
  }
  const trimmed = rawPhone.trim();
  if (trimmed.includes(":") || trimmed.includes("@")) {
    return null;
  }
  const digits = trimmed.replace(/[^0-9]/g, "");
  if (digits.length < MIN_PHONE_DIGITS || digits.length > MAX_PHONE_DIGITS) {
    return null;
  }
  return `+${digits}`;
}
