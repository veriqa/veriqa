# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Settings of the side of the platform, read from the environment once at the start.

Credentials are records of bots. The record without a tenant is the default one: the implicit tenant and
every tenant without a record of its own use it. A record with a tenant serves that tenant alone.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import re
from collections.abc import Mapping
from dataclasses import dataclass, field
from urllib.parse import urlsplit

from adapter.common import ConfigError, optional, required


class TelegramEnv:
    """Names of the environment variables of the side of the platform."""

    API_URL = "TELEGRAM_API_URL"
    INBOUND_MODE = "TELEGRAM_INBOUND_MODE"
    PUBLIC_URL = "TELEGRAM_PUBLIC_URL"
    BOT_TOKEN = "TELEGRAM_BOT_TOKEN"
    BOT_FORWARD_URL = "TELEGRAM_BOT_FORWARD_URL"
    CODE_START = "CODE_START"

    # Records of the bots of tenants: TELEGRAM_BOT_<n>_TENANT, _TOKEN and _FORWARD_URL, n = 1, 2, ...
    NUMBERED_TOKEN = re.compile(r"\ATELEGRAM_BOT_([0-9]+)_TOKEN\Z")

    @staticmethod
    def numbered(number: str, suffix: str) -> str:
        return f"TELEGRAM_BOT_{number}_{suffix}"


class InboundMode:
    """How the adapter takes the updates of Telegram in."""

    WEBHOOK = "webhook"
    POLLING = "polling"

    ALL = (WEBHOOK, POLLING)


class CodeStartMode:
    """Whether a user may start a sign-in by sending the short code the sign-in window shows.

    The mode has to match the declaration of the registration in Veriqa,
    ``Http:Capabilities:CodeStart:LinkCarriesTransaction``: ``fallback`` goes with ``true``, ``only`` with
    ``false``, ``off`` with the key not set.
    """

    # No code: a message of digits is a message like any other.
    OFF = "off"
    # The deep link still carries the transaction; a message of digits is a start by a code as well.
    FALLBACK = "fallback"
    # The deep link only opens the chat with the bot; a sign-in starts by a code alone.
    ONLY = "only"

    ALL = (OFF, FALLBACK, ONLY)


DEFAULT_API_URL = "https://api.telegram.org"

# Path of the webhook of one bot on the adapter; the bot is named by the public numeric part of its token.
WEBHOOK_PATH_PREFIX = "/telegram/"

# A bot token: the numeric identifier of the bot, a colon, the secret part.
_TOKEN_PATTERN = re.compile(r"\A([0-9]+):[A-Za-z0-9_-]+\Z")

# Label of the derivation of the secret_token of a webhook from the token of its bot.
_WEBHOOK_SECRET_LABEL = b"veriqa-reference-adapter/webhook-secret-token"

_URL_SCHEMES = ("http", "https")


@dataclass(frozen=True)
class BotRecord:
    """One bot: the tenant it serves (``None`` — the default record), its token and where the updates that
    are not events of Veriqa go."""

    tenant: str | None
    token: str = field(repr=False)
    forward_url: str | None = field(repr=False)

    @property
    def bot_id(self) -> str:
        """The numeric identifier of the bot: the part of the token before the colon, not a secret."""
        return self.token.split(":", 1)[0]

    @property
    def webhook_secret(self) -> str:
        """The ``secret_token`` of the webhook of this bot, derived from its token.

        Only the holder of the token can register the webhook and so learn the value; each bot gets its own,
        and a restart of the adapter gets the same one.
        """
        digest = hmac.new(self.token.encode("utf-8"), _WEBHOOK_SECRET_LABEL, hashlib.sha256).digest()
        return base64.urlsafe_b64encode(digest).decode("ascii").rstrip("=")

    @property
    def webhook_path(self) -> str:
        return WEBHOOK_PATH_PREFIX + self.bot_id


