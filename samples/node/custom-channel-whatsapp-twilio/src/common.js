// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Helpers both sides of the adapter share: configuration errors, the log, log fingerprints, bounded bodies.

import { createHash } from "node:crypto";

/** Largest body the adapter reads from a request: the same limit Veriqa puts on its webhook route. */
export const MAX_BODY_BYTES = 1024 * 1024;

// Length of a log fingerprint in bytes of SHA-256 — the form the built-in channels of Veriqa use.
const FINGERPRINT_BYTES = 8;

const LOG_LEVELS = { DEBUG: 10, INFO: 20, WARNING: 30, ERROR: 40 };

/** The names of the log levels an operator may choose. */
export const LOG_LEVEL_NAMES = Object.keys(LOG_LEVELS);

let threshold = LOG_LEVELS.INFO;

/** A setting of the adapter is missing or invalid. The message names the variable, never its value. */
export class ConfigError extends Error {}

/** Sets the lowest level the log writes; ``level`` is one of ``LOG_LEVEL_NAMES``. */
export function setLogLevel(level) {
  threshold = LOG_LEVELS[level];
}

/**
 * A named logger. A message is a fixed text with values the caller has made safe for the log: never a
 * secret, a settings string, a body of the wire or of the platform, or a phone number in clear text.
 */
export function logger(name) {
  const write = (level, message) => {
    if (LOG_LEVELS[level] < threshold) {
      return;
    }
    process.stderr.write(`${new Date().toISOString()} ${level} ${name}: ${message}\n`);
  };
  return {
    debug: (message) => write("DEBUG", message),
    info: (message) => write("INFO", message),
    warning: (message) => write("WARNING", message),
    error: (message) => write("ERROR", message),
  };
}

/** The value of a required environment variable; a missing or blank one stops the start. */
export function required(env, name) {
  const value = (env[name] ?? "").trim();
  if (!value) {
    throw new ConfigError(`${name} is not set`);
  }
  return value;
}

/** The value of an optional environment variable; blank reads as not set. */
export function optional(env, name) {
  const value = (env[name] ?? "").trim();
  return value || null;
}

/**
 * An optional whole number above zero. A number past the safe integers of JavaScript is refused as well:
 * such a value reads back rounded, or as ``Infinity``, and no arithmetic on it holds.
 */
export function positiveInt(env, name, defaultValue) {
  const value = optional(env, name);
  if (value === null) {
    return defaultValue;
  }
  if (!isWholeNumber(value) || !Number.isSafeInteger(Number(value)) || Number(value) <= 0) {
    throw new ConfigError(`${name} must be a whole number from 1 to ${Number.MAX_SAFE_INTEGER}`);
  }
  return Number(value);
}

/** Whether a string is a whole number in ASCII digits. */
function isWholeNumber(value) {
  return typeof value === "string" && /^[0-9]+$/.test(value);
}

/**
 * An absolute ``http`` or ``https`` URL without a query or a fragment, its trailing slashes cut off. The
 * text itself is checked for ``?`` and ``#``: a parsed URL reports an empty query or fragment as none, and
 * the text, not the parsed URL, is what the adapter joins paths to.
 */
export function absoluteUrl(name, value) {
  let address;
  try {
    address = new URL(value);
  } catch {
    address = null;
  }
  if (address === null || !["http:", "https:"].includes(address.protocol) || /[?#]/.test(value)) {
    throw new ConfigError(`${name} must be an absolute http or https URL without a query or a fragment`);
  }
  return value.replace(/\/+$/, "");
}

/** A tenant for the log; ``null`` is the implicit tenant of a self-hosted installation. */
export function describeTenant(tenant) {
  return tenant === null ? "the implicit tenant" : `tenant ${tenant}`;
}

/** A short unsalted fingerprint of an identifier for the log; an absent value gives an empty string. */
export function fingerprint(value) {
  if (!value) {
    return "";
  }
  return createHash("sha256").update(value, "utf8").digest().subarray(0, FINGERPRINT_BYTES).toString("hex").toUpperCase();
}

/** Pauses for ``milliseconds``. */
export function sleep(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

/**
 * The raw body of a request, or ``null`` when it is longer than ``limit``.
 *
 * A declared length over the limit is refused before anything is read; a body that grows over the limit
 * while it streams in is refused at that chunk, and the rest is never read: the stream is paused, and the
 * answer closes the connection.
 */
export function readBoundedBody(request, limit = MAX_BODY_BYTES) {
  const declared = request.headers["content-length"];
  if (isWholeNumber(declared) && Number(declared) > limit) {
    return Promise.resolve(null);
  }

  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    let settled = false;
    const settle = (action) => {
      if (!settled) {
        settled = true;
        action();
      }
    };

    request.on("data", (chunk) => {
      if (settled) {
        return;
      }
      size += chunk.length;
      if (size > limit) {
        request.pause();
        settle(() => resolve(null));
        return;
      }
      chunks.push(chunk);
    });
    request.on("end", () => settle(() => resolve(Buffer.concat(chunks))));
    request.on("error", (error) => settle(() => reject(error)));
    request.on("close", () => settle(() => reject(new Error("the request closed before its body ended"))));
  });
}

/** Writes an answer without a body; a refusal of an oversized body also closes the connection. */
export function sendEmpty(response, status) {
  const headers = status === 413 ? { connection: "close" } : {};
  response.writeHead(status, headers);
  response.end();
}

/** Writes a JSON answer. */
export function sendJson(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, { "content-type": "application/json; charset=utf-8", "content-length": Buffer.byteLength(body) });
  response.end(body);
}
