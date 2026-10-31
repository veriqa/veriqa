// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The transport signature, both directions: Standard Webhooks, symmetric scheme v1 (HMAC-SHA256).

import { Webhook } from "standardwebhooks";

import { WEBHOOK_ID_HEADER, WEBHOOK_SIGNATURE_HEADER, WEBHOOK_TIMESTAMP_HEADER } from "./wire.js";

// Separator of the signatures in one header: one per secret while a secret is rotated.
const SIGNATURE_SEPARATOR = " ";

/**
 * Checks the signature of a call of Veriqa and signs the events the adapter posts to Veriqa.
 *
 * The secrets are the current transport secret and, during a rotation, the previous one. A call is
 * authentic when any signature of its header matches any of them — the library takes every signature of
 * the header in turn; an event is signed with all of them, so Veriqa accepts it whichever secret it holds
 * at the moment. The window of the timestamp — five minutes either way — and the constant-time comparison
 * are the ones of the Standard Webhooks library.
 */
export class TransportSignature {
  constructor(secrets) {
    this.webhooks = secrets.map((secret) => new Webhook(secret));
  }

  /** Whether a received message is signed with a known secret inside the window of its timestamp. */
  verify(headers, body) {
    const signed = {
      [WEBHOOK_ID_HEADER]: headerValue(headers, WEBHOOK_ID_HEADER),
      [WEBHOOK_TIMESTAMP_HEADER]: headerValue(headers, WEBHOOK_TIMESTAMP_HEADER),
      [WEBHOOK_SIGNATURE_HEADER]: headerValue(headers, WEBHOOK_SIGNATURE_HEADER),
    };
    for (const webhook of this.webhooks) {
      try {
        webhook.verify(body, signed, { jsonParse: false });
        return true;
      } catch {
        // Any refusal of the library, a malformed header among them, means "not authentic" for this secret.
      }
    }
    return false;
  }

  /** The signature headers of a message: ``body`` is the exact text whose UTF-8 bytes are sent. */
  sign(webhookId, body) {
    const signedAt = Math.floor(Date.now() / 1000);
    const moment = new Date(signedAt * 1000);
    const signatures = this.webhooks.map((webhook) => webhook.sign(webhookId, moment, body));
    return {
      [WEBHOOK_ID_HEADER]: webhookId,
      [WEBHOOK_TIMESTAMP_HEADER]: String(signedAt),
      [WEBHOOK_SIGNATURE_HEADER]: signatures.join(SIGNATURE_SEPARATOR),
    };
  }
}

/** The value of a header of a Node request; an absent header reads as empty, which the library refuses. */
function headerValue(headers, name) {
  const value = headers[name];
  return typeof value === "string" ? value : "";
}
