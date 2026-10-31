# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Names and envelopes of the HTTP channel wire of Veriqa, version 1.

The names are the ones of the wire contract verbatim; this is the one place the adapter spells them.
"""

from __future__ import annotations

import re
from typing import Any

# Version of the wire the adapter speaks, written into every envelope it sends: major.minor.
WIRE_VERSION = "1.0"
WIRE_MAJOR_VERSION = 1

# Version of this adapter (semver), reported by health and written into every event.
ADAPTER_VERSION = "1.0.0"

# Headers of the Standard Webhooks signature of the transport.
WEBHOOK_ID_HEADER = "webhook-id"
WEBHOOK_TIMESTAMP_HEADER = "webhook-timestamp"
WEBHOOK_SIGNATURE_HEADER = "webhook-signature"

# A transaction identifier: 43 Base62 characters.
TRANSACTION_ID_PATTERN = re.compile(r"\A[0-9A-Za-z]{43}\Z")

# A phone number of the wire: E.164 — the leading plus and 1 to 15 digits, the first not zero.
_E164_PATTERN = re.compile(r"\A\+[1-9][0-9]{0,14}\Z")

# Characters a platform writes a phone number with for readability; they carry no value.
_PHONE_SEPARATORS = str.maketrans("", "", " -().")

# A wire version: major.minor in ASCII digits.
_VERSION_PATTERN = re.compile(r"\A([0-9]+)\.[0-9]+\Z")


class Operation:
    """Values of ``operation`` of a call of the core."""

    SEND_PROMPT = "send-prompt"
    SEND_MESSAGE = "send-message"
    REPORT_OUTCOME = "report-outcome"
    DEEP_LINK = "deep-link"
    HEALTH = "health"

    ALL = frozenset({SEND_PROMPT, SEND_MESSAGE, REPORT_OUTCOME, DEEP_LINK, HEALTH})

    # The calls with an effect the user sees: their webhook-id is the idempotency key. The webhook-id of
    # deep-link and health is random per call and binds nothing.
    KEYED = frozenset({SEND_PROMPT, SEND_MESSAGE, REPORT_OUTCOME})


class Event:
    """Values of ``event`` of an event the adapter posts to the core."""

    AUTH_START = "auth_start"
    AUTH_START_BY_CODE = "auth_start_by_code"
    AUTH_CONFIRM = "auth_confirm"
    AUTH_DECLINE = "auth_decline"
    PHONE_SHARED = "phone_shared"
    UNADDRESSED = "unaddressed"


class ErrorCode:
    """Codes of ``error`` in an answer of the adapter."""

    MESSAGE_SEND_FAILED = "message_send_failed"
    CHANNEL_NOT_AVAILABLE = "channel_not_available"
    DEEP_LINK_GENERATION_FAILED = "deep_link_generation_failed"
    UNSUPPORTED_WIRE_VERSION = "unsupported_wire_version"
    UNSUPPORTED_OPERATION = "unsupported_operation"


class Field:
    """Names of the members of the envelopes the adapter reads and writes."""

    VERSION = "version"
    OPERATION = "operation"
    CHANNEL_TYPE = "channel_type"
    TENANT = "tenant"
    ADAPTER_SETTINGS = "adapter_settings"
    PAYLOAD = "payload"
    RESULT = "result"
    ERROR = "error"
    CODE = "code"
    MESSAGE = "message"
    EVENT = "event"
    ADAPTER_VERSION = "adapter_version"

    TRANSACTION_ID = "transaction_id"
    CHANNEL_USER_ID = "channel_user_id"
    PROMPT = "prompt"
    RENDERED = "rendered"
    TEXT = "text"
    CONFIRM_LABEL = "confirm_label"
    DECLINE_LABEL = "decline_label"
    KIND = "kind"
    OUTCOME = "outcome"
    STATUS_TEXT = "status_text"
    INBOUND_TOKEN = "inbound_token"
    PROMPT_MESSAGE = "prompt_message"
    PHONE_REQUEST_MESSAGE = "phone_request_message"
    DISPLAY_INTENT = "display_intent"
    CHAT_ID = "chat_id"
    MESSAGE_ID = "message_id"
    SHOWN = "shown"
    URL = "url"
    HEALTHY = "healthy"
    DETAILS = "details"
    WIRE_VERSION = "wire_version"
    IDENTITY = "identity"
    PHONE_NUMBER = "phone_number"
    PHONE_NUMBER_REQUEST = "phone_number_request"
    SHARE_BUTTON_LABEL = "share_button_label"
    SECONDARY_ACTION = "secondary_action"
    SECONDARY_BUTTON_LABEL = "secondary_button_label"


class MessageKind:
    """Values of ``kind`` of ``send-message`` this adapter knows; the registration declares the ones it takes."""

    PLAIN_TEXT = "plain_text"
    # Travels with the member ``phone_number_request``: the captions of the two buttons of the request.
    PHONE_NUMBER_REQUEST = "phone_number_request"


# The display intent that replaces the prompt with the outcome; any other value — known or not — is a new message.
REPLACE_PROMPT_INTENT = "replace_prompt"


def result_envelope(result: dict[str, Any]) -> dict[str, Any]:
    """An answer of the adapter with the data of the operation."""
    return {Field.VERSION: WIRE_VERSION, Field.RESULT: result}


def error_envelope(code: str, message: str) -> dict[str, Any]:
    """An answer of the adapter with a refusal of the operation."""
    return {Field.VERSION: WIRE_VERSION, Field.ERROR: {Field.CODE: code, Field.MESSAGE: message}}


def major_version_of(value: Any) -> int | None:
    """The major part of a wire version ``major.minor``; ``None`` for anything else."""
    match = _VERSION_PATTERN.match(value) if isinstance(value, str) else None
    return int(match.group(1)) if match else None


def is_transaction_id(value: Any) -> bool:
    """Whether a value is a transaction identifier of Veriqa."""
    return isinstance(value, str) and TRANSACTION_ID_PATTERN.match(value) is not None


def to_e164(value: Any) -> str | None:
    """A phone number in E.164, the format ``phone_number`` of ``phone_shared`` has on the wire.

    The format is the adapter's to bring the number to: Veriqa does not repair a number in another
    spelling, it does not process the event that carries one. ``None`` — the value has no E.164 form.
    """
    if not isinstance(value, str):
        return None
    spelled = value.strip().translate(_PHONE_SEPARATORS)
    number = spelled if spelled.startswith("+") else "+" + spelled
    return number if _E164_PATTERN.match(number) else None
