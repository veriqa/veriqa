// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// A thin client of the REST API of Twilio, over fetch with the Basic authentication of an account.
//
// The base URL is a setting — the API of Twilio or a stand-in — and every request of the adapter goes to it.
// An error carries the resource, the HTTP status and the numeric code of Twilio, never the message of
// Twilio: that message may quote the phone number of the request.

import { PlatformError } from "../core/seam.js";

/** Version segment of the REST API of Twilio. */
const API_VERSION = "2010-04-01";

/** How long one REST request may take, in milliseconds. */
export const REQUEST_TIMEOUT_MS = 10_000;

/** Twilio refused a request or could not be reached; ``code`` is the numeric code of Twilio, if any. */
export class TwilioError extends PlatformError {
  constructor(message, code = null) {
    super(message);
    this.code = code;
  }
}

/** A message resource: its SID, its status and, for a failed one, the error code of Twilio. */
function messageResource(answer) {
  if (typeof answer?.sid !== "string" || !answer.sid) {
    throw new TwilioError("message resource: no sid");
  }
  return {
    sid: answer.sid,
    status: typeof answer.status === "string" ? answer.status : "",
    errorCode: codeOf(answer.error_code),
  };
}

export class TwilioRest {
  constructor(baseUrl) {
    this.baseUrl = baseUrl;
  }

  /** Creates a message: ``POST /Accounts/{sid}/Messages.json`` with a form. */
  async createMessage(account, form) {
    return messageResource(await this.request(account, "POST", "/Messages.json", "create message", form));
  }

  /** Reads a message: ``GET /Accounts/{sid}/Messages/{message sid}.json``. */
  async fetchMessage(account, messageSid, timeoutMs) {
    const path = `/Messages/${encodeURIComponent(messageSid)}.json`;
    return messageResource(await this.request(account, "GET", path, "fetch message", null, timeoutMs));
  }

  /** Reads the account itself: ``GET /Accounts/{sid}.json``; its ``status`` is ``active`` when it may send. */
  async fetchAccount(account) {
    const answer = await this.request(account, "GET", ".json", "fetch account", null);
    return { status: typeof answer?.status === "string" ? answer.status : "" };
  }

  async request(account, method, path, resource, form, timeoutMs = REQUEST_TIMEOUT_MS) {
    const url = `${this.baseUrl}/${API_VERSION}/Accounts/${encodeURIComponent(account.accountSid)}${path}`;
    const headers = {
      authorization: `Basic ${Buffer.from(`${account.accountSid}:${account.authToken}`, "utf8").toString("base64")}`,
      accept: "application/json",
    };
    let body;
    if (form !== null) {
      headers["content-type"] = "application/x-www-form-urlencoded";
      body = new URLSearchParams(form).toString();
    }

    let response;
    try {
      response = await fetch(url, { method, headers, body, redirect: "manual", signal: AbortSignal.timeout(timeoutMs) });
    } catch (error) {
      throw new TwilioError(`${resource}: ${error.name}`);
    }

    let answer;
    try {
      answer = JSON.parse(await response.text());
    } catch {
      answer = null;
    }
    if (!response.ok) {
      const code = codeOf(answer?.code);
      throw new TwilioError(`${resource}: status ${response.status}${code === null ? "" : `, code ${code}`}`, code);
    }
    if (answer === null) {
      throw new TwilioError(`${resource}: status ${response.status} without a JSON answer`);
    }
    return answer;
  }
}

/** A numeric error code of Twilio, or ``null``. */
function codeOf(value) {
  if (typeof value === "number" && Number.isInteger(value)) {
    return value;
  }
  if (typeof value === "string" && /^[0-9]+$/.test(value)) {
    return Number(value);
  }
  return null;
}
