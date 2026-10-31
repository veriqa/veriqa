// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The state of the adapter that outlives a restart: results of the idempotent calls, the tenants of the
// transactions and their deep links. One SQLite file on a volume, through the built-in node:sqlite.
//
// The adapter is one process with one event loop, and the synchronous API of node:sqlite runs every method
// here to its end between two awaits, so a read followed by a write cannot interleave with another call.

import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";

/** Name of the database file inside the data directory. */
export const DATABASE_FILE = "adapter.sqlite3";

// How often, at most, expired rows are deleted, in milliseconds.
const PURGE_INTERVAL_MS = 60_000;

const SCHEMA = `
CREATE TABLE IF NOT EXISTS call_results (
    operation TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    result TEXT NOT NULL,
    expires_at INTEGER NOT NULL,
    PRIMARY KEY (operation, idempotency_key)
);
CREATE TABLE IF NOT EXISTS transaction_routes (
    transaction_id TEXT PRIMARY KEY,
    tenant TEXT,
    expires_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS deep_links (
    transaction_id TEXT NOT NULL,
    purpose TEXT NOT NULL,
    url TEXT NOT NULL,
    expires_at INTEGER NOT NULL,
    PRIMARY KEY (transaction_id, purpose)
);
`;

/**
 * Keeps the results of the idempotent calls, the routes and the deep links of the transactions for
 * ``retentionSeconds``. A route is ``{ tenant }``, where ``null`` is the implicit tenant. A deep link is kept
 * per transaction and purpose: the text of the link depends on the purpose.
 */
export class AdapterStore {
  constructor(dataDir, retentionSeconds) {
    mkdirSync(dataDir, { recursive: true });
    this.database = new DatabaseSync(join(dataDir, DATABASE_FILE));
    this.database.exec("PRAGMA journal_mode=WAL");
    dropDeepLinksWithoutPurpose(this.database);
    this.database.exec(SCHEMA);
    this.retentionMs = retentionSeconds * 1000;
    this.purgedAt = 0;

    this.selectResult = this.database.prepare(
      "SELECT result FROM call_results WHERE operation = ? AND idempotency_key = ? AND expires_at > ?",
    );
    this.upsertResult = this.database.prepare(
      "INSERT OR REPLACE INTO call_results (operation, idempotency_key, result, expires_at) VALUES (?, ?, ?, ?)",
    );
    this.selectRoute = this.database.prepare(
      "SELECT tenant FROM transaction_routes WHERE transaction_id = ? AND expires_at > ?",
    );
    this.insertRoute = this.database.prepare(
      "INSERT INTO transaction_routes (transaction_id, tenant, expires_at) VALUES (?, ?, ?) "
        + "ON CONFLICT (transaction_id) DO UPDATE SET tenant = excluded.tenant, expires_at = excluded.expires_at "
        + "WHERE transaction_routes.expires_at <= ?",
    );
    this.selectDeepLink = this.database.prepare(
      "SELECT url FROM deep_links WHERE transaction_id = ? AND purpose = ? AND expires_at > ?",
    );
    this.insertDeepLink = this.database.prepare(
      "INSERT INTO deep_links (transaction_id, purpose, url, expires_at) VALUES (?, ?, ?, ?) "
        + "ON CONFLICT (transaction_id, purpose) DO UPDATE SET url = excluded.url, expires_at = excluded.expires_at "
        + "WHERE deep_links.expires_at <= ?",
    );
  }

  close() {
    this.database.close();
  }

  /** The kept result of a successful call, or ``null``. */
  resultOf(operation, key) {
    const row = this.selectResult.get(operation, key, Date.now());
    return row ? JSON.parse(row.result) : null;
  }

  /** Keeps the result of a successful call under its idempotency key. */
  keepResult(operation, key, result) {
    this.upsertResult.run(operation, key, JSON.stringify(result), this.expiry());
    this.purgeExpired();
  }

  /** The route of a transaction, or ``null`` when no call of Veriqa has named its tenant yet. */
  routeOf(transactionId) {
    const row = this.selectRoute.get(transactionId, Date.now());
    return row ? { tenant: row.tenant } : null;
  }

  /** Keeps the route of a transaction; a live route kept first is never overwritten, an expired one is. */
  keepRoute(transactionId, route) {
    this.insertRoute.run(transactionId, route.tenant, this.expiry(), Date.now());
    this.purgeExpired();
  }

  /** The deep link made for a transaction and a purpose, or ``null``. */
  deepLinkOf(transactionId, purpose) {
    const row = this.selectDeepLink.get(transactionId, purpose, Date.now());
    return row ? row.url : null;
  }

  /**
   * Keeps the deep link of a transaction for a purpose; a live link made first is never overwritten, an expired
   * one is.
   */
  keepDeepLink(transactionId, purpose, url) {
    this.insertDeepLink.run(transactionId, purpose, url, this.expiry(), Date.now());
    this.purgeExpired();
  }

  expiry() {
    return Date.now() + this.retentionMs;
  }

  purgeExpired() {
    const now = Date.now();
    if (now - this.purgedAt < PURGE_INTERVAL_MS) {
      return;
    }
    this.purgedAt = now;
    this.database.prepare("DELETE FROM call_results WHERE expires_at <= ?").run(now);
    this.database.prepare("DELETE FROM transaction_routes WHERE expires_at <= ?").run(now);
    this.database.prepare("DELETE FROM deep_links WHERE expires_at <= ?").run(now);
  }
}

/**
 * Drops the table of deep links a data directory of an earlier version of the adapter has — one link per
 * transaction, without the purpose. Its links are not carried over: the next deep-link call makes the link again.
 */
function dropDeepLinksWithoutPurpose(database) {
  const columns = database.prepare("PRAGMA table_info(deep_links)").all();
  if (columns.length > 0 && !columns.some((column) => column.name === "purpose")) {
    database.exec("DROP TABLE deep_links");
  }
}
