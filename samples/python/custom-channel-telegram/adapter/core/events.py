# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Delivery of the events of Veriqa to the core: signed, on the route of the tenant of the transaction,
retried on a transport failure under the same ``webhook-id``."""

from __future__ import annotations

import asyncio
import json
import logging
from urllib.parse import quote

import httpx

from adapter.common import describe_tenant
from adapter.core.seam import InboundEvent
from adapter.core.signature import TransportSignature
from adapter.core.store import AdapterStore
from adapter.core.wire import ADAPTER_VERSION, WIRE_VERSION, Field

_log = logging.getLogger(__name__)

# Attempts of one delivery, the time one attempt may take and the pauses between them. An attempt waits
# for Veriqa to process the event, and processing auth_start includes the counter send-prompt Veriqa makes
# to this adapter within its own retry budget of 20 seconds — hence the timeout above that budget.
EVENT_ATTEMPTS = 3
EVENT_ATTEMPT_TIMEOUT_SECONDS = 30.0
EVENT_RETRY_PAUSES_SECONDS = (1.0, 2.0)

# How long after the first delivery the adapter may deliver the same event again: the lower bound of the
# retention of the idempotency keys it derives from its events.
EVENT_REDELIVERY_WINDOW_SECONDS = int(EVENT_ATTEMPTS * EVENT_ATTEMPT_TIMEOUT_SECONDS + sum(EVENT_RETRY_PAUSES_SECONDS))

# The status of Veriqa that is repeated besides 5xx, and the status that means a configuration error.
_TOO_MANY_REQUESTS = 429
_FORBIDDEN = 403

_JSON_CONTENT_TYPE = "application/json"


def webhook_path(channel_type: str, tenant: str | None) -> str:
    """The path of the webhook route of Veriqa for a channel and a tenant; ``None`` is the implicit tenant."""
    path = f"/api/channels/{quote(channel_type, safe='')}/webhook"
    return path if tenant is None else f"{path}/{quote(tenant, safe='')}"


class EventDelivery:
    """Posts the events the platform recognised to Veriqa.

    The route is the tenant of the transaction the event names, as the first deep-link or send-prompt
    call of that transaction recorded it; an event with no such transaction goes on the route of the tenant
    of the credential the platform traffic came in with. Nothing is locked while a delivery is in progress: Veriqa answers
    auth_start only after it has called this adapter back with send-prompt.
    """

    def __init__(
        self,
        veriqa_url: str,
        channel_type: str,
        signature: TransportSignature,
        store: AdapterStore,
        http: httpx.AsyncClient,
    ) -> None:
        self._veriqa_url = veriqa_url
        self._channel_type = channel_type
        self._signature = signature
        self._store = store
        self._http = http

    async def publish(self, event: InboundEvent) -> bool:
        tenant = self._route_tenant_of(event)
        url = self._veriqa_url + webhook_path(self._channel_type, tenant)
        envelope = {
            Field.VERSION: WIRE_VERSION,
            Field.EVENT: event.name,
            Field.ADAPTER_VERSION: ADAPTER_VERSION,
            **event.fields,
        }
        body = json.dumps(envelope, ensure_ascii=False, separators=(",", ":"))
        content = body.encode("utf-8")

        for attempt in range(1, EVENT_ATTEMPTS + 1):
            # Every attempt is signed anew: the timestamp moves, the webhook-id stays.
            headers = {**self._signature.sign(event.event_id, body), "content-type": _JSON_CONTENT_TYPE}
            try:
                response = await self._http.post(url, content=content, headers=headers, timeout=EVENT_ATTEMPT_TIMEOUT_SECONDS)
            except httpx.TransportError as exception:
                _log.warning("Delivery of a %s event, attempt %d: %s.", event.name, attempt, type(exception).__name__)
            else:
                if response.is_success:
                    _log.info("A %s event was delivered to the route of %s.", event.name, describe_tenant(tenant))
                    return True
                if response.status_code == _FORBIDDEN:
                    # The transport secret or the owner of the registration does not match: a configuration
                    # error that a repeat does not fix. The body of the answer is not logged.
                    _log.error("Veriqa refused a %s event with 403: check the transport secret and the channel type.", event.name)
                    return False
                if response.status_code != _TOO_MANY_REQUESTS and response.status_code < 500:
                    _log.error("Veriqa answered a %s event with %d; the event is not repeated.", event.name, response.status_code)
                    return False
                _log.warning("Delivery of a %s event, attempt %d: status %d.", event.name, attempt, response.status_code)

            if attempt < EVENT_ATTEMPTS:
                await asyncio.sleep(EVENT_RETRY_PAUSES_SECONDS[attempt - 1])

        _log.error("A %s event was not delivered after %d attempts.", event.name, EVENT_ATTEMPTS)
        return False

    def _route_tenant_of(self, event: InboundEvent) -> str | None:
        transaction_id = event.fields.get(Field.TRANSACTION_ID)
        route = self._store.route_of(transaction_id) if isinstance(transaction_id, str) else None
        return route.tenant if route is not None else event.fallback_tenant
