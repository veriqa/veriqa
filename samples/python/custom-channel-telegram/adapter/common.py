# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Helpers both sides of the adapter share: configuration errors, log fingerprints, bounded bodies."""

from __future__ import annotations

import hashlib
from collections.abc import Mapping

from starlette.requests import Request

# Largest body the adapter reads from a request: the same limit Veriqa puts on its webhook route.
MAX_BODY_BYTES = 1024 * 1024

# Length of a log fingerprint in bytes of SHA-256 — the form the built-in channels of Veriqa use.
_FINGERPRINT_BYTES = 8


class ConfigError(Exception):
    """A setting of the adapter is missing or invalid. The message names the variable, never its value."""


def required(env: Mapping[str, str], name: str) -> str:
    """The value of a required environment variable; a missing or blank one stops the start."""
    value = env.get(name, "").strip()
    if not value:
        raise ConfigError(f"{name} is not set")
    return value


def optional(env: Mapping[str, str], name: str) -> str | None:
    """The value of an optional environment variable; blank reads as not set."""
    value = env.get(name, "").strip()
    return value or None


def positive_int(env: Mapping[str, str], name: str, default: int) -> int:
    """An optional whole number above zero."""
    value = optional(env, name)
    if value is None:
        return default
    if not is_whole_number(value) or int(value) <= 0:
        raise ConfigError(f"{name} must be a whole number above zero")
    return int(value)


def is_whole_number(value: str) -> bool:
    """Whether a string is a whole number in ASCII digits."""
    return value.isascii() and value.isdigit()


def describe_tenant(tenant: str | None) -> str:
    """A tenant for the log; ``None`` is the implicit tenant of a self-hosted installation."""
    return "the implicit tenant" if tenant is None else f"tenant {tenant}"


def fingerprint(value: str | None) -> str:
    """A short unsalted fingerprint of an identifier for the log; an absent value gives an empty string."""
    if not value:
        return ""
    return hashlib.sha256(value.encode("utf-8")).digest()[:_FINGERPRINT_BYTES].hex().upper()


async def read_bounded_body(request: Request, limit: int = MAX_BODY_BYTES) -> bytes | None:
    """The raw body of a request, or ``None`` when it is longer than ``limit``.

    A declared length over the limit is refused before anything is read; a body that grows over the limit
    while it streams in is refused at that chunk, and the rest is never read.
    """
    declared = request.headers.get("content-length")
    if declared is not None and is_whole_number(declared) and int(declared) > limit:
        return None

    received = bytearray()
    async for chunk in request.stream():
        received.extend(chunk)
        if len(received) > limit:
            return None
    return bytes(received)
