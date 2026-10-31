# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Composition of the adapter: the side of the core and the side of the platform joined in one app."""

from __future__ import annotations

import asyncio
import contextlib
import errno
import logging
import sqlite3
from collections.abc import AsyncIterator

import httpx
from fastapi import FastAPI

from adapter.common import ConfigError
from adapter.core.calls import CallInput
from adapter.core.config import CoreConfig, CoreEnv
from adapter.core.events import EventDelivery
from adapter.core.idempotency import IdempotentCalls
from adapter.core.signature import TransportSignature
from adapter.core.store import AdapterStore
from adapter.telegram.bot_api import BotApi
from adapter.telegram.config import WEBHOOK_PATH_PREFIX, InboundMode, TelegramConfig
from adapter.telegram.intake import TelegramIntake
from adapter.telegram.platform import TelegramPlatform

_log = logging.getLogger(__name__)

# Path of the input of the calls of Veriqa: the base URL of the registration points at the root.
CALL_PATH = "/"


def create_app(core: CoreConfig, telegram: TelegramConfig) -> FastAPI:
    """The app: the input of the calls of Veriqa and, in the webhook mode, the webhooks of the bots."""
    store = _open_store(core)
    signature = TransportSignature(core.transport_secrets)
    # Redirects are never followed: neither Veriqa nor the Bot API answers with one.
    core_http = httpx.AsyncClient(follow_redirects=False)
    telegram_http = httpx.AsyncClient(follow_redirects=False)

    api = BotApi(telegram.api_url, telegram_http)
    platform = TelegramPlatform(telegram, api)
    delivery = EventDelivery(core.veriqa_url, core.channel_type, signature, store, core_http)
    calls = CallInput(core.channel_type, signature, platform, IdempotentCalls(store), store)
    intake = TelegramIntake(telegram, api, delivery, telegram_http)

    @contextlib.asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncIterator[None]:
        intake_task = asyncio.create_task(intake.run(), name="telegram-intake")
        intake_task.add_done_callback(_report_intake_end)
        try:
            yield
        finally:
            intake_task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await intake_task
            await core_http.aclose()
            await telegram_http.aclose()
            store.close()

    app = FastAPI(lifespan=lifespan, docs_url=None, redoc_url=None, openapi_url=None)
    app.add_api_route(CALL_PATH, calls.handle, methods=["POST"])
    if telegram.inbound_mode == InboundMode.WEBHOOK:
        app.add_api_route(WEBHOOK_PATH_PREFIX + "{bot_id}", intake.webhook, methods=["POST"])

    _log.info(
        "Adapter for channel %s: %d bot(s), %s mode.",
        core.channel_type,
        len(telegram.bots),
        telegram.inbound_mode,
    )
    return app


def _report_intake_end(task: asyncio.Task[None]) -> None:
    if not task.cancelled() and task.exception() is not None:
        _log.error("The intake of Telegram updates stopped: %s.", type(task.exception()).__name__)


def _open_store(core: CoreConfig) -> AdapterStore:
    """The store in the data directory; a directory it cannot be opened in stops the start."""
    try:
        return AdapterStore(core.data_dir, core.idempotency_ttl_seconds)
    except (OSError, sqlite3.Error) as error:
        # Only the code of the failure is given: its text carries the path.
        code = errno.errorcode.get(error.errno, type(error).__name__) if isinstance(error, OSError) else type(error).__name__
        raise ConfigError(f"{CoreEnv.DATA_DIR} names a directory the store cannot be opened in ({code})") from None
