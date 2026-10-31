// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Idempotency of the calls with an effect the user sees: one effect per key, whatever the number of calls.

import { logger } from "../common.js";
import { ErrorCode, Field, errorEnvelope, resultEnvelope } from "./wire.js";

const log = logger("core.idempotency");

// What a call that waited for a crashed first call with its key gets.
const CRASHED_MESSAGE = "The first call with this idempotency key failed.";

/**
 * Runs a call with an effect once per idempotency key.
 *
 * - A key whose call succeeded answers with the kept result, and the platform is not called again.
 * - A call that failed binds nothing: the next call with its key runs anew.
 * - A call with a key whose first call is still running waits for it and answers with its outcome, so a
 *   repeat of Veriqa that overtakes a slow first attempt makes no second message either.
 *
 * The waiting is on a promise, not on a lock, and nothing here is held while an event goes to Veriqa.
 */
export class IdempotentCalls {
  constructor(store) {
    this.store = store;
    this.running = new Map();
  }

  /** The answer envelope of the call with ``key``: kept, awaited or made now. */
  async run(operation, key, call) {
    const kept = this.store.resultOf(operation, key);
    if (kept !== null) {
      return resultEnvelope(kept);
    }

    // The slot names the operation and the key; JSON keeps the two apart whatever characters they hold.
    const slot = JSON.stringify([operation, key]);
    const running = this.running.get(slot);
    if (running !== undefined) {
      return running;
    }

    const attempt = this.attempt(operation, key, call);
    // Callers that wait get an envelope, never a rejection: a crash of the first call is a refusal to them.
    const shared = attempt.catch(() => errorEnvelope(ErrorCode.MESSAGE_SEND_FAILED, CRASHED_MESSAGE));
    this.running.set(slot, shared);
    try {
      return await attempt;
    } finally {
      this.running.delete(slot);
    }
  }

  async attempt(operation, key, call) {
    const envelope = await call();
    if (Field.RESULT in envelope) {
      this.store.keepResult(operation, key, envelope[Field.RESULT]);
    } else {
      log.info(`A ${operation} call failed; its idempotency key stays unbound.`);
    }
    return envelope;
  }
}
