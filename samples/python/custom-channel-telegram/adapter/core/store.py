# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""The state of the adapter that outlives a restart: results of the idempotent calls, the tenants of the
transactions and their deep links. One SQLite file on a volume.

The adapter is one process with one event loop, and every method here runs on that loop between two
awaits, so the connection needs no lock and a read followed by a write cannot interleave with another call.
"""

from __future__ import annotations

import json
import sqlite3
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

# Name of the database file inside the data directory.
DATABASE_FILE = "adapter.sqlite3"

# How often, at most, expired rows are deleted, in seconds.
_PURGE_INTERVAL_SECONDS = 60

_SCHEMA = """
CREATE TABLE IF NOT EXISTS call_results (
    operation TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    result TEXT NOT NULL,
    expires_at REAL NOT NULL,
    PRIMARY KEY (operation, idempotency_key)
);
CREATE TABLE IF NOT EXISTS transaction_routes (
    transaction_id TEXT PRIMARY KEY,
    tenant TEXT,
    expires_at REAL NOT NULL
);
CREATE TABLE IF NOT EXISTS deep_links (
    transaction_id TEXT PRIMARY KEY,
    url TEXT NOT NULL,
    expires_at REAL NOT NULL
);
"""


@dataclass(frozen=True)
class TransactionRoute:
    """The tenant whose route the events of a transaction go to; ``None`` is the implicit tenant."""

    tenant: str | None


class AdapterStore:
    """Keeps the results of the idempotent calls, the routes and the deep links of the transactions for
    ``retention_seconds``."""

    def __init__(self, data_dir: str, retention_seconds: int) -> None:
        Path(data_dir).mkdir(parents=True, exist_ok=True)
        self._connection = sqlite3.connect(Path(data_dir) / DATABASE_FILE, isolation_level=None)
        self._connection.execute("PRAGMA journal_mode=WAL")
        self._connection.executescript(_SCHEMA)
        self._retention_seconds = retention_seconds
        self._purged_at = 0.0

    def close(self) -> None:
        self._connection.close()

    def result_of(self, operation: str, key: str) -> dict[str, Any] | None:
        """The kept result of a successful call, or ``None``."""
        row = self._connection.execute(
            "SELECT result FROM call_results WHERE operation = ? AND idempotency_key = ? AND expires_at > ?",
            (operation, key, time.time()),
        ).fetchone()
        return json.loads(row[0]) if row else None

    def keep_result(self, operation: str, key: str, result: dict[str, Any]) -> None:
        """Keeps the result of a successful call under its idempotency key."""
        self._connection.execute(
            "INSERT OR REPLACE INTO call_results (operation, idempotency_key, result, expires_at) VALUES (?, ?, ?, ?)",
            (operation, key, json.dumps(result), self._expiry()),
        )
        self._purge_expired()

    def route_of(self, transaction_id: str) -> TransactionRoute | None:
        """The route of a transaction, or ``None`` when no call of Veriqa has named its tenant yet."""
        row = self._connection.execute(
            "SELECT tenant FROM transaction_routes WHERE transaction_id = ? AND expires_at > ?",
            (transaction_id, time.time()),
        ).fetchone()
        return TransactionRoute(tenant=row[0]) if row else None

    def keep_route(self, transaction_id: str, route: TransactionRoute) -> None:
        """Keeps the route of a transaction; a live route kept first is never overwritten, an expired one is."""
        self._connection.execute(
            "INSERT INTO transaction_routes (transaction_id, tenant, expires_at) VALUES (?, ?, ?) "
            "ON CONFLICT (transaction_id) DO UPDATE SET tenant = excluded.tenant, expires_at = excluded.expires_at "
            "WHERE transaction_routes.expires_at <= ?",
            (transaction_id, route.tenant, self._expiry(), time.time()),
        )
        self._purge_expired()

    def deep_link_of(self, transaction_id: str) -> str | None:
        """The deep link made for a transaction, or ``None``."""
        row = self._connection.execute(
            "SELECT url FROM deep_links WHERE transaction_id = ? AND expires_at > ?",
            (transaction_id, time.time()),
        ).fetchone()
        return row[0] if row else None

    def keep_deep_link(self, transaction_id: str, url: str) -> None:
        """Keeps the deep link of a transaction; a live link made first is never overwritten, an expired one is."""
        self._connection.execute(
            "INSERT INTO deep_links (transaction_id, url, expires_at) VALUES (?, ?, ?) "
            "ON CONFLICT (transaction_id) DO UPDATE SET url = excluded.url, expires_at = excluded.expires_at "
            "WHERE deep_links.expires_at <= ?",
            (transaction_id, url, self._expiry(), time.time()),
        )
        self._purge_expired()

    def _expiry(self) -> float:
        return time.time() + self._retention_seconds

    def _purge_expired(self) -> None:
        now = time.time()
        if now - self._purged_at < _PURGE_INTERVAL_SECONDS:
            return
        self._purged_at = now
        self._connection.execute("DELETE FROM call_results WHERE expires_at <= ?", (now,))
        self._connection.execute("DELETE FROM transaction_routes WHERE expires_at <= ?", (now,))
        self._connection.execute("DELETE FROM deep_links WHERE expires_at <= ?", (now,))
