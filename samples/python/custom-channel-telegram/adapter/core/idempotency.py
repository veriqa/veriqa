# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Idempotency of the calls with an effect the user sees: one effect per key, whatever the number of calls."""

from __future__ import annotations

import asyncio
import logging
from collections.abc import Awaitable, Callable
from typing import Any

from adapter.core.store import AdapterStore
from adapter.core.wire import ErrorCode, Field, error_envelope, result_envelope

_log = logging.getLogger(__name__)

# What a call that waited for a crashed first call with its key gets.
_CRASHED_MESSAGE = "The first call with this idempotency key failed."


class IdempotentCalls:
    """Runs a call with an effect once per idempotency key.

    - A key whose call succeeded answers with the kept result, and the platform is not called again.
    - A call that failed binds nothing: the next call with its key runs anew.
    - A call with a key whose first call is still running waits for it and answers with its outcome, so
      a repeat of Veriqa that overtakes a slow first attempt makes no second message either.

    The waiting is on a future, not on a lock, and nothing here is held while an event goes to Veriqa.
    """

    def __init__(self, store: AdapterStore) -> None:
        self._store = store
        self._running: dict[tuple[str, str], asyncio.Future[dict[str, Any]]] = {}

    async def run(self, operation: str, key: str, call: Callable[[], Awaitable[dict[str, Any]]]) -> dict[str, Any]:
        """The answer envelope of the call with ``key``: kept, awaited or made now."""
        kept = self._store.result_of(operation, key)
        if kept is not None:
            return result_envelope(kept)

        slot = (operation, key)
        running = self._running.get(slot)
        if running is not None:
            return await asyncio.shield(running)

        future: asyncio.Future[dict[str, Any]] = asyncio.get_running_loop().create_future()
        self._running[slot] = future
        try:
            envelope = await call()
        except BaseException:
            future.set_result(error_envelope(ErrorCode.MESSAGE_SEND_FAILED, _CRASHED_MESSAGE))
            raise
        finally:
            del self._running[slot]

        if Field.RESULT in envelope:
            self._store.keep_result(operation, key, envelope[Field.RESULT])
        else:
            _log.info("A %s call failed; its idempotency key stays unbound.", operation)
        future.set_result(envelope)
        return envelope
