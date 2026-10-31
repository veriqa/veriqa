# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""The seam between the side of the core and the side of the platform.

The side of the core calls the platform through ``Platform``; the platform hands the events it recognised
back through ``EventSink``. Everything that crosses the seam is in terms of the wire — chat and message
identifiers, texts, event names — never in terms of a platform API. An adapter for another channel
implements ``Platform`` and feeds an ``EventSink``; the side of the core stays as it is.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Protocol


class PlatformError(Exception):
    """The platform refused an operation or could not be reached.

    The message is safe for the log: it never carries a credential, a request address or a user's data.
    """


@dataclass(frozen=True)
class MessageRef:
    """Coordinates of a message on the platform."""

    chat_id: str
    message_id: str


@dataclass(frozen=True)
class PromptRequest:
    """A confirmation prompt to put to a user: the text Veriqa rendered and the captions of its two buttons."""

    transaction_id: str
    channel_user_id: str
    text: str
    confirm_label: str
    decline_label: str


@dataclass(frozen=True)
class PhoneNumberRequest:
    """A request for the phone number of a user, as ``send-message`` of that kind carries it.

    How the two actions are shown is the platform's own choice. ``secondary_action`` is a value of the
    wire (``skip``, ``cancel_sign_in`` or one a newer version adds); a platform whose button can carry a
    payload answers it with a ``phone_step_action`` event naming the transaction, one whose button cannot
    lets the press come back as an ``unaddressed`` message with the caption as its text.
    """

    transaction_id: str
    share_button_label: str
    secondary_action: str
    secondary_button_label: str


@dataclass(frozen=True)
class OutcomeRequest:
    """The outcome of a transaction to show to a user."""

    channel_user_id: str
    status_text: str
    inbound_token: str | None
    prompt_message: MessageRef | None
    phone_request_message: MessageRef | None
    replace_prompt: bool


@dataclass(frozen=True)
class HealthReport:
    """Whether the platform answers for every configured credential; ``details`` says what failed."""

    healthy: bool
    details: str | None


class Platform(Protocol):
    """Operations of the platform the calls of the core come down to.

    ``account`` is what ``account_for`` returned for the tenant of the call; the side of the core never looks
    into it. ``settings`` is the opaque ``adapter_settings`` string of the call, read by the platform alone.
    """

    def account_for(self, tenant: str | None) -> Any | None:
        """The credentials the calls of a tenant use, or ``None`` when the tenant has none."""
        ...

    async def send_prompt(self, account: Any, settings: str | None, request: PromptRequest) -> MessageRef:
        """Sends the prompt with its two buttons."""
        ...

    async def send_message(
        self,
        account: Any,
        settings: str | None,
        channel_user_id: str,
        text: str,
        kind: str,
        phone_number_request: PhoneNumberRequest | None,
    ) -> MessageRef:
        """Sends a message of a kind of content the wire names.

        ``phone_number_request`` is set exactly when the kind is the request for the phone number.
        """
        ...

    async def report_outcome(self, account: Any, settings: str | None, request: OutcomeRequest) -> None:
        """Shows the outcome of a transaction."""
        ...

    async def deep_link(self, account: Any, transaction_id: str) -> str:
        """The deep link of the transaction: one that opens it on the platform, or, on a channel whose sign-in
        starts by a code alone, one that only opens the chat."""
        ...

    async def health(self) -> HealthReport:
        """Probes every configured credential."""
        ...


@dataclass(frozen=True)
class InboundEvent:
    """An event of Veriqa the platform recognised in its traffic.

    ``event_id`` becomes the ``webhook-id`` of the delivery: unique per event and the same on every delivery
    of it. ``fields`` are the members of the event envelope besides the version, the name and the version of
    the adapter. ``fallback_tenant`` is the route of an event whose transaction has no known tenant — the
    tenant of the credential the platform traffic came in with.
    """

    event_id: str
    name: str
    fields: dict[str, Any] = field(repr=False)
    fallback_tenant: str | None


class EventSink(Protocol):
    """Where the platform hands the events of Veriqa to."""

    async def publish(self, event: InboundEvent) -> bool:
        """Delivers an event to Veriqa; ``True`` when Veriqa took it."""
        ...
