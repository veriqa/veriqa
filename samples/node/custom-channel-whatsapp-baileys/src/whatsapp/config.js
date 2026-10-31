// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Settings of the side of the platform, read from the environment once at the start.
//
// Credentials are records of WhatsApp numbers, each with a WhatsApp Web session of its own. The record
// without a tenant is the default one: the implicit tenant and every tenant without a record of its own use
// it. A record with a tenant serves that tenant alone. The label of a record names it in the log, in the
// platform log of the stand and in the path of its session state; it is not a secret.

import { join } from "node:path";

import { ConfigError, absoluteUrl, optional, required } from "../common.js";
import { normalizePhoneToE164 } from "./phone.js";

/** Names of the environment variables of the side of the platform. */
export const WhatsAppEnv = Object.freeze({
  PLATFORM: "WHATSAPP_PLATFORM",
  STUB_URL: "WHATSAPP_STUB_URL",
  SESSIONS_DIR: "WHATSAPP_SESSIONS_DIR",
  LABEL: "WHATSAPP_LABEL",
  NUMBER: "WHATSAPP_NUMBER",
});

// Records of the numbers of tenants: WHATSAPP_ACCOUNT_<n>_TENANT, _LABEL and _NUMBER, n = 1, 2, ...
const NUMBERED_NUMBER = /^WHATSAPP_ACCOUNT_([0-9]+)_NUMBER$/;

/** The name of a variable of the numbered record ``number``. */
export function numbered(number, suffix) {
  return `WHATSAPP_ACCOUNT_${number}_${suffix}`;
}

/** What the port of the platform talks to: WhatsApp Web through Baileys, or the platform stub of the stand. */
export const PlatformMode = Object.freeze({
  BAILEYS: "baileys",
  STUB: "stub",
});

/** The label of the default record when the operator gives none. */
export const DEFAULT_LABEL = "default";

/** Name of the directory of the session states inside the data directory, when the operator names none. */
export const DEFAULT_SESSIONS_SUBDIR = "sessions";

// A label: a path segment of the session state and a word of the log.
const LABEL_PATTERN = /^[a-z0-9][a-z0-9_-]{0,63}$/;

/** One WhatsApp number of the adapter: the tenant it serves (``null`` — the default record) and its label. */
export class WhatsAppAccount {
  constructor(tenant, label, number) {
    this.tenant = tenant;
    this.label = label;
    /** The WhatsApp number in E.164, ``+`` and digits. */
    this.number = number;
  }

  /** The digits of the number: the path of the deep link and the user part of the address of WhatsApp Web. */
  get digits() {
    return this.number.slice(1);
  }
}

/**
 * Which port serves the platform, where it keeps the sessions, and the numbers.
 *
 * @typedef {object} WhatsAppConfig
 * @property {string} mode One of ``PlatformMode``.
 * @property {string|null} stubUrl The base URL of the platform stub; set in the ``stub`` mode alone.
 * @property {string} sessionsDir The directory the session state of every record is kept under.
 * @property {WhatsAppAccount[]} accounts
 */

/** The settings of the side of the platform; a missing or invalid one throws a ``ConfigError``. */
export function whatsAppConfigFromEnv(env, dataDir) {
  const mode = (optional(env, WhatsAppEnv.PLATFORM) ?? PlatformMode.BAILEYS).toLowerCase();
  if (!Object.values(PlatformMode).includes(mode)) {
    throw new ConfigError(`${WhatsAppEnv.PLATFORM} must be one of ${Object.values(PlatformMode).join(", ")}`);
  }
  const stubUrl = mode === PlatformMode.STUB
    ? absoluteUrl(WhatsAppEnv.STUB_URL, required(env, WhatsAppEnv.STUB_URL))
    : null;
  return {
    mode,
    stubUrl,
    sessionsDir: optional(env, WhatsAppEnv.SESSIONS_DIR) ?? join(dataDir, DEFAULT_SESSIONS_SUBDIR),
    accounts: accountRecords(env),
  };
}

function accountRecords(env) {
  const records = [];

  if (optional(env, WhatsAppEnv.NUMBER) !== null) {
    records.push(record(env, null, { label: WhatsAppEnv.LABEL, number: WhatsAppEnv.NUMBER }));
  }

  const numbers = Object.keys(env)
    .map((name) => NUMBERED_NUMBER.exec(name))
    .filter((match) => match !== null)
    .map((match) => match[1])
    .sort((left, right) => Number(left) - Number(right));
  for (const number of numbers) {
    records.push(record(env, required(env, numbered(number, "TENANT")), {
      label: numbered(number, "LABEL"),
      number: numbered(number, "NUMBER"),
    }));
  }

  if (records.length === 0) {
    throw new ConfigError(`no WhatsApp number is configured: set ${WhatsAppEnv.NUMBER} or ${numbered("1", "NUMBER")}`);
  }
  for (const [field, what] of [["tenant", "tenant"], ["label", "label"], ["number", "WhatsApp number"]]) {
    if (new Set(records.map((account) => account[field])).size !== records.length) {
      throw new ConfigError(`two WhatsApp records carry the same ${what}`);
    }
  }
  return records;
}

/**
 * One record; ``names`` are the variables it is read from. The number is required; the label is required
 * for a record of a tenant, and the default record takes ``default`` without one.
 */
function record(env, tenant, names) {
  const number = normalizePhoneToE164(required(env, names.number));
  if (number === null) {
    throw new ConfigError(`${names.number} is not a phone number in E.164, such as +14155550123`);
  }
  const label = tenant === null ? optional(env, names.label) ?? DEFAULT_LABEL : required(env, names.label);
  if (!LABEL_PATTERN.test(label)) {
    throw new ConfigError(`${names.label} must be 1 to 64 lowercase letters, digits, - and _, starting with a letter or a digit`);
  }
  return new WhatsAppAccount(tenant, label, number);
}
