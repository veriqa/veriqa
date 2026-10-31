// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The own setting of this adapter, carried by the opaque adapter_settings string of every call.
//
// Veriqa forwards the string verbatim and never reads it; the operator sets it for the channel and, when a
// tenant needs another value, for that tenant. This adapter reads a JSON object with one key:
//
//     {"prefill_text": "Send this message to sign in to Acme."}
//
// prefill_text is the opening line of the message a deep link puts into WhatsApp, above the sign-in code, when
// the deep-link call carries no text Veriqa rendered for the link: an earlier Veriqa does not send one, and the
// current one always does unless it cannot render a plain-text wording for the link. The text of the link is
// worded by the deeplink-prefill template of the channel type in Veriqa, not here. The string is secret-grade
// for Veriqa, so its value never reaches the log.

import { logger } from "../common.js";

const log = logger("whatsapp.settings");

export const PREFILL_TEXT = "prefill_text";

/** Longest opening line the setting may carry, in characters. */
export const MAX_PREFILL_TEXT_LENGTH = 200;

/** The opening line of the prefilled message when the setting gives none. */
export const DEFAULT_PREFILL_TEXT = "Send this message to sign in.";

/** The ``prefill_text`` of the settings string; the default with a warning when it cannot be read. */
export function prefillText(settings) {
  if (settings === null) {
    log.warning(`adapter_settings is not set; ${PREFILL_TEXT} takes the default.`);
    return DEFAULT_PREFILL_TEXT;
  }
  let parsed;
  try {
    parsed = JSON.parse(settings);
  } catch {
    log.warning(`adapter_settings is not JSON; ${PREFILL_TEXT} takes the default.`);
    return DEFAULT_PREFILL_TEXT;
  }
  const value = typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed[PREFILL_TEXT] : undefined;
  if (typeof value !== "string" || !value.trim()) {
    log.warning(`adapter_settings has no ${PREFILL_TEXT} string; it takes the default.`);
    return DEFAULT_PREFILL_TEXT;
  }
  if ([...value].length > MAX_PREFILL_TEXT_LENGTH) {
    log.warning(`${PREFILL_TEXT} of adapter_settings is longer than ${MAX_PREFILL_TEXT_LENGTH} characters; it takes the default.`);
    return DEFAULT_PREFILL_TEXT;
  }
  return value;
}
