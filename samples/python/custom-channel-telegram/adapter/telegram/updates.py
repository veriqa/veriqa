# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Which updates of Telegram are events of Veriqa.

The rules are the ones of the built-in Telegram channel of Veriqa; this adapter applies them itself,
before anything goes to Veriqa, so an update that is not an event of Veriqa never leaves the adapter —
except to the bot of the integrator, when the adapter forwards to one.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from typing import Any

from adapter.core.wire import Event, is_transaction_id, to_e164

# Markers Veriqa puts into its deep links and buttons — the same as the built-in Telegram channel uses.
DEEP_LINK_MARKER = "auth_"
CONFIRM_PREFIX = "vq_confirm_"
DECLINE_PREFIX = "vq_decline_"

# The types of updates ``classify`` reads: the messages and the presses of the buttons of a prompt.
VERIQA_UPDATE_TYPES = ("message", "callback_query")

_START_COMMAND = "/start"
_PRIVATE_CHAT = "private"

# The character users put between the groups of digits of a code, besides whitespace.
_CODE_GROUP_SEPARATOR = "-"


class Kind(Enum):
    """How an update is handled."""

    # An answer to Veriqa itself — a deep link, a code of a sign-in or a button of a prompt: always an event
    # of Veriqa.
    VERIQA = "veriqa"
    # A message of a user to the bot that names no transaction: an event of Veriqa, unless the adapter
    # forwards to the bot of the integrator, which then owns the conversation.
    CONVERSATION = "conversation"
    # Anything else: never an event of Veriqa.
    FOREIGN = "foreign"


@dataclass(frozen=True)
class Classification:
    kind: Kind
    event: str | None = None
    user: dict[str, Any] | None = None
    transaction_id: str | None = None
    inbound_token: str | None = None
    phone_number: str | None = None
    # The text of a message that names no transaction: the press of the secondary button of a request for
    # the phone number arrives as the caption of that button.
    text: str | None = None
    # The code of a sign-in exactly as the user sent it: Veriqa drops the separators and checks the length.
    code: str | None = None

    @property
    def wants_avatar(self) -> bool:
        # The built-in channel shows an avatar on the start of a sign-in and on its confirmation alone; a
        # start by a code is a start of a sign-in as well.
        return self.event in (Event.AUTH_START, Event.AUTH_START_BY_CODE, Event.AUTH_CONFIRM)


FOREIGN = Classification(Kind.FOREIGN)


def classify(update: dict[str, Any], accepts_codes: bool) -> Classification:
    """The classification of one update; ``accepts_codes`` — a message of digits is a start by a code."""
    callback_query = update.get("callback_query")
    if isinstance(callback_query, dict):
        return _classify_callback_query(callback_query)
    message = update.get("message")
    if isinstance(message, dict):
        return _classify_message(message, accepts_codes)
    return FOREIGN


def _classify_callback_query(callback_query: dict[str, Any]) -> Classification:
    user = callback_query.get("from")
    data = callback_query.get("data")
    message = callback_query.get("message")
    if not _is_person(user) or not isinstance(data, str):
        return FOREIGN
    if isinstance(message, dict) and not _is_private(message):
        return FOREIGN

    for prefix, event in ((CONFIRM_PREFIX, Event.AUTH_CONFIRM), (DECLINE_PREFIX, Event.AUTH_DECLINE)):
        if data.startswith(prefix):
            transaction_id = data[len(prefix):]
            if not is_transaction_id(transaction_id):
                return FOREIGN
            # The token names the callback query, which the outcome answers. A press on a message the
            # update does not carry has no token.
            token = callback_query.get("id") if isinstance(message, dict) else None
            return Classification(Kind.VERIQA, event, user, transaction_id, inbound_token=token if isinstance(token, str) else None)
    return FOREIGN


def _classify_message(message: dict[str, Any], accepts_codes: bool) -> Classification:
    user = message.get("from")
    if not _is_person(user) or not _is_private(message):
        return FOREIGN

    contact = message.get("contact")
    if isinstance(contact, dict):
        # The contact must be the sender's own, not a card of somebody else. Telegram states no format
        # of the number and often sends it without the plus; the wire takes E.164 alone.
        phone_number = to_e164(contact.get("phone_number"))
        if contact.get("user_id") != user["id"] or phone_number is None:
            return FOREIGN
        return Classification(Kind.CONVERSATION, Event.PHONE_SHARED, user, phone_number=phone_number)

    parameter = _start_parameter(message.get("text"))
    if parameter is not None and parameter.startswith(DEEP_LINK_MARKER):
        transaction_id = parameter[len(DEEP_LINK_MARKER):]
        if not is_transaction_id(transaction_id):
            return FOREIGN
        return Classification(Kind.VERIQA, Event.AUTH_START, user, transaction_id)

    text = message.get("text")
    if accepts_codes and _is_code(text):
        # The code names a sign-in the way a deep link does, so it goes to Veriqa even when the adapter
        # forwards to the bot of the integrator. It goes as it was sent and is never logged.
        return Classification(Kind.VERIQA, Event.AUTH_START_BY_CODE, user, code=text)

    # Any other direct message, a bare /start among them, names no transaction. Its text goes with the
    # event: Veriqa compares it with the caption of the secondary button of a request it is waiting on.
    return Classification(Kind.CONVERSATION, Event.UNADDRESSED, user, text=text if isinstance(text, str) else None)


def _start_parameter(text: Any) -> str | None:
    """The parameter of a ``/start <parameter>`` command, or ``None`` for any other text."""
    if not isinstance(text, str):
        return None
    command, _, parameter = text.partition(" ")
    return parameter.strip() if command.lower() == _START_COMMAND and parameter.strip() else None


def _is_code(text: Any) -> bool:
    """Whether a text is a code of a sign-in: decimal digits, once whitespace and the separator are dropped.

    How many digits a code has is the setting of Veriqa, which checks the length itself.
    """
    if not isinstance(text, str):
        return False
    digits = "".join(character for character in text if not character.isspace() and character != _CODE_GROUP_SEPARATOR)
    return digits.isascii() and digits.isdigit()


def _is_person(user: Any) -> bool:
    return isinstance(user, dict) and isinstance(user.get("id"), int) and not user.get("is_bot", False)


def _is_private(message: dict[str, Any]) -> bool:
    chat = message.get("chat")
    return isinstance(chat, dict) and chat.get("type") == _PRIVATE_CHAT
