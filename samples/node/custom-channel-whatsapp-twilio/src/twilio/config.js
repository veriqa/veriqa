// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Settings of the side of the platform, read from the environment once at the start.
//
// Credentials are records of Twilio senders. The record without a tenant is the default one: the implicit
// tenant and every tenant without a record of its own use it. A record with a tenant serves that tenant
// alone. Each record has its own inbound webhook path, named by the digits of its WhatsApp sender number.

import { ConfigError, absoluteUrl, optional, positiveInt, required } from "../common.js";
import { normalizePhoneToE164 } from "./phone.js";

/** Names of the environment variables of the side of the platform. */
export const TwilioEnv = Object.freeze({
  API_URL: "TWILIO_API_URL",
  PUBLIC_URL: "TWILIO_PUBLIC_URL",
  STATUS_WAIT_SECONDS: "TWILIO_STATUS_WAIT_SECONDS",
  ACCOUNT_SID: "TWILIO_ACCOUNT_SID",
  AUTH_TOKEN: "TWILIO_AUTH_TOKEN",
  WHATSAPP_FROM: "TWILIO_WHATSAPP_FROM",
  PROMPT_CONTENT_SID: "TWILIO_PROMPT_CONTENT_SID",
});

// Records of the senders of tenants: TWILIO_ACCOUNT_<n>_TENANT, _SID, _AUTH_TOKEN, _WHATSAPP_FROM and
// _PROMPT_CONTENT_SID, n = 1, 2, ...
const NUMBERED_SID = /^TWILIO_ACCOUNT_([0-9]+)_SID$/;

/** The name of a variable of the numbered record ``number``. */
export function numbered(number, suffix) {
  return `TWILIO_ACCOUNT_${number}_${suffix}`;
}

export const DEFAULT_API_URL = "https://api.twilio.com";

/**
 * How long send-prompt may take, counted from its start, for the creation of the prompt and the reads of its
 * status for a refusal of the platform: shorter than the operation timeout of the HTTP channel of Veriqa
 * (10 seconds by default), so the call answers inside it. The adapter does not know the timeout the operator
 * set in Veriqa, so a larger value is not refused: the operator keeps the two in step.
 */
export const DEFAULT_STATUS_WAIT_SECONDS = 5;

/** Path prefix of the inbound webhook of one record; the record is named by the digits of its sender. */
export const WEBHOOK_PATH_PREFIX = "/twilio/";

// An Account SID and the SID of a Content template: a prefix and 32 hexadecimal digits.
const ACCOUNT_SID_PATTERN = /^AC[0-9a-fA-F]{32}$/;
const CONTENT_SID_PATTERN = /^HX[0-9a-fA-F]{32}$/;

/**
 * One Twilio sender: the tenant it serves (``null`` — the default record) and its credentials. The Auth
 * Token is kept off the enumerable fields, so no serialization of a record carries it.
 */
export class TwilioAccount {
  constructor(tenant, accountSid, authToken, sender, promptContentSid) {
    this.tenant = tenant;
    this.accountSid = accountSid;
    /** The WhatsApp sender number in E.164, ``+`` and digits. */
    this.sender = sender;
    this.promptContentSid = promptContentSid;
    Object.defineProperty(this, "authToken", { value: authToken, enumerable: false });
  }

  /** The digits of the sender number: the key of the webhook path and of the deep link. */
  get senderDigits() {
    return this.sender.slice(1);
  }

  get webhookPath() {
    return WEBHOOK_PATH_PREFIX + this.senderDigits;
  }
}

/**
 * Where the REST API of Twilio is, where the inbound webhooks are published, and the senders.
 *
 * @typedef {object} TwilioConfig
 * @property {string} apiUrl
 * @property {string} publicUrl The public base URL of the webhooks: the URL Twilio signs is it plus the path.
 * @property {number} statusWaitSeconds
 * @property {TwilioAccount[]} accounts
 */

/** The settings of the side of the platform; a missing or invalid one throws a ``ConfigError``. */
export function twilioConfigFromEnv(env) {
  return {
    apiUrl: absoluteUrl(TwilioEnv.API_URL, optional(env, TwilioEnv.API_URL) ?? DEFAULT_API_URL),
    publicUrl: absoluteUrl(TwilioEnv.PUBLIC_URL, required(env, TwilioEnv.PUBLIC_URL)),
    statusWaitSeconds: positiveInt(env, TwilioEnv.STATUS_WAIT_SECONDS, DEFAULT_STATUS_WAIT_SECONDS),
    accounts: accountRecords(env),
  };
}

function accountRecords(env) {
  const records = [];

  if (optional(env, TwilioEnv.ACCOUNT_SID) !== null) {
    records.push(record(env, null, {
      sid: TwilioEnv.ACCOUNT_SID,
      authToken: TwilioEnv.AUTH_TOKEN,
      sender: TwilioEnv.WHATSAPP_FROM,
      promptContentSid: TwilioEnv.PROMPT_CONTENT_SID,
    }));
  }

  const numbers = Object.keys(env)
    .map((name) => NUMBERED_SID.exec(name))
    .filter((match) => match !== null)
    .map((match) => match[1])
    .sort((left, right) => Number(left) - Number(right));
  for (const number of numbers) {
    records.push(record(env, required(env, numbered(number, "TENANT")), {
      sid: numbered(number, "SID"),
      authToken: numbered(number, "AUTH_TOKEN"),
      sender: numbered(number, "WHATSAPP_FROM"),
      promptContentSid: numbered(number, "PROMPT_CONTENT_SID"),
    }));
  }

  if (records.length === 0) {
    throw new ConfigError(`no Twilio account is configured: set ${TwilioEnv.ACCOUNT_SID} or ${numbered("1", "SID")}`);
  }
  if (new Set(records.map((account) => account.tenant)).size !== records.length) {
    throw new ConfigError("two Twilio account records name the same tenant");
  }
  if (new Set(records.map((account) => account.sender)).size !== records.length) {
    throw new ConfigError("two Twilio account records carry the same WhatsApp sender number");
  }
  return records;
}

/** One record; ``names`` are the variables it is read from, and every one of them is required. */
function record(env, tenant, names) {
  const accountSid = required(env, names.sid);
  if (!ACCOUNT_SID_PATTERN.test(accountSid)) {
    throw new ConfigError(`${names.sid} is not an Account SID of the form AC and 32 hexadecimal digits`);
  }
  const sender = normalizePhoneToE164(required(env, names.sender));
  if (sender === null) {
    throw new ConfigError(`${names.sender} is not a phone number in E.164, such as +14155238886`);
  }
  const promptContentSid = required(env, names.promptContentSid);
  if (!CONTENT_SID_PATTERN.test(promptContentSid)) {
    throw new ConfigError(`${names.promptContentSid} is not a Content SID of the form HX and 32 hexadecimal digits`);
  }
  return new TwilioAccount(tenant, accountSid, required(env, names.authToken), sender, promptContentSid);
}
