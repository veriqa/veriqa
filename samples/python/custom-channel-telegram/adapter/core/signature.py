# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""The transport signature, both directions: Standard Webhooks, symmetric scheme ``v1`` (HMAC-SHA256)."""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from datetime import datetime, timezone
from math import floor

from standardwebhooks import Webhook

from adapter.core.wire import WEBHOOK_ID_HEADER, WEBHOOK_SIGNATURE_HEADER, WEBHOOK_TIMESTAMP_HEADER

# Separator of the signatures in one header: one per secret while a secret is rotated.
_SIGNATURE_SEPARATOR = " "


class TransportSignature:
    """Checks the signature of a call of Veriqa and signs the events the adapter posts to Veriqa.

    The secrets are the current transport secret and, during a rotation, the previous one. A call is
    authentic when any signature of its header matches any of them; an event is signed with all of them, so
    Veriqa accepts it whichever secret it holds at the moment. The window of the timestamp — five minutes
    either way — and the constant-time comparison are the ones of the Standard Webhooks library.
    """

    def __init__(self, secrets: Sequence[str]) -> None:
        self._webhooks = [Webhook(secret) for secret in secrets]

    def verify(self, headers: Mapping[str, str], body: bytes) -> bool:
        """Whether a received message is signed with a known secret inside the window of its timestamp."""
        for webhook in self._webhooks:
            try:
                webhook.verify(body, dict(headers), json_parse=False)
                return True
            except Exception:  # any refusal of the library, a malformed header among them, means "not authentic"
                continue
        return False

    def sign(self, webhook_id: str, body: str) -> dict[str, str]:
        """The signature headers of a message: ``body`` is the exact text whose UTF-8 bytes are sent."""
        signed_at = floor(datetime.now(timezone.utc).timestamp())
        moment = datetime.fromtimestamp(signed_at, tz=timezone.utc)
        signatures = [webhook.sign(webhook_id, moment, body) for webhook in self._webhooks]
        return {
            WEBHOOK_ID_HEADER: webhook_id,
            WEBHOOK_TIMESTAMP_HEADER: str(signed_at),
            WEBHOOK_SIGNATURE_HEADER: _SIGNATURE_SEPARATOR.join(signatures),
        }
