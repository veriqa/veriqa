# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""The operations of the core on Telegram: the implementation of ``adapter.core.seam.Platform``."""

from __future__ import annotations

import asyncio
import logging
from typing import Any

from adapter.common import fingerprint
from adapter.core.seam import HealthReport, MessageRef, OutcomeRequest, PhoneNumberRequest, PlatformError, PromptRequest
from adapter.core.wire import MessageKind
from adapter.telegram.bot_api import BotApi
from adapter.telegram.config import BotRecord, TelegramConfig
from adapter.telegram.settings import disable_notification
from adapter.telegram.updates import CONFIRM_PREFIX, DEEP_LINK_MARKER, DECLINE_PREFIX

_log = logging.getLogger(__name__)

# Where a deep link of a bot leads.
DEEP_LINK_BASE_URL = "https://t.me/"

# Longest parameter of a /start deep link Telegram accepts.
MAX_START_PARAMETER_LENGTH = 64


class TelegramPlatform:
    """Sends the prompts, messages and outcomes of Veriqa with the bot of the tenant of the call.

    Messages go as plain text, without a parse mode: the text Veriqa rendered is shown as it is, and there
    is nothing to escape.
    """

    def __init__(self, config: TelegramConfig, api: BotApi) -> None:
        self._api = api
        self._link_carries_transaction = config.link_carries_transaction
        self._bots = config.bots
        self._by_tenant = {bot.tenant: bot for bot in config.bots}
        self._usernames: dict[str, str] = {}

    def account_for(self, tenant: str | None) -> BotRecord | None:
        """The bot of the tenant; a tenant without a bot of its own uses the default one, if there is one."""
        return self._by_tenant.get(tenant) or self._by_tenant.get(None)

    async def send_prompt(self, account: BotRecord, settings: str | None, request: PromptRequest) -> MessageRef:
        keyboard = {
            "inline_keyboard": [[
                {"text": request.confirm_label, "callback_data": CONFIRM_PREFIX + request.transaction_id},
                {"text": request.decline_label, "callback_data": DECLINE_PREFIX + request.transaction_id},
            ]]
        }
        message = await self._send_text(account, settings, request.channel_user_id, request.text, keyboard)
        _log.info("Prompt sent to user %s by bot %s.", fingerprint(request.channel_user_id), account.bot_id)
        return message

    async def send_message(
        self,
        account: BotRecord,
        settings: str | None,
        channel_user_id: str,
        text: str,
        kind: str,
        phone_number_request: PhoneNumberRequest | None,
    ) -> MessageRef:
        if kind == MessageKind.PHONE_NUMBER_REQUEST and phone_number_request is not None:
            # A reply keyboard, as the built-in Telegram channel shows it: the button that shares the
            # contact of the user, and under it the secondary action. A reply button carries no payload —
            # its press comes back as a message with the caption as its text, which reaches Veriqa in an
            # unaddressed event. one_time_keyboard only folds the keyboard away after a press; the final
            # outcome of the sign-in is what takes it down (see report_outcome).
            keyboard = {
                "keyboard": [
                    [{"text": phone_number_request.share_button_label, "request_contact": True}],
                    [{"text": phone_number_request.secondary_button_label}],
                ],
                "one_time_keyboard": True,
                "resize_keyboard": True,
            }
            message = await self._send_text(account, settings, channel_user_id, text, keyboard)
            _log.info("Phone number request sent to user %s by bot %s.", fingerprint(channel_user_id), account.bot_id)
            return message
        if kind != MessageKind.PLAIN_TEXT:
            raise PlatformError(f"the kind of content {kind} is not supported")
        return await self._send_text(account, settings, channel_user_id, text)

    async def report_outcome(self, account: BotRecord, settings: str | None, request: OutcomeRequest) -> None:
        # The button press is owed an answer whatever the outcome shows: it stops the loading indicator.
        # Its failure is not a failure to show the outcome.
        if request.inbound_token is not None:
            try:
                await self._api.call(account.token, "answerCallbackQuery", {"callback_query_id": request.inbound_token})
            except PlatformError as error:
                _log.warning("The button press was not answered: %s.", error)

        if request.phone_request_message is not None:
            # A final outcome that names a phone number request takes its keyboard down, as the built-in
            # Telegram channel does. A reply keyboard goes only with a new message — an edit accepts inline
            # markup alone — so the outcome is sent as one whatever the intent says.
            await self._send_text(
                account, settings, request.channel_user_id, request.status_text, {"remove_keyboard": True}
            )
            return

        if request.replace_prompt and request.prompt_message is not None:
            # Editing the text without a keyboard takes the buttons of the prompt off.
            await self._api.call(
                account.token,
                "editMessageText",
                {
                    "chat_id": request.prompt_message.chat_id,
                    "message_id": _message_number(request.prompt_message.message_id),
                    "text": request.status_text,
                },
            )
            return
        await self._send_text(account, settings, request.channel_user_id, request.status_text)

    async def deep_link(self, account: BotRecord, transaction_id: str) -> str:
        chat = f"{DEEP_LINK_BASE_URL}{await self._username_of(account)}"
        if not self._link_carries_transaction:
            # A sign-in starts by a code alone: the link only opens the chat with the bot.
            return chat
        parameter = DEEP_LINK_MARKER + transaction_id
        if len(parameter) > MAX_START_PARAMETER_LENGTH:
            raise PlatformError("the deep link parameter is too long")
        return f"{chat}?start={parameter}"

    async def health(self) -> HealthReport:
        probes = await asyncio.gather(*(self._probe(bot) for bot in self._bots))
        failed = [bot.bot_id for bot, healthy in zip(self._bots, probes) if not healthy]
        if not failed:
            return HealthReport(healthy=True, details=None)
        return HealthReport(healthy=False, details=f"getMe failed for bot {', '.join(failed)}")

    async def _send_text(
        self,
        account: BotRecord,
        settings: str | None,
        chat_id: str,
        text: str,
        keyboard: dict[str, Any] | None = None,
    ) -> MessageRef:
        parameters: dict[str, Any] = {
            "chat_id": chat_id,
            "text": text,
            "disable_notification": disable_notification(settings),
        }
        if keyboard is not None:
            parameters["reply_markup"] = keyboard
        message = await self._api.call(account.token, "sendMessage", parameters)
        try:
            return MessageRef(chat_id=str(message["chat"]["id"]), message_id=str(message["message_id"]))
        except (KeyError, TypeError):
            raise PlatformError("sendMessage: the answer carries no message coordinates") from None

    async def _username_of(self, account: BotRecord) -> str:
        """The username of a bot, asked of Telegram once and kept for the life of the process."""
        username = self._usernames.get(account.bot_id)
        if username is None:
            me = await self._api.call(account.token, "getMe")
            username = me.get("username") if isinstance(me, dict) else None
            if not isinstance(username, str) or not username:
                raise PlatformError("getMe: the bot has no username")
            self._usernames[account.bot_id] = username
        return username

    async def _probe(self, bot: BotRecord) -> bool:
        try:
            me = await self._api.call(bot.token, "getMe")
        except PlatformError as error:
            _log.warning("Health probe of bot %s failed: %s.", bot.bot_id, error)
            return False
        if isinstance(me, dict) and isinstance(me.get("username"), str):
            self._usernames[bot.bot_id] = me["username"]
        return True


def _message_number(message_id: str) -> int:
    """The Bot API identifies a message by a number; the wire carries it as a string."""
    if not message_id.isascii() or not message_id.isdigit():
        raise PlatformError("the prompt message identifier is not a Telegram message number")
    return int(message_id)
