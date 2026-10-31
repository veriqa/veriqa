# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""The one input of the calls of Veriqa: ``POST`` on the base URL of the adapter.

A call goes through the same steps in the same order, and the platform is reached only after all of them:

1. the signature of the transport — anything else is ``403`` without an envelope and without an effect;
2. the envelope — the major version, the operation, the channel type; unknown fields are ignored;
3. the credentials of the tenant of the call;
4. the operation, once per idempotency key when it has an effect the user sees.
"""

from __future__ import annotations

import json
import logging
from collections.abc import Awaitable, Callable
from typing import Any

from starlette.requests import Request
from starlette.responses import JSONResponse, Response

from adapter.common import describe_tenant, read_bounded_body
from adapter.core.idempotency import IdempotentCalls
from adapter.core.seam import MessageRef, OutcomeRequest, PhoneNumberRequest, Platform, PlatformError, PromptRequest
from adapter.core.signature import TransportSignature
from adapter.core.store import AdapterStore, TransactionRoute
from adapter.core.wire import (
    ADAPTER_VERSION,
    REPLACE_PROMPT_INTENT,
    WEBHOOK_ID_HEADER,
    WIRE_MAJOR_VERSION,
    WIRE_VERSION,
    ErrorCode,
    Field,
    MessageKind,
    Operation,
    error_envelope,
    is_transaction_id,
    major_version_of,
    result_envelope,
)

_log = logging.getLogger(__name__)


class MalformedCall(Exception):
    """A signed body that is not a call of this wire: answered ``400`` without an envelope."""


class CallInput:
    """Handles the calls of Veriqa for the registration ``channel_type``."""

    def __init__(
        self,
        channel_type: str,
        signature: TransportSignature,
        platform: Platform,
        idempotent_calls: IdempotentCalls,
        store: AdapterStore,
    ) -> None:
        self._channel_type = channel_type
        self._signature = signature
        self._platform = platform
        self._idempotent_calls = idempotent_calls
        self._store = store

    async def handle(self, request: Request) -> Response:
        body = await read_bounded_body(request)
        if body is None:
            return Response(status_code=413)
        if not self._signature.verify(request.headers, body):
            return Response(status_code=403)

        try:
            envelope = _json_object(body)
            answer = await self._answer(envelope, request.headers.get(WEBHOOK_ID_HEADER, ""))
        except MalformedCall as reason:
            _log.warning("A signed call was refused: %s.", reason)
            return Response(status_code=400)
        return JSONResponse(answer)

    async def _answer(self, envelope: dict[str, Any], webhook_id: str) -> dict[str, Any]:
        major_version = major_version_of(envelope.get(Field.VERSION))
        if major_version is None:
            raise MalformedCall("no wire version")
        if major_version != WIRE_MAJOR_VERSION:
            return error_envelope(ErrorCode.UNSUPPORTED_WIRE_VERSION, f"The adapter speaks major version {WIRE_MAJOR_VERSION} of the wire.")

        operation = envelope.get(Field.OPERATION)
        if not isinstance(operation, str):
            # An event envelope reflected onto this input has no operation: it is not a call.
            raise MalformedCall("no operation")
        if operation not in Operation.ALL:
            return error_envelope(ErrorCode.UNSUPPORTED_OPERATION, "The adapter does not know the operation.")
        if envelope.get(Field.CHANNEL_TYPE) != self._channel_type:
            return error_envelope(ErrorCode.CHANNEL_NOT_AVAILABLE, "The adapter does not serve this channel type.")

        payload = envelope.get(Field.PAYLOAD)
        if not isinstance(payload, dict):
            raise MalformedCall("no payload")
        if operation == Operation.HEALTH:
            # Health probes every bot of the adapter; it belongs to no tenant.
            return await self._health()

        tenant = _optional_string(envelope, Field.TENANT)
        settings = _optional_string(envelope, Field.ADAPTER_SETTINGS)
        account = self._platform.account_for(tenant)
        if account is None:
            code = ErrorCode.DEEP_LINK_GENERATION_FAILED if operation == Operation.DEEP_LINK else ErrorCode.CHANNEL_NOT_AVAILABLE
            _log.warning("A %s call of %s was refused: no credentials.", operation, describe_tenant(tenant))
            return error_envelope(code, "The tenant of the call has no credentials on the platform.")

        if operation == Operation.DEEP_LINK:
            return await self._deep_link(account, tenant, _transaction_id(payload))

        # The signature check has required the webhook-id: it is the idempotency key of the call.
        effect = self._effect(operation, account, settings, payload)
        if operation == Operation.SEND_PROMPT:
            # Kept before the prompt goes out: a press of its button may come in before the call returns.
            self._keep_route(_transaction_id(payload), tenant)
        return await self._idempotent_calls.run(operation, webhook_id, effect)

    def _effect(
        self, operation: str, account: Any, settings: str | None, payload: dict[str, Any]
    ) -> Callable[[], Awaitable[dict[str, Any]]]:
        """The call with an effect, its payload read before anything reaches the platform."""
        if operation == Operation.SEND_PROMPT:
            rendered = payload.get(Field.RENDERED)
            if not isinstance(payload.get(Field.PROMPT), dict) or not isinstance(rendered, dict):
                raise MalformedCall("no prompt")
            prompt = PromptRequest(
                transaction_id=_transaction_id(payload),
                channel_user_id=_string(payload, Field.CHANNEL_USER_ID),
                text=_string(rendered, Field.TEXT),
                confirm_label=_string(rendered, Field.CONFIRM_LABEL),
                decline_label=_string(rendered, Field.DECLINE_LABEL),
            )
            return lambda: self._message_call(operation, self._platform.send_prompt(account, settings, prompt))

        if operation == Operation.SEND_MESSAGE:
            channel_user_id = _string(payload, Field.CHANNEL_USER_ID)
            text = _string(payload, Field.TEXT)
            kind = _string(payload, Field.KIND)
            # The content of the request travels with its kind alone; with any other kind the member is
            # null and is not read.
            phone_number_request = _phone_number_request(payload) if kind == MessageKind.PHONE_NUMBER_REQUEST else None
            return lambda: self._message_call(
                operation,
                self._platform.send_message(account, settings, channel_user_id, text, kind, phone_number_request),
            )

        # The transaction and the outcome are required members, checked but not used: an unknown outcome is
        # shown by its status text like any other, and an unknown intent is a new message.
        _transaction_id(payload)
        _string(payload, Field.OUTCOME)
        outcome = OutcomeRequest(
            channel_user_id=_string(payload, Field.CHANNEL_USER_ID),
            status_text=_string(payload, Field.STATUS_TEXT),
            inbound_token=_optional_string(payload, Field.INBOUND_TOKEN),
            prompt_message=_message_ref(payload.get(Field.PROMPT_MESSAGE)),
            phone_request_message=_message_ref(payload.get(Field.PHONE_REQUEST_MESSAGE)),
            replace_prompt=_string(payload, Field.DISPLAY_INTENT) == REPLACE_PROMPT_INTENT,
        )
        return lambda: self._outcome_call(account, settings, outcome)

    async def _message_call(self, operation: str, sending: Awaitable[MessageRef]) -> dict[str, Any]:
        try:
            message = await sending
        except PlatformError as error:
            _log.warning("A %s call failed on the platform: %s.", operation, error)
            return error_envelope(ErrorCode.MESSAGE_SEND_FAILED, "The platform did not take the message.")
        return result_envelope({Field.MESSAGE: {Field.CHAT_ID: message.chat_id, Field.MESSAGE_ID: message.message_id}})

    async def _outcome_call(self, account: Any, settings: str | None, outcome: OutcomeRequest) -> dict[str, Any]:
        try:
            await self._platform.report_outcome(account, settings, outcome)
        except PlatformError as error:
            _log.warning("A %s call failed on the platform: %s.", Operation.REPORT_OUTCOME, error)
            return error_envelope(ErrorCode.MESSAGE_SEND_FAILED, "The platform did not take the outcome.")
        return result_envelope({Field.SHOWN: True})

    async def _deep_link(self, account: Any, tenant: str | None, transaction_id: str) -> dict[str, Any]:
        # One link per transaction: a repeat — whichever tenant it comes with — gets the link made first.
        url = self._store.deep_link_of(transaction_id)
        if url is None:
            try:
                made = await self._platform.deep_link(account, transaction_id)
            except PlatformError as error:
                _log.warning("A deep-link call failed on the platform: %s.", error)
                return error_envelope(ErrorCode.DEEP_LINK_GENERATION_FAILED, "The platform did not give a deep link.")
            # A concurrent call on the same transaction may have kept its link meanwhile: the kept one wins.
            self._store.keep_deep_link(transaction_id, made)
            url = self._store.deep_link_of(transaction_id) or made
        self._keep_route(transaction_id, tenant)
        return result_envelope({Field.URL: url})

    def _keep_route(self, transaction_id: str, tenant: str | None) -> None:
        """Records the tenant of a transaction as the route of its events; the tenant recorded first stands."""
        self._store.keep_route(transaction_id, TransactionRoute(tenant=tenant))
        route = self._store.route_of(transaction_id)
        if route is not None and route.tenant != tenant:
            _log.info("A call of %s named a transaction recorded for %s; its events stay on that route.", describe_tenant(tenant), describe_tenant(route.tenant))

    async def _health(self) -> dict[str, Any]:
        report = await self._platform.health()
        return result_envelope(
            {
                Field.HEALTHY: report.healthy,
                Field.DETAILS: report.details,
                Field.ADAPTER_VERSION: ADAPTER_VERSION,
                Field.WIRE_VERSION: WIRE_VERSION,
            }
        )


def _json_object(body: bytes) -> dict[str, Any]:
    try:
        parsed = json.loads(body)
    except ValueError:
        raise MalformedCall("the body is not JSON") from None
    if not isinstance(parsed, dict):
        raise MalformedCall("the body is not a JSON object")
    return parsed


def _string(container: dict[str, Any], name: str) -> str:
    value = container.get(name)
    if not isinstance(value, str):
        raise MalformedCall(f"no {name}")
    return value


def _optional_string(container: dict[str, Any], name: str) -> str | None:
    value = container.get(name)
    if value is not None and not isinstance(value, str):
        raise MalformedCall(f"{name} is not a string")
    return value


def _transaction_id(payload: dict[str, Any]) -> str:
    value = payload.get(Field.TRANSACTION_ID)
    if not is_transaction_id(value):
        raise MalformedCall(f"no valid {Field.TRANSACTION_ID}")
    return value


def _phone_number_request(payload: dict[str, Any]) -> PhoneNumberRequest:
    value = payload.get(Field.PHONE_NUMBER_REQUEST)
    if not isinstance(value, dict):
        raise MalformedCall(f"no {Field.PHONE_NUMBER_REQUEST}")
    return PhoneNumberRequest(
        transaction_id=_transaction_id(value),
        share_button_label=_string(value, Field.SHARE_BUTTON_LABEL),
        secondary_action=_string(value, Field.SECONDARY_ACTION),
        secondary_button_label=_string(value, Field.SECONDARY_BUTTON_LABEL),
    )


def _message_ref(value: Any) -> MessageRef | None:
    if value is None:
        return None
    if not isinstance(value, dict):
        raise MalformedCall(f"{Field.PROMPT_MESSAGE} is not an object")
    return MessageRef(chat_id=_string(value, Field.CHAT_ID), message_id=_string(value, Field.MESSAGE_ID))
