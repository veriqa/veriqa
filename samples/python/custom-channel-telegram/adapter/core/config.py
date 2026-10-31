# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Settings of the side of the core, read from the environment once at the start."""

from __future__ import annotations

import base64
import binascii
import re
from collections.abc import Mapping
from dataclasses import dataclass, field
from urllib.parse import urlsplit

from adapter.common import ConfigError, optional, positive_int, required
from adapter.core.events import EVENT_REDELIVERY_WINDOW_SECONDS


class CoreEnv:
    """Names of the environment variables of the side of the core."""

    VERIQA_URL = "VERIQA_URL"
    CHANNEL_TYPE = "VERIQA_CHANNEL_TYPE"
    TRANSPORT_SECRET = "VERIQA_TRANSPORT_SECRET"
    PREVIOUS_TRANSPORT_SECRET = "VERIQA_PREVIOUS_TRANSPORT_SECRET"
    PORT = "ADAPTER_PORT"
    DATA_DIR = "ADAPTER_DATA_DIR"
    IDEMPOTENCY_TTL_SECONDS = "ADAPTER_IDEMPOTENCY_TTL_SECONDS"
    LOG_LEVEL = "ADAPTER_LOG_LEVEL"


DEFAULT_PORT = 8000

# The largest TCP port number.
MAX_PORT = 65535
DEFAULT_DATA_DIR = "/data"
DEFAULT_IDEMPOTENCY_TTL_SECONDS = 24 * 60 * 60
DEFAULT_LOG_LEVEL = "INFO"

# The budget within which the HTTP channel of Veriqa repeats one call: a key kept for less could let a
# repeat of the same call through as a new one.
WRAPPER_RETRY_BUDGET_SECONDS = 20

# The longest transaction Veriqa accepts (the upper bound of its TransactionTtlSeconds): the route of a
# transaction is kept for the same retention and has to outlive the transaction, or a late event of it
# would go on the route of the tenant of the bot instead.
VERIQA_MAX_TRANSACTION_TTL_SECONDS = 30 * 60

# The retention may not be shorter than any window in which the same key can come again, nor than a
# transaction whose events need its route.
MIN_IDEMPOTENCY_TTL_SECONDS = max(WRAPPER_RETRY_BUDGET_SECONDS, EVENT_REDELIVERY_WINDOW_SECONDS, VERIQA_MAX_TRANSACTION_TTL_SECONDS)

# A channel type of Veriqa.
_CHANNEL_TYPE_PATTERN = re.compile(r"\A[a-z][a-z0-9-]{0,63}\Z")

# A transport secret in the Standard Webhooks serialization: the prefix and a base64 key of 24 to 64 bytes.
_SECRET_PREFIX = "whsec_"
_MIN_SECRET_BYTES = 24
_MAX_SECRET_BYTES = 64

_URL_SCHEMES = ("http", "https")
_LOG_LEVELS = ("DEBUG", "INFO", "WARNING", "ERROR")


@dataclass(frozen=True)
class CoreConfig:
    """Where Veriqa is, which registration the adapter serves, and how it keeps its state."""

    veriqa_url: str
    channel_type: str
    transport_secrets: tuple[str, ...] = field(repr=False)
    port: int
    data_dir: str
    idempotency_ttl_seconds: int
    log_level: str

    @classmethod
    def from_env(cls, env: Mapping[str, str]) -> CoreConfig:
        veriqa_url = required(env, CoreEnv.VERIQA_URL).rstrip("/")
        address = urlsplit(veriqa_url)
        # The raw value is checked for "?" and "#": urlsplit reads an empty query or fragment as none, and the
        # paths of the core appended after a bare "?" would land in the query.
        if address.scheme not in _URL_SCHEMES or not address.netloc or "?" in veriqa_url or "#" in veriqa_url:
            raise ConfigError(f"{CoreEnv.VERIQA_URL} must be an absolute http or https URL without a query or a fragment")

        channel_type = required(env, CoreEnv.CHANNEL_TYPE)
        if not _CHANNEL_TYPE_PATTERN.match(channel_type):
            raise ConfigError(f"{CoreEnv.CHANNEL_TYPE} must be a lowercase channel type such as telegram-http")

        secrets = [_transport_secret(env, CoreEnv.TRANSPORT_SECRET, is_required=True)]
        previous = _transport_secret(env, CoreEnv.PREVIOUS_TRANSPORT_SECRET, is_required=False)
        if previous is not None:
            secrets.append(previous)

        ttl = positive_int(env, CoreEnv.IDEMPOTENCY_TTL_SECONDS, DEFAULT_IDEMPOTENCY_TTL_SECONDS)
        if ttl < MIN_IDEMPOTENCY_TTL_SECONDS:
            raise ConfigError(f"{CoreEnv.IDEMPOTENCY_TTL_SECONDS} must be at least {MIN_IDEMPOTENCY_TTL_SECONDS} seconds")

        port = positive_int(env, CoreEnv.PORT, DEFAULT_PORT)
        if port > MAX_PORT:
            raise ConfigError(f"{CoreEnv.PORT} must be a port number from 1 to {MAX_PORT}")

        log_level = (optional(env, CoreEnv.LOG_LEVEL) or DEFAULT_LOG_LEVEL).upper()
        if log_level not in _LOG_LEVELS:
            raise ConfigError(f"{CoreEnv.LOG_LEVEL} must be one of {', '.join(_LOG_LEVELS)}")

        return cls(
            veriqa_url=veriqa_url,
            channel_type=channel_type,
            transport_secrets=tuple(secrets),
            port=port,
            data_dir=optional(env, CoreEnv.DATA_DIR) or DEFAULT_DATA_DIR,
            idempotency_ttl_seconds=ttl,
            log_level=log_level,
        )


def _transport_secret(env: Mapping[str, str], name: str, is_required: bool) -> str | None:
    """A transport secret in the Standard Webhooks serialization; the value never reaches a message."""
    value = required(env, name) if is_required else optional(env, name)
    if value is None:
        return None
    if not value.startswith(_SECRET_PREFIX):
        raise ConfigError(f"{name} must start with {_SECRET_PREFIX}")
    try:
        key = base64.b64decode(value[len(_SECRET_PREFIX):], validate=True)
    except (binascii.Error, ValueError):
        raise ConfigError(f"{name} must carry a base64 key after {_SECRET_PREFIX}") from None
    if not _MIN_SECRET_BYTES <= len(key) <= _MAX_SECRET_BYTES:
        raise ConfigError(f"{name} must carry a key of {_MIN_SECRET_BYTES} to {_MAX_SECRET_BYTES} bytes")
    return value