@dataclass(frozen=True)
class TelegramConfig:
    """Where the Bot API is, how updates come in, and the bots."""

    api_url: str
    inbound_mode: str
    public_url: str | None
    bots: tuple[BotRecord, ...]
    code_start: str

    @classmethod
    def from_env(cls, env: Mapping[str, str]) -> TelegramConfig:
        api_url = _base_url(TelegramEnv.API_URL, optional(env, TelegramEnv.API_URL) or DEFAULT_API_URL).rstrip("/")

        inbound_mode = required(env, TelegramEnv.INBOUND_MODE)
        if inbound_mode not in InboundMode.ALL:
            raise ConfigError(f"{TelegramEnv.INBOUND_MODE} must be one of {', '.join(InboundMode.ALL)}")

        public_url = None
        if inbound_mode == InboundMode.WEBHOOK:
            public_url = _base_url(TelegramEnv.PUBLIC_URL, required(env, TelegramEnv.PUBLIC_URL)).rstrip("/")

        code_start = optional(env, TelegramEnv.CODE_START) or CodeStartMode.OFF
        if code_start not in CodeStartMode.ALL:
            raise ConfigError(f"{TelegramEnv.CODE_START} must be one of {', '.join(CodeStartMode.ALL)}")

        bots = _bot_records(env)
        return cls(api_url=api_url, inbound_mode=inbound_mode, public_url=public_url, bots=bots, code_start=code_start)

    @property
    def accepts_codes(self) -> bool:
        """Whether a message of digits is a start by a code."""
        return self.code_start != CodeStartMode.OFF

    @property
    def link_carries_transaction(self) -> bool:
        """Whether the deep link names the transaction, or only opens the chat with the bot."""
        return self.code_start != CodeStartMode.ONLY


def _bot_records(env: Mapping[str, str]) -> tuple[BotRecord, ...]:
    records: list[BotRecord] = []

    default_token = optional(env, TelegramEnv.BOT_TOKEN)
    if default_token is not None:
        records.append(_record(env, None, TelegramEnv.BOT_TOKEN, default_token, TelegramEnv.BOT_FORWARD_URL))

    numbers = sorted(
        (match.group(1) for match in map(TelegramEnv.NUMBERED_TOKEN.match, env) if match is not None),
        key=int,
    )
    for number in numbers:
        tenant_name = TelegramEnv.numbered(number, "TENANT")
        token_name = TelegramEnv.numbered(number, "TOKEN")
        records.append(
            _record(env, required(env, tenant_name), token_name, required(env, token_name), TelegramEnv.numbered(number, "FORWARD_URL"))
        )

    if not records:
        raise ConfigError(f"no bot is configured: set {TelegramEnv.BOT_TOKEN} or {TelegramEnv.numbered('1', 'TOKEN')}")

    tenants = [record.tenant for record in records]
    if len(set(tenants)) != len(tenants):
        raise ConfigError("two bot records name the same tenant")
    bot_ids = [record.bot_id for record in records]
    if len(set(bot_ids)) != len(bot_ids):
        raise ConfigError("two bot records carry the same bot")
    return tuple(records)


def _record(env: Mapping[str, str], tenant: str | None, token_name: str, token: str, forward_name: str) -> BotRecord:
    if not _TOKEN_PATTERN.match(token):
        raise ConfigError(f"{token_name} is not a bot token of the form <bot id>:<secret>")
    forward = optional(env, forward_name)
    forward_url = _absolute_url(forward_name, forward) if forward is not None else None
    return BotRecord(tenant=tenant, token=token, forward_url=forward_url)


def _absolute_url(name: str, value: str) -> str:
    """An absolute http or https URL used as it is: a query is its own, a fragment is refused.

    The raw value is checked for ``#``: ``urlsplit`` reads an empty fragment as none.
    """
    address = urlsplit(value)
    if address.scheme not in _URL_SCHEMES or not address.netloc or "#" in value:
        raise ConfigError(f"{name} must be an absolute http or https URL without a fragment")
    return value


def _base_url(name: str, value: str) -> str:
    """An absolute http or https URL the adapter appends paths to: neither a query nor a fragment.

    The raw value is checked for ``?`` and ``#``: ``urlsplit`` reads an empty query or fragment as none,
    and a path appended after a bare ``?`` would land in the query.
    """
    if "?" in value:
        raise ConfigError(f"{name} must be an absolute http or https URL without a query or a fragment")
    return _absolute_url(name, value)
