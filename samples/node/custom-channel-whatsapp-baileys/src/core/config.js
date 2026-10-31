// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Settings of the side of the core, read from the environment once at the start.

import { ConfigError, LOG_LEVEL_NAMES, absoluteUrl, optional, positiveInt, required } from "../common.js";
import { EVENT_REDELIVERY_WINDOW_SECONDS } from "./events.js";

/** Names of the environment variables of the side of the core. */
export const CoreEnv = Object.freeze({
  VERIQA_URL: "VERIQA_URL",
  CHANNEL_TYPE: "VERIQA_CHANNEL_TYPE",
  TRANSPORT_SECRET: "VERIQA_TRANSPORT_SECRET",
  PREVIOUS_TRANSPORT_SECRET: "VERIQA_PREVIOUS_TRANSPORT_SECRET",
  PORT: "ADAPTER_PORT",
  DATA_DIR: "ADAPTER_DATA_DIR",
  IDEMPOTENCY_TTL_SECONDS: "ADAPTER_IDEMPOTENCY_TTL_SECONDS",
  LOG_LEVEL: "ADAPTER_LOG_LEVEL",
});

export const DEFAULT_PORT = 8000;

// The largest TCP port number.
const MAX_PORT = 65535;
export const DEFAULT_DATA_DIR = "/data";
export const DEFAULT_IDEMPOTENCY_TTL_SECONDS = 24 * 60 * 60;
export const DEFAULT_LOG_LEVEL = "INFO";

// The budget within which the HTTP channel of Veriqa repeats one call: a key kept for less could let a
// repeat of the same call through as a new one.
const WRAPPER_RETRY_BUDGET_SECONDS = 20;

// The longest transaction Veriqa accepts (the upper bound of its TransactionTtlSeconds): the route of a
// transaction is kept for the same retention and has to outlive the transaction, or a late event of it
// would go on the route of the tenant of the credential instead.
const VERIQA_MAX_TRANSACTION_TTL_SECONDS = 30 * 60;

/**
 * The retention may not be shorter than any window in which the same key can come again, nor than a
 * transaction whose events need its route.
 */
export const MIN_IDEMPOTENCY_TTL_SECONDS = Math.max(
  WRAPPER_RETRY_BUDGET_SECONDS,
  EVENT_REDELIVERY_WINDOW_SECONDS,
  VERIQA_MAX_TRANSACTION_TTL_SECONDS,
);

// A channel type of Veriqa.
const CHANNEL_TYPE_PATTERN = /^[a-z][a-z0-9-]{0,63}$/;

// A transport secret in the Standard Webhooks serialization: the prefix and a base64 key of 24 to 64 bytes.
const SECRET_PREFIX = "whsec_";
const SECRET_KEY_PATTERN = /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/;
const MIN_SECRET_BYTES = 24;
const MAX_SECRET_BYTES = 64;

/**
 * Where Veriqa is, which registration the adapter serves, and how it keeps its state.
 *
 * @typedef {object} CoreConfig
 * @property {string} veriqaUrl
 * @property {string} channelType
 * @property {string[]} transportSecrets The current secret first, the previous one during a rotation.
 * @property {number} port
 * @property {string} dataDir
 * @property {number} idempotencyTtlSeconds
 * @property {string} logLevel
 */

/** The settings of the side of the core; a missing or invalid one throws a ``ConfigError``. */
export function coreConfigFromEnv(env) {
  const veriqaUrl = absoluteUrl(CoreEnv.VERIQA_URL, required(env, CoreEnv.VERIQA_URL));

  const channelType = required(env, CoreEnv.CHANNEL_TYPE);
  if (!CHANNEL_TYPE_PATTERN.test(channelType)) {
    throw new ConfigError(`${CoreEnv.CHANNEL_TYPE} must be a lowercase channel type such as whatsapp-baileys`);
  }

  const transportSecrets = [transportSecret(env, CoreEnv.TRANSPORT_SECRET, true)];
  const previous = transportSecret(env, CoreEnv.PREVIOUS_TRANSPORT_SECRET, false);
  if (previous !== null) {
    transportSecrets.push(previous);
  }

  const idempotencyTtlSeconds = positiveInt(env, CoreEnv.IDEMPOTENCY_TTL_SECONDS, DEFAULT_IDEMPOTENCY_TTL_SECONDS);
  if (idempotencyTtlSeconds < MIN_IDEMPOTENCY_TTL_SECONDS) {
    throw new ConfigError(`${CoreEnv.IDEMPOTENCY_TTL_SECONDS} must be at least ${MIN_IDEMPOTENCY_TTL_SECONDS} seconds`);
  }

  const port = positiveInt(env, CoreEnv.PORT, DEFAULT_PORT);
  if (port > MAX_PORT) {
    throw new ConfigError(`${CoreEnv.PORT} must be a port number from 1 to ${MAX_PORT}`);
  }

  const logLevel = (optional(env, CoreEnv.LOG_LEVEL) ?? DEFAULT_LOG_LEVEL).toUpperCase();
  if (!LOG_LEVEL_NAMES.includes(logLevel)) {
    throw new ConfigError(`${CoreEnv.LOG_LEVEL} must be one of ${LOG_LEVEL_NAMES.join(", ")}`);
  }

  return {
    veriqaUrl,
    channelType,
    transportSecrets,
    port,
    dataDir: optional(env, CoreEnv.DATA_DIR) ?? DEFAULT_DATA_DIR,
    idempotencyTtlSeconds,
    logLevel,
  };
}

/** A transport secret in the Standard Webhooks serialization; the value never reaches a message. */
function transportSecret(env, name, isRequired) {
  const value = isRequired ? required(env, name) : optional(env, name);
  if (value === null) {
    return null;
  }
  if (!value.startsWith(SECRET_PREFIX)) {
    throw new ConfigError(`${name} must start with ${SECRET_PREFIX}`);
  }
  const encoded = value.slice(SECRET_PREFIX.length);
  if (!SECRET_KEY_PATTERN.test(encoded)) {
    throw new ConfigError(`${name} must carry a base64 key after ${SECRET_PREFIX}`);
  }
  const length = Buffer.from(encoded, "base64").length;
  if (length < MIN_SECRET_BYTES || length > MAX_SECRET_BYTES) {
    throw new ConfigError(`${name} must carry a key of ${MIN_SECRET_BYTES} to ${MAX_SECRET_BYTES} bytes`);
  }
  return value;
}
