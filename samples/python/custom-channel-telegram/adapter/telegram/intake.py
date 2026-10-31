# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Intake of the updates of Telegram: a webhook per bot or a long poll per bot, one decision per update.

Each update either becomes an event of Veriqa, goes to the bot of the integrator, or is dropped.
The adapter is the one consumer of the updates of its bots, so the bot of the integrator — when there is
one — gets what is not ours from here rather than from Telegram.
"""

from __future__ import annotations

import asyncio
import hashlib
import hmac
import json
import logging
from typing import Any

import httpx
from starlette.requests import Request
from starlette.responses import Response

from adapter.common import fingerprint, read_bounded_body
from adapter.core.seam import EventSink, InboundEvent, PlatformError
from adapter.core.wire import Field
from adapter.telegram.bot_api import BotApi
from adapter.telegram.config import BotRecord, InboundMode, TelegramConfig
from adapter.telegram.identity import avatar_of, identity_of
from adapter.telegram.updates import VERIQA_UPDATE_TYPES, Classification, Kind, classify

_log = logging.getLogger(__name__)

# The header Telegram puts the secret_token of the webhook into.
SECRET_TOKEN_HEADER = "X-Telegram-Bot-Api-Secret-Token"

# Long poll of getUpdates: how long Telegram holds the request, and the margin of the HTTP timeout over it.
LONG_POLL_SECONDS = 30
_LONG_POLL_MARGIN_SECONDS = 10.0

# Pause before the next attempt to register a webhook or to poll after a failure.
RETRY_PAUSE_SECONDS = 5.0

# How long the bot of the integrator may take to take a forwarded update.
FORWARD_TIMEOUT_SECONDS = 10.0

_JSON_CONTENT_TYPE = "application/json"


class TelegramIntake:
    """Takes the updates of the configured bots in and hands the events of Veriqa to ``sink``."""

    def __init__(self, config: TelegramConfig, api: BotApi, sink: EventSink, http: httpx.AsyncClient) -> None:
        self._config = config
        self._api = api
        self._sink = sink
        self._http = http
        self._by_bot_id = {bot.bot_id: bot for bot in config.bots}

    async def run(self) -> None:
        """Registers the webhook of every bot, or polls every bot until cancelled."""
        work = self._register_webhook if self._config.inbound_mode == InboundMode.WEBHOOK else self._poll
        await asyncio.gather(*(work(bot) for bot in self._config.bots))

    async def webhook(self, request: Request) -> Response:
        """``POST /telegram/{bot_id}``: one update of one bot."""
        bot = self._by_bot_id.get(request.path_params.get("bot_id", ""))
        if bot is None:
            return Response(status_code=404)

        # The secret of the bot is checked before the body is read: an update without it has no effect.
        presented = request.headers.get(SECRET_TOKEN_HEADER)
        if not presented:
            return Response(status_code=401)
        if not _same_secret(presented, bot.webhook_secret):
            return Response(status_code=403)

        body = await read_bounded_body(request)
        if body is None:
            return Response(status_code=413)
        try:
            update = json.loads(body)
        except ValueError:
            return Response(status_code=400)
        if not isinstance(update, dict):
            return Response(status_code=400)

        # The update is taken once: an event Veriqa did not take after the retries of the delivery is not
        # handed back to Telegram for a redelivery (see "Limits of the sample" in the README).
        await self._take(bot, body, update)
        return Response(status_code=200)

    async def _take(self, bot: BotRecord, raw: bytes, update: dict[str, Any]) -> None:
        """Decides what one update is and acts on it."""
        update_id = update.get("update_id")
        if not isinstance(update_id, int):
            _log.warning("An update of bot %s without update_id is dropped.", bot.bot_id)
            return

        classification = classify(update, self._config.accepts_codes)
        event, user = classification.event, classification.user
        is_event = classification.kind is Kind.VERIQA or (classification.kind is Kind.CONVERSATION and bot.forward_url is None)
        if is_event and event is not None and user is not None:
            await self._publish(bot, update_id, classification, event, user)
        elif bot.forward_url is not None:
            await self._forward(bot.forward_url, bot, raw, update_id)
        else:
            _log.debug("Update %d of bot %s is not an event of Veriqa and is dropped.", update_id, bot.bot_id)

    async def _publish(self, bot: BotRecord, update_id: int, classification: Classification, event: str, user: dict[str, Any]) -> None:
        avatar = await avatar_of(self._api, bot.token, user["id"]) if classification.wants_avatar else None

        fields: dict[str, Any] = {Field.IDENTITY: identity_of(user, avatar)}
        if classification.transaction_id is not None:
            fields[Field.TRANSACTION_ID] = classification.transaction_id
        if classification.inbound_token is not None:
            fields[Field.INBOUND_TOKEN] = classification.inbound_token
        if classification.phone_number is not None:
            fields[Field.PHONE_NUMBER] = classification.phone_number
        if classification.text is not None:
            # The text of the message is carried, never logged.
            fields[Field.TEXT] = classification.text
        if classification.code is not None:
            # The code of a sign-in is carried, never logged.
            fields[Field.CODE] = classification.code

        _log.info("Update %d of bot %s from user %s is a %s event.", update_id, bot.bot_id, fingerprint(str(user["id"])), event)
        # The identifier of the event follows from the bot and the update: a repeated delivery of the update,
        # by Telegram or by this adapter, reaches Veriqa under the same webhook-id.
        await self._sink.publish(
            InboundEvent(
                event_id=f"tg-{bot.bot_id}-{update_id}",
                name=event,
                fields=fields,
                fallback_tenant=bot.tenant,
            )
        )

    async def _forward(self, forward_url: str, bot: BotRecord, raw: bytes, update_id: int) -> None:
        """Hands an update that is not ours to the bot of the integrator: in the webhook mode the body Telegram
        posted, byte for byte; in the polling mode the update of getUpdates, serialized again."""
        try:
            response = await self._http.post(
                forward_url,
                content=raw,
                headers={"content-type": _JSON_CONTENT_TYPE},
                timeout=FORWARD_TIMEOUT_SECONDS,
            )
        except httpx.HTTPError as exception:
            _log.warning("Update %d of bot %s was not forwarded: %s.", update_id, bot.bot_id, type(exception).__name__)
            return
        if not response.is_success:
            _log.warning("Update %d of bot %s was not forwarded: status %d.", update_id, bot.bot_id, response.status_code)

    async def _register_webhook(self, bot: BotRecord) -> None:
        """Points the webhook of the bot at this adapter, with the secret of the bot; retried until it holds."""
        parameters = {
            "url": f"{self._config.public_url}{bot.webhook_path}",
            "secret_token": bot.webhook_secret,
            "allowed_updates": _allowed_updates(bot),
        }
        while True:
            try:
                await self._api.call(bot.token, "setWebhook", parameters)
            except PlatformError as error:
                _log.warning("Webhook of bot %s is not registered yet: %s.", bot.bot_id, error)
                await asyncio.sleep(RETRY_PAUSE_SECONDS)
                continue
            _log.info("Webhook of bot %s is registered.", bot.bot_id)
            return

    async def _poll(self, bot: BotRecord) -> None:
        """Takes the updates of the bot in with getUpdates, one at a time, until cancelled."""
        offset: int | None = None
        webhook_removed = False
        while True:
            try:
                if not webhook_removed:
                    # getUpdates does not work while a webhook is set.
                    await self._api.call(bot.token, "deleteWebhook")
                    webhook_removed = True
                parameters: dict[str, Any] = {"timeout": LONG_POLL_SECONDS, "allowed_updates": _allowed_updates(bot)}
                if offset is not None:
                    parameters["offset"] = offset
                updates = await self._api.call(bot.token, "getUpdates", parameters, timeout=LONG_POLL_SECONDS + _LONG_POLL_MARGIN_SECONDS)
            except PlatformError as error:
                _log.warning("Polling of bot %s failed: %s.", bot.bot_id, error)
                await asyncio.sleep(RETRY_PAUSE_SECONDS)
                continue

            for update in updates if isinstance(updates, list) else []:
                if not isinstance(update, dict) or not isinstance(update.get("update_id"), int):
                    continue
                # The offset moves past the update whatever becomes of it, as the webhook answers 200.
                offset = update["update_id"] + 1
                try:
                    await self._take(bot, json.dumps(update).encode("utf-8"), update)
                except Exception as exception:  # one bad update must not stop the polling of the bot
                    _log.error("Update %d of bot %s failed: %s.", offset - 1, bot.bot_id, type(exception).__name__)


def _allowed_updates(bot: BotRecord) -> list[str]:
    """The types of updates the bot asks Telegram for, named every time: left out, Telegram keeps the setting
    an earlier user of the token left, which may leave out the presses of the buttons.

    A bot with a forward address asks for the default set of Telegram (an empty list: every type but
    ``chat_member``, ``message_reaction`` and ``message_reaction_count``), since the bot of the integrator
    owns the conversation; a bot without one asks only for what the classification reads.
    """
    return [] if bot.forward_url is not None else list(VERIQA_UPDATE_TYPES)


def _same_secret(presented: str, expected: str) -> bool:
    """Constant-time comparison of two secrets of any length: their SHA-256 digests are compared."""
    return hmac.compare_digest(
        hashlib.sha256(presented.encode("utf-8")).digest(),
        hashlib.sha256(expected.encode("utf-8")).digest(),
    )
