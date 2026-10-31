// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The prompts of the adapter — polls — kept in the database of the adapter: without the poll a vote is not
// read, since a vote names the poll and an option of it, never the transaction. The tenant of the
// transaction is not kept here: the route table of the side of the core holds it, recorded before the poll
// goes out, and the events of the votes take it from there.
//
// Like the store of the side of the core, every method runs to its end between two awaits of the one event
// loop, so the claim of a vote cannot interleave with another claim.

// How often, at most, expired polls are deleted, in milliseconds.
const PURGE_INTERVAL_MS = 60_000;

const SCHEMA = `
CREATE TABLE IF NOT EXISTS polls (
    label TEXT NOT NULL,
    message_id TEXT NOT NULL,
    transaction_id TEXT NOT NULL,
    recipient TEXT NOT NULL,
    confirm_option TEXT NOT NULL,
    decline_option TEXT NOT NULL,
    secret BLOB NOT NULL,
    vote_message_id TEXT,
    expires_at INTEGER NOT NULL,
    PRIMARY KEY (label, message_id)
);
`;

/**
 * One poll of a prompt: the record it went out from, its message, the transaction, the recipient in E.164,
 * the option of each answer and the secret its votes are encrypted with.
 *
 * @typedef {object} Poll
 * @property {string} label
 * @property {string} messageId
 * @property {string} transactionId
 * @property {string} recipient
 * @property {string} confirmOption
 * @property {string} declineOption
 * @property {Buffer} secret
 */

/** Keeps the polls of the prompts for ``retentionSeconds`` — the retention of the idempotency keys. */
export class PollStore {
  constructor(database, retentionSeconds) {
    this.database = database;
    this.database.exec(SCHEMA);
    this.retentionMs = retentionSeconds * 1000;
    this.purgedAt = 0;

    this.insertPoll = database.prepare(
      "INSERT OR REPLACE INTO polls (label, message_id, transaction_id, recipient, confirm_option, decline_option, "
        + "secret, vote_message_id, expires_at) VALUES (?, ?, ?, ?, ?, ?, ?, NULL, ?)",
    );
    this.selectPoll = database.prepare(
      "SELECT transaction_id, recipient, confirm_option, decline_option, secret "
        + "FROM polls WHERE label = ? AND message_id = ? AND expires_at > ?",
    );
    this.updateVote = database.prepare(
      "UPDATE polls SET vote_message_id = ? WHERE label = ? AND message_id = ? AND expires_at > ? "
        + "AND (vote_message_id IS NULL OR vote_message_id = ?)",
    );
    this.clearVote = database.prepare(
      "UPDATE polls SET vote_message_id = NULL WHERE label = ? AND message_id = ? AND vote_message_id = ?",
    );
  }

  /** Keeps a poll before it goes out: a vote on it may come in before the sending returns. */
  keep(poll) {
    this.insertPoll.run(
      poll.label,
      poll.messageId,
      poll.transactionId,
      poll.recipient,
      poll.confirmOption,
      poll.declineOption,
      poll.secret,
      Date.now() + this.retentionMs,
    );
    this.purgeExpired();
  }

  /** The poll ``messageId`` of the record ``label``, or ``null`` when it is unknown or expired. */
  pollOf(label, messageId) {
    const row = this.selectPoll.get(label, messageId, Date.now());
    if (!row) {
      return null;
    }
    return {
      label,
      messageId,
      transactionId: row.transaction_id,
      recipient: row.recipient,
      confirmOption: row.confirm_option,
      declineOption: row.decline_option,
      secret: Buffer.from(row.secret),
    };
  }

  /**
   * Binds the answer of a poll to the vote message ``voteMessageId``; ``true`` when the poll is answered by
   * that message — the first vote, or a repeated delivery of it — and ``false`` for any later vote.
   */
  claimVote(label, messageId, voteMessageId) {
    return this.updateVote.run(voteMessageId, label, messageId, Date.now(), voteMessageId).changes > 0;
  }

  /** Unbinds the answer of a poll from the vote message ``voteMessageId`` whose event was not delivered. */
  releaseVote(label, messageId, voteMessageId) {
    this.clearVote.run(label, messageId, voteMessageId);
  }

  purgeExpired() {
    const now = Date.now();
    if (now - this.purgedAt < PURGE_INTERVAL_MS) {
      return;
    }
    this.purgedAt = now;
    this.database.prepare("DELETE FROM polls WHERE expires_at <= ?").run(now);
  }
}
