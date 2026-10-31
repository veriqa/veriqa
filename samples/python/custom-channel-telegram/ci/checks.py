# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Checks of the stand that the conformance suite does not see, run inside the network of the stand.

Subcommands, all driven by ``run.sh``:

- ``own`` — the checks of the adapter itself: the protection of the webhook, the separation of the updates
  of Veriqa from the rest, the events without forwarding, the own setting of the adapter, the rules of the
  side of Veriqa (idempotency keys, credentials per tenant, the route of an event, the envelope), the
  shape of what the adapter sends to Telegram and to Veriqa, and the phone number on request: the reply
  keyboard of the request, the refusal of a request without its content and the text of a message in its
  event; and the start by a code: what a message of digits becomes in the modes ``only``, ``fallback`` and
  ``off`` of ``CODE_START``, and the deep link of each mode. Each prints ``PASS <check>`` or
  ``FAIL <check>: <reason>``; what counts is what the recorder of the stub saw, not only the status the
  adapter answered;
- ``survive-before <key> <transaction>`` and ``survive-after <key> <transaction> <first>`` — the two phases
  of the step ``state.survives-recreate`` of ``run.sh``, around a recreation of the container of the
  standalone adapter. The first sends a message under the idempotency key and asks a deep link of the
  transaction for the tenant ``shop-b``, and prints what the second needs; the second repeats the message
  and brings the transaction in on the bot of the default record;
- ``wait-webhooks`` — waits until every bot of the stand has registered its webhook with the stub;
- ``wait-adapter <url> <seconds>`` — waits until the adapter at ``url`` refuses an unsigned call;
- ``suite-report [code-start]`` — reads the JSON report of the conformance suite from the standard input
  and checks the outcome of every scenario; ``code-start`` — the report of the run against the adapter in
  the mode ``CODE_START=only``;
- ``wait-channel`` — waits until ``GET /health/channels`` of Veriqa reports the channel healthy.

Standard library only; exit code 0 when everything held, 1 otherwise.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import secrets
import string
import sys
import time
import urllib.error
import urllib.request
import uuid
from collections.abc import Callable
from typing import Any
from urllib.parse import parse_qs, urlsplit

STUB_URL = os.environ.get("STAND_STUB_URL", "http://stub:9000")
CHANNEL_TYPE = "telegram-http"
WIRE_VERSION = "1.0"
WEBHOOK_SECRET_HEADER = "X-Telegram-Bot-Api-Secret-Token"
REQUEST_TIMEOUT_SECONDS = 30.0
POLL_SECONDS = 2.0

# The scenario the stand declares not applicable: Telegram has no case in which it refuses a prompt.
NOT_APPLICABLE_SCENARIO = "platform.cannot-continue"
# Scenarios the gate names on their own: each must be in the report, the first not applicable, the rest passed.
# The request for the phone number joins a run only while the registration declares the number obtained on
# request, so a stand that lost the declaration would drop it without a failure.
REQUIRED_SCENARIOS = [
    NOT_APPLICABLE_SCENARIO,
    "platform.credentials-by-tenant",
    "send-message.phone-number-request",
    "events.all-valid",
]

# The argument of suite-report for the run against the adapter in the mode CODE_START=only, whose
# registration declares the start by a code with a deep link that only opens the chat.
CODE_START_RUN = "code-start"
# The start by a code: in the report of that run, and in no other.
CODE_START_SCENARIO = "flow.code-start"
# The scenarios that start by a deep link of a transaction: not in the report of that run.
LINK_SCENARIOS = ["flow.confirm", "flow.decline", "identity.phone-automatic"]

# The event of a start by a code and the marker of Veriqa in the start parameter of a deep link.
AUTH_START_BY_CODE = "auth_start_by_code"
DEEP_LINK_MARKER = "auth_"

# The status codes a webhook without its secret, or with a wrong one, is refused with.
WEBHOOK_REFUSALS = (401, 403)

# Error codes of the wire the checks expect.
CHANNEL_NOT_AVAILABLE = "channel_not_available"
DEEP_LINK_GENERATION_FAILED = "deep_link_generation_failed"

# The user name the stub gives the bot of an identifier, and the deep link host of Telegram.
BOT_USERNAME = "stand_{bot_id}_bot"
DEEP_LINK_HOST = "t.me"
MAX_START_PARAMETER_LENGTH = 64

# Tenants of the stand: two with records of their own on the adapter of the tenants, one with none anywhere.
SHOP_A = "shop-a"
SHOP_B = "shop-b"
TENANT_WITHOUT_RECORD = "shop-z"

# The labels and the texts the calls of the checks carry.
CONFIRM_LABEL = "Confirm"
DECLINE_LABEL = "Decline"
STATUS_TEXT = "You are signed in to the stand."

# The request for the phone number the checks send, and the plain message a press of its second button is.
PHONE_NUMBER_REQUEST_KIND = "phone_number_request"
SHARE_BUTTON_LABEL = "Share my number"
SECONDARY_BUTTON_LABEL = "Not now"

# A body of a call over the limit of 1 MB the adapter must refuse.
OVERSIZED_TEXT_LENGTH = 1_100_000

# What a link to a file of the Bot API looks like: it carries the token of the bot.
BOT_API_FILE_LINK_MARKERS = ("api.telegram.org/file/", "/file/bot")

# The identity of an event, as the built-in channel builds it; the avatar only when the user has a photo.
IDENTITY_KEYS = {"channel_user_id", "display_name", "first_name", "last_name", "username", "is_bot", "locale", "raw_metadata"}
AVATAR_KEY = "avatar_url"
RAW_METADATA_KEYS = {"is_premium", "telegram_id"}
AVATAR_PREFIX = "data:image/"

# The distance from the user of the stand to the other user the checks play, the one with a profile photo.
PHOTO_USER_OFFSET = 7

_BASE62 = string.digits + string.ascii_letters


class CheckFailed(Exception):
    """A check did not hold; the message says what was seen."""


# ── HTTP ─────────────────────────────────────────────────────────────────────


def http(method: str, url: str, body: bytes | None = None, headers: dict[str, str] | None = None) -> tuple[int, bytes]:
    request = urllib.request.Request(url, data=body, headers=headers or {}, method=method)
    try:
        with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


def stub(method: str, path: str, document: Any = None) -> Any:
    body = None if document is None else json.dumps(document).encode("utf-8")
    headers = {} if document is None else {"content-type": "application/json"}
    status, answer = http(method, STUB_URL + path, body, headers)
    if not 200 <= status < 300:
        raise CheckFailed(f"the stub answered {method} {path} with {status}")
    return json.loads(answer) if answer else None


class SetupError(Exception):
    """The checks cannot start: a setting is missing or the stand is not as run.sh built it."""


def env(name: str) -> str:
    value = os.environ.get(name, "")
    if not value:
        raise SetupError(f"{name} is not set")
    return value


# ── Updates of the user of the stand ─────────────────────────────────────────


class Updates:
    """Telegram updates from the user of the stand; each carries an update_id of its own."""

    def __init__(self, user_id: int) -> None:
        self.user_id = user_id
        self._next = int(time.time())

    def _id(self) -> int:
        self._next += 1
        return self._next

    def user(self) -> dict[str, Any]:
        return {
            "id": self.user_id,
            "is_bot": False,
            "first_name": "Stand",
            "last_name": "User",
            "username": "stand_user",
            "language_code": "en",
            "is_premium": False,
        }

    def message(self, **content: Any) -> dict[str, Any]:
        message = {"message_id": self._id(), "date": int(time.time()), "chat": {"id": self.user_id, "type": "private"}, "from": self.user()}
        return {"update_id": self._id(), "message": {**message, **content}}

    def text(self, text: str) -> dict[str, Any]:
        return self.message(text=text)

    def group_text(self, text: str) -> dict[str, Any]:
        return self.message(chat={"id": -(self.user_id + 1), "type": "group", "title": "Stand group"}, text=text)

    def contact(self, owner_id: int) -> dict[str, Any]:
        return self.message(contact={"phone_number": "+15550100", "first_name": "Stand", "user_id": owner_id})

    def press(self, data: str, message: dict[str, Any] | None = None) -> dict[str, Any]:
        """A press of an inline button; ``message`` ``None`` gives the press the message a stand-in prompt."""
        if message is None:
            message = {"message_id": self._id(), "date": int(time.time()), "chat": {"id": self.user_id, "type": "private"}, "text": "A question"}
        return {"update_id": self._id(), "callback_query": {"id": secrets.token_hex(8), "from": self.user(), "chat_instance": "stand", "data": data, "message": message}}

    def press_without_message(self, data: str) -> dict[str, Any]:
        """A press of an inline button the update of which carries no message, as Telegram may send it."""
        return {"update_id": self._id(), "callback_query": {"id": secrets.token_hex(8), "from": self.user(), "chat_instance": "stand", "data": data}}


def transaction_id() -> str:
    return "".join(secrets.choice(_BASE62) for _ in range(43))


def idempotency_key() -> str:
    return "msg_" + uuid.uuid4().hex


def post_update(webhook: dict[str, str], update: dict[str, Any], secret: str | None) -> tuple[int, str]:
    """Posts an update to a webhook of an adapter; ``secret`` ``None`` sends no secret header."""
    body = json.dumps(update)
    headers = {"content-type": "application/json"}
    if secret is not None:
        headers[WEBHOOK_SECRET_HEADER] = secret
    status, _ = http("POST", webhook["url"], body.encode("utf-8"), headers)
    return status, body


def start_parameter(url: str) -> str:
    return parse_qs(urlsplit(url).query).get("start", [""])[0]


def username_of_link(url: str) -> str:
    return urlsplit(url).path.lstrip("/")


def buttons_of(parameters: dict[str, Any]) -> list[dict[str, Any]]:
    markup = parameters.get("reply_markup")
    if isinstance(markup, str):
        try:
            markup = json.loads(markup)
        except ValueError:
            markup = None
    rows = markup.get("inline_keyboard", []) if isinstance(markup, dict) else []
    return [button for row in rows if isinstance(row, list) for button in row if isinstance(button, dict)]


def reply_markup_of(parameters: dict[str, Any]) -> dict[str, Any]:
    """The reply markup of a Bot API request, sent as an object or as its JSON text; empty when there is none."""
    markup = parameters.get("reply_markup")
    if isinstance(markup, str):
        try:
            markup = json.loads(markup)
        except ValueError:
            markup = None
    return markup if isinstance(markup, dict) else {}


# ── The own checks ───────────────────────────────────────────────────────────


class OwnChecks:
    def __init__(self) -> None:
        self.updates = Updates(int(env("STAND_USER_ID")))
        self.forward_bot = env("STAND_FORWARD_BOT_ID")
        self.forward_tenant = env("STAND_FORWARD_TENANT")
        self.standalone_bot = env("STAND_STANDALONE_BOT_ID")
        self.standalone_url = env("STAND_STANDALONE_ADAPTER_URL")
        self.tenants_url = env("STAND_TENANTS_ADAPTER_URL")
        self.tenants_shop_a_bot = env("STAND_TENANTS_SHOP_A_BOT_ID")
        self.tenants_shop_b_bot = env("STAND_TENANTS_SHOP_B_BOT_ID")
        self.transport_secret = env("STAND_TRANSPORT_SECRET")
        # adapter_settings with a marker of the run: the step logs.no-secrets of run.sh looks for it in the logs.
        self.marked_settings = json.dumps({"stand_marker": env("STAND_SETTINGS_MARKER")})
        # The start by a code: the adapter in the mode only with a forward address, the one in the mode
        # fallback without, and the codes of the run they are sent.
        self.code_only_bot = env("STAND_CODE_ONLY_BOT_ID")
        self.code_only_tenant = env("STAND_CODE_ONLY_TENANT")
        self.code_only_url = env("STAND_CODE_ONLY_ADAPTER_URL")
        self.code_fallback_bot = env("STAND_CODE_FALLBACK_BOT_ID")
        self.code_fallback_url = env("STAND_CODE_FALLBACK_ADAPTER_URL")
        self.code_text = env("STAND_CODE_TEXT")
        self.code_text_separated = env("STAND_CODE_TEXT_SEPARATED")
        webhooks = stub("GET", "/stand/webhooks")
        bots = (self.forward_bot, self.standalone_bot, self.tenants_shop_a_bot, self.tenants_shop_b_bot, self.code_only_bot, self.code_fallback_bot)
        if any(bot not in webhooks for bot in bots):
            raise SetupError("the adapters of the own checks have not registered their webhooks")
        self.webhooks = webhooks
        self.forward_webhook = webhooks[self.forward_bot]
        self.standalone_webhook = webhooks[self.standalone_bot]
        self.events_path = f"/registrar/api/channels/{CHANNEL_TYPE}/webhook"

    def run(self) -> bool:
        checks: list[tuple[str, Callable[[], None]]] = [
            ("inbound.no-secret", self.inbound_no_secret),
            ("inbound.wrong-secret", self.inbound_wrong_secret),
            ("separation.veriqa", self.separation_veriqa),
            ("separation.foreign", self.separation_foreign),
            ("events.standalone", self.events_standalone),
            ("settings.disable-notification", self.settings_disable_notification),
            ("idempotency.send-message-once", self.idempotency_send_message_once),
            ("idempotency.report-outcome-once", self.idempotency_report_outcome_once),
            ("idempotency.failure-unbinds", self.idempotency_failure_unbinds),
            ("tenant.fallback", self.tenant_fallback),
            ("tenant.unknown", self.tenant_unknown),
            ("tenant.event-route", self.tenant_event_route),
            ("wire.channel-type", self.wire_channel_type),
            ("wire.body-limit", self.wire_body_limit),
            ("classify.not-sent", self.classify_not_sent),
            ("settings.disable-notification-malformed", self.settings_disable_notification_malformed),
            ("deep-link.form", self.deep_link_form),
            ("report-outcome.replace", self.report_outcome_replace),
            ("deep-link.rebind", self.deep_link_rebind),
            ("webhook.replay", self.webhook_replay),
            ("identity.shape", self.identity_shape),
            ("callback.no-message", self.callback_no_message),
            ("phone-request.keyboard", self.phone_request_keyboard),
            ("phone-request.no-content", self.phone_request_no_content),
            ("unaddressed.text", self.unaddressed_text),
            ("report-outcome.takes-down-phone-request", self.report_outcome_takes_down_phone_request),
            ("code-start.only-classify", self.code_start_only_classify),
            ("code-start.deep-link", self.code_start_deep_link),
            ("code-start.off-forwards", self.code_start_off_forwards),
        ]
        passed = True
        for name, check in checks:
            stub("POST", "/stand/clear")
            try:
                check()
            except CheckFailed as failure:
                print(f"FAIL {name}: {failure}", flush=True)
                passed = False
            except (OSError, ValueError) as error:
                print(f"FAIL {name}: the stand did not answer ({type(error).__name__})", flush=True)
                passed = False
            else:
                print(f"PASS {name}", flush=True)
        stub("POST", "/stand/clear")
        return passed

    # ── The recorder ─────────────────────────────────────────────────────────

    def recorded(self) -> tuple[list[dict[str, Any]], list[str]]:
        records = stub("GET", "/stand/records")
        return records["events"], records["forwards"]

    def bot_api(self, *methods: str) -> list[dict[str, Any]]:
        return [entry for entry in stub("GET", "/stand/records")["bot_api"] if not methods or entry["method"] in methods]

    def expect_no_effect(self) -> None:
        events, forwards = self.recorded()
        if events or forwards:
            raise CheckFailed(f"the recorder saw {len(events)} event(s) and {len(forwards)} forwarded update(s)")

    def expect_no_sending(self) -> None:
        sent = self.bot_api("sendMessage", "editMessageText")
        if sent:
            raise CheckFailed(f"the Bot API got {len(sent)} message(s)")

    # ── Calls of Veriqa ──────────────────────────────────────────────────────

    def call(
        self,
        url: str,
        operation: str,
        payload: dict[str, Any],
        *,
        tenant: str | None = None,
        settings: str | None = None,
        channel_type: str = CHANNEL_TYPE,
        key: str | None = None,
    ) -> dict[str, Any]:
        """A call of Veriqa signed with the transport secret of the stand; its answer, which must be a 200."""
        envelope = {
            "version": WIRE_VERSION,
            "operation": operation,
            "channel_type": channel_type,
            "tenant": tenant,
            "adapter_settings": settings,
            "payload": payload,
        }
        body = json.dumps(envelope)
        status, answer = http("POST", url, body.encode("utf-8"), sign(self.transport_secret, body, key))
        if status != 200:
            raise CheckFailed(f"the {operation} call was answered {status}")
        document = json.loads(answer or b"null")
        if not isinstance(document, dict):
            raise CheckFailed(f"the {operation} call was answered with no envelope")
        return document

    @staticmethod
    def result(answer: dict[str, Any], operation: str) -> dict[str, Any]:
        result = answer.get("result")
        if not isinstance(result, dict):
            raise CheckFailed(f"the {operation} call was answered {json.dumps(answer.get('error'))} instead of a result")
        return result

    @staticmethod
    def error_code(answer: dict[str, Any]) -> str | None:
        error = answer.get("error")
        return error.get("code") if isinstance(error, dict) else None

    def send_message(self, url: str, *, key: str | None = None, settings: str | None = None, tenant: str | None = None) -> dict[str, Any]:
        payload = {"channel_user_id": str(self.updates.user_id), "text": "A message of the stand", "kind": "plain_text"}
        return self.call(url, "send-message", payload, key=key, settings=settings if settings is not None else self.marked_settings, tenant=tenant)

    def send_prompt(self, url: str, transaction: str, *, tenant: str | None = None) -> dict[str, Any]:
        payload = {
            "transaction_id": transaction,
            "channel_user_id": str(self.updates.user_id),
            "prompt": {"variant": "suppressed", "reason": "display_disabled_by_configuration", "ownership": {}, "recipient_locale": None, "recipient_time_zone": None},
            "rendered": {"text": "Sign in to the stand?", "confirm_label": CONFIRM_LABEL, "decline_label": DECLINE_LABEL, "locale": "en"},
        }
        return self.result(self.call(url, "send-prompt", payload, tenant=tenant, settings=self.marked_settings), "send-prompt")

    def report_outcome(
        self,
        url: str,
        transaction: str,
        *,
        key: str | None = None,
        replace: bool = False,
        prompt_message: dict[str, Any] | None = None,
        inbound_token: str | None = None,
        phone_request_message: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        payload = {
            "transaction_id": transaction,
            "outcome": "confirmed",
            "channel_user_id": str(self.updates.user_id),
            "status_text": STATUS_TEXT,
            "inbound_token": inbound_token,
            "prompt_message": prompt_message,
            "phone_request_message": phone_request_message,
            "display_intent": "replace_prompt" if replace else "new_message",
        }
        return self.call(url, "report-outcome", payload, key=key, settings=self.marked_settings)

    def deep_link(self, url: str, transaction: str, *, tenant: str | None = None) -> dict[str, Any]:
        return self.call(url, "deep-link", {"transaction_id": transaction}, tenant=tenant, settings=self.marked_settings)

    def deep_link_url(self, url: str, transaction: str, *, tenant: str | None = None) -> str:
        link = self.result(self.deep_link(url, transaction, tenant=tenant), "deep-link").get("url")
        if not isinstance(link, str):
            raise CheckFailed(f"the deep link of tenant {tenant} carried no url")
        return link

    def prompt_buttons(self, transaction: str) -> dict[str, str]:
        """Sends a prompt of the transaction through the standalone adapter; the callback data of its buttons by caption."""
        self.send_prompt(self.standalone_url, transaction)
        prompts = [entry for entry in self.bot_api("sendMessage") if buttons_of(entry["parameters"])]
        if not prompts:
            raise CheckFailed("the prompt reached the Bot API without buttons")
        return {str(button.get("text")): str(button.get("callback_data")) for button in buttons_of(prompts[-1]["parameters"])}

    # ── The webhook ──────────────────────────────────────────────────────────

    def inbound_no_secret(self) -> None:
        status, _ = post_update(self.forward_webhook, self.updates.text("/start auth_" + transaction_id()), secret=None)
        if status not in WEBHOOK_REFUSALS:
            raise CheckFailed(f"an update without the secret was answered {status}, not {' or '.join(map(str, WEBHOOK_REFUSALS))}")
        self.expect_no_effect()

    def inbound_wrong_secret(self) -> None:
        # A made-up secret, and the secret of another bot of the stand.
        for secret in (secrets.token_urlsafe(32), self.standalone_webhook["secret_token"]):
            status, _ = post_update(self.forward_webhook, self.updates.text("/start auth_" + transaction_id()), secret)
            if status not in WEBHOOK_REFUSALS:
                raise CheckFailed(f"an update with a wrong secret was answered {status}, not {' or '.join(map(str, WEBHOOK_REFUSALS))}")
        self.expect_no_effect()

    def separation_veriqa(self) -> None:
        transaction = transaction_id()
        status, _ = post_update(self.forward_webhook, self.updates.text("/start auth_" + transaction), self.forward_webhook["secret_token"])
        if status != 200:
            raise CheckFailed(f"the update was answered {status}")
        events, forwards = self.recorded()
        route = f"{self.events_path}/{self.forward_tenant}"
        seen = [(event["path"], event["body"].get("event"), event["body"].get("transaction_id")) for event in events]
        if seen != [(route, "auth_start", transaction)]:
            raise CheckFailed(f"expected one auth_start of the transaction on {route}, the recorder saw {seen}")
        if forwards:
            raise CheckFailed(f"{len(forwards)} update(s) were forwarded to the bot")

    def separation_foreign(self) -> None:
        updates = [
            self.updates.text("Hello"),
            self.updates.text("/start"),
            self.updates.press("shop_item_42"),
            self.updates.contact(self.updates.user_id),
        ]
        sent = []
        for update in updates:
            status, body = post_update(self.forward_webhook, update, self.forward_webhook["secret_token"])
            if status != 200:
                raise CheckFailed(f"update {update['update_id']} was answered {status}")
            sent.append(body)
        events, forwards = self.recorded()
        if events:
            raise CheckFailed(f"{len(events)} event(s) reached the core")
        if forwards != sent:
            raise CheckFailed(f"{len(sent)} update(s) were sent, {len(forwards)} came to the bot, not byte for byte in order")

    def events_standalone(self) -> None:
        for update in (self.updates.text("Hello"), self.updates.contact(self.updates.user_id), self.updates.contact(self.updates.user_id + 1)):
            status, _ = post_update(self.standalone_webhook, update, self.standalone_webhook["secret_token"])
            if status != 200:
                raise CheckFailed(f"update {update['update_id']} was answered {status}")
        events, forwards = self.recorded()
        seen = [(event["path"], event["body"].get("event")) for event in events]
        expected = [(self.events_path, "unaddressed"), (self.events_path, "phone_shared")]
        if seen != expected:
            raise CheckFailed(f"expected {expected}, the recorder saw {seen}")
        if forwards:
            raise CheckFailed(f"{len(forwards)} update(s) were forwarded without a forward address")

    def settings_disable_notification(self) -> None:
        for settings, expected in (('{"disable_notification": true}', True), (None, False)):
            self.send_message_with_settings(settings)
            calls = self.bot_api("sendMessage")
            if not calls or calls[-1]["bot_id"] != self.standalone_bot:
                raise CheckFailed("the send-message call reached no sendMessage of the bot")
            value = calls[-1]["parameters"].get("disable_notification", False)
            if value is not expected:
                raise CheckFailed(f"adapter_settings {settings!r} gave disable_notification = {value!r}")

    def send_message_with_settings(self, settings: str | None) -> None:
        """A send-message call of Veriqa with exactly ``settings`` as adapter_settings; it must give a result."""
        payload = {"channel_user_id": str(self.updates.user_id), "text": "A message of the stand", "kind": "plain_text"}
        self.result(self.call(self.standalone_url, "send-message", payload, settings=settings), "send-message")

    # ── The side of Veriqa: idempotency ──────────────────────────────────────

    def idempotency_send_message_once(self) -> None:
        key = idempotency_key()
        first = self.result(self.send_message(self.standalone_url, key=key), "send-message")
        second = self.result(self.send_message(self.standalone_url, key=key), "send-message")
        if first != second:
            raise CheckFailed(f"the repeat under the same key was answered {second}, the first call {first}")
        sent = self.bot_api("sendMessage")
        if len(sent) != 1:
            raise CheckFailed(f"two calls under one key gave {len(sent)} sendMessage, not 1")

    def idempotency_report_outcome_once(self) -> None:
        key = idempotency_key()
        transaction = transaction_id()
        first = self.result(self.report_outcome(self.standalone_url, transaction, key=key), "report-outcome")
        second = self.result(self.report_outcome(self.standalone_url, transaction, key=key), "report-outcome")
        if first != second:
            raise CheckFailed(f"the repeat under the same key was answered {second}, the first call {first}")
        actions = self.bot_api("sendMessage", "editMessageText")
        if len(actions) != 1:
            raise CheckFailed(f"two calls under one key gave {len(actions)} message(s) of the Bot API, not 1")

    def idempotency_failure_unbinds(self) -> None:
        key = idempotency_key()
        stub("POST", "/stand/refuse-send")
        refused = self.send_message(self.standalone_url, key=key)
        if "result" in refused or self.error_code(refused) is None:
            raise CheckFailed(f"a send-message the Bot API refused was answered {json.dumps(refused)}, not an error")
        stub("POST", "/stand/clear")
        self.result(self.send_message(self.standalone_url, key=key), "send-message")
        sent = self.bot_api("sendMessage")
        if len(sent) != 1:
            raise CheckFailed(f"the repeat after a failed attempt gave {len(sent)} sendMessage, not 1")

    # ── The side of Veriqa: tenants ──────────────────────────────────────────

    def tenant_fallback(self) -> None:
        link = self.deep_link_url(self.standalone_url, transaction_id(), tenant=TENANT_WITHOUT_RECORD)
        expected = BOT_USERNAME.format(bot_id=self.standalone_bot)
        if urlsplit(link).netloc != DEEP_LINK_HOST or username_of_link(link) != expected:
            raise CheckFailed(f"a tenant without a record got a link of {username_of_link(link)!r}, not of the default bot {expected!r}")

    def tenant_unknown(self) -> None:
        link = self.deep_link(self.tenants_url, transaction_id(), tenant=TENANT_WITHOUT_RECORD)
        if self.error_code(link) != DEEP_LINK_GENERATION_FAILED:
            raise CheckFailed(f"deep-link of a tenant without any record was answered {json.dumps(link)}, not {DEEP_LINK_GENERATION_FAILED}")
        message = self.send_message(self.tenants_url, tenant=TENANT_WITHOUT_RECORD)
        if self.error_code(message) != CHANNEL_NOT_AVAILABLE:
            raise CheckFailed(f"send-message of a tenant without any record was answered {json.dumps(message)}, not {CHANNEL_NOT_AVAILABLE}")
        self.expect_no_sending()

    def tenant_event_route(self) -> None:
        transaction = transaction_id()
        start = start_parameter(self.deep_link_url(self.standalone_url, transaction, tenant=SHOP_B))
        status, _ = post_update(self.standalone_webhook, self.updates.text(f"/start {start}"), self.standalone_webhook["secret_token"])
        if status != 200:
            raise CheckFailed(f"the update was answered {status}")
        events, _ = self.recorded()
        route = f"{self.events_path}/{SHOP_B}"
        seen = [(event["path"], event["body"].get("event"), event["body"].get("transaction_id")) for event in events]
        if seen != [(route, "auth_start", transaction)]:
            raise CheckFailed(f"expected one auth_start of the transaction on {route}, the recorder saw {seen}")

    # ── The side of Veriqa: the envelope ─────────────────────────────────────

    def wire_channel_type(self) -> None:
        payload = {"channel_user_id": str(self.updates.user_id), "text": "A message of the stand", "kind": "plain_text"}
        answer = self.call(self.standalone_url, "send-message", payload, channel_type="other-channel", settings=self.marked_settings)
        if self.error_code(answer) != CHANNEL_NOT_AVAILABLE:
            raise CheckFailed(f"a call of another channel type was answered {json.dumps(answer)}, not {CHANNEL_NOT_AVAILABLE}")
        self.expect_no_sending()

    def wire_body_limit(self) -> None:
        payload = {"channel_user_id": str(self.updates.user_id), "text": "a" * OVERSIZED_TEXT_LENGTH, "kind": "plain_text"}
        envelope = {"version": WIRE_VERSION, "operation": "send-message", "channel_type": CHANNEL_TYPE, "tenant": None, "adapter_settings": None, "payload": payload}
        body = json.dumps(envelope)
        try:
            status, answer = http("POST", self.standalone_url, body.encode("utf-8"), sign(self.transport_secret, body))
        except OSError:
            # The adapter may close the connection without reading the rest of the body: a refusal as well.
            status, answer = None, b""
        if status == 200:
            try:
                document = json.loads(answer or b"null")
            except ValueError:
                document = None
            if isinstance(document, dict) and "result" in document:
                raise CheckFailed("a call over 1 MB was answered 200 with a result")
        self.expect_no_sending()

    # ── The side of Telegram ─────────────────────────────────────────────────

    def classify_not_sent(self) -> None:
        # A marker with an id one character short, and a group message with a valid marker.
        for webhook, forwarding in ((self.forward_webhook, True), (self.standalone_webhook, False)):
            stub("POST", "/stand/clear")
            sent = []
            for update in (self.updates.text("/start auth_" + transaction_id()[:42]), self.updates.group_text("/start auth_" + transaction_id())):
                status, body = post_update(webhook, update, webhook["secret_token"])
                if status != 200:
                    raise CheckFailed(f"update {update['update_id']} was answered {status}")
                sent.append(body)
            events, forwards = self.recorded()
            if events:
                raise CheckFailed(f"{len(events)} event(s) reached the core, forwarding {'on' if forwarding else 'off'}")
            expected = sent if forwarding else []
            if forwards != expected:
                raise CheckFailed(f"forwarding {'on' if forwarding else 'off'}: {len(forwards)} update(s) came to the bot, expected {len(expected)} byte for byte")

    def settings_disable_notification_malformed(self) -> None:
        for settings in ("not json", "{}"):
            self.send_message_with_settings(settings)
            calls = self.bot_api("sendMessage")
            if not calls:
                raise CheckFailed(f"adapter_settings {settings!r}: the call reached no sendMessage")
            if calls[-1]["parameters"].get("disable_notification") is True:
                raise CheckFailed(f"adapter_settings {settings!r} gave disable_notification = true")

    def deep_link_form(self) -> None:
        transaction = transaction_id()
        link = self.deep_link_url(self.standalone_url, transaction)
        address = urlsplit(link)
        expected = BOT_USERNAME.format(bot_id=self.standalone_bot)
        if address.scheme != "https" or address.netloc != DEEP_LINK_HOST or username_of_link(link) != expected:
            raise CheckFailed(f"the deep link {link!r} is not https://{DEEP_LINK_HOST}/{expected}")
        start = start_parameter(link)
        if not start.endswith(transaction) or len(start) > MAX_START_PARAMETER_LENGTH:
            raise CheckFailed(f"the start parameter is {len(start)} characters long or does not carry the transaction")

    def report_outcome_replace(self) -> None:
        transaction = transaction_id()
        prompt = self.send_prompt(self.standalone_url, transaction).get("message")
        if not isinstance(prompt, dict):
            raise CheckFailed("send-prompt gave no message to replace")

        # The prompt named: it is edited into the status text, with its buttons gone.
        seen = len(self.bot_api())
        self.result(self.report_outcome(self.standalone_url, transaction, replace=True, prompt_message=prompt), "report-outcome")
        calls = self.bot_api()[seen:]
        edits = [entry for entry in calls if entry["method"] == "editMessageText"]
        if [entry["method"] for entry in calls if entry["method"] in ("sendMessage", "editMessageText")] != ["editMessageText"]:
            raise CheckFailed(f"replacing a named prompt gave the Bot API {[entry['method'] for entry in calls]}, not one editMessageText")
        edit = edits[0]["parameters"]
        if str(edit.get("message_id")) != str(prompt.get("message_id")) or edit.get("text") != STATUS_TEXT or buttons_of(edit):
            raise CheckFailed("the edit is not the status text on the prompt message without buttons")

        # No prompt named: a new message.
        seen = len(self.bot_api())
        self.result(self.report_outcome(self.standalone_url, transaction, replace=True, prompt_message=None), "report-outcome")
        methods = [entry["method"] for entry in self.bot_api()[seen:] if entry["method"] in ("sendMessage", "editMessageText")]
        if methods != ["sendMessage"]:
            raise CheckFailed(f"replacing with no prompt message gave the Bot API {methods}, not one sendMessage")

        # An inbound token of a press: the callback query is answered.
        pressed, token = self.inbound_token_of_press()
        seen = len(self.bot_api())
        self.result(self.report_outcome(self.standalone_url, pressed, inbound_token=token), "report-outcome")
        methods = [entry["method"] for entry in self.bot_api()[seen:]]
        if "answerCallbackQuery" not in methods:
            raise CheckFailed(f"report-outcome with an inbound token gave the Bot API {methods}, no answerCallbackQuery")

    def report_outcome_takes_down_phone_request(self) -> None:
        transaction = transaction_id()
        prompt = self.send_prompt(self.standalone_url, transaction).get("message")
        if not isinstance(prompt, dict):
            raise CheckFailed("send-prompt gave no message to name")

        # A final outcome that names a phone number request: a new message that removes the reply
        # keyboard, and no edit of the prompt even though the intent asks to replace it.
        seen = len(self.bot_api())
        self.result(
            self.report_outcome(
                self.standalone_url, transaction, replace=True, prompt_message=prompt, phone_request_message=prompt
            ),
            "report-outcome",
        )
        calls = [entry for entry in self.bot_api()[seen:] if entry["method"] in ("sendMessage", "editMessageText")]
        if [entry["method"] for entry in calls] != ["sendMessage"]:
            raise CheckFailed(f"the outcome naming a phone request gave the Bot API {[entry['method'] for entry in calls]}, not one sendMessage")
        sent = calls[0]["parameters"]
        if sent.get("text") != STATUS_TEXT or reply_markup_of(sent).get("remove_keyboard") is not True:
            raise CheckFailed("the outcome naming a phone request is not the status text with remove_keyboard")

    def inbound_token_of_press(self) -> tuple[str, str]:
        """Presses the confirm button of a prompt of a new transaction; the transaction and the inbound token of its auth_confirm."""
        transaction = transaction_id()
        buttons = self.prompt_buttons(transaction)
        prompt = [entry for entry in self.bot_api("sendMessage") if buttons_of(entry["parameters"])][-1]["result"]
        seen = len(self.recorded()[0])
        press = self.updates.press(buttons[CONFIRM_LABEL], prompt)
        status, _ = post_update(self.standalone_webhook, press, self.standalone_webhook["secret_token"])
        if status != 200:
            raise CheckFailed(f"the press was answered {status}")
        events = [event["body"] for event in self.recorded()[0][seen:] if event["body"].get("transaction_id") == transaction]
        if [event.get("event") for event in events] != ["auth_confirm"] or not isinstance(events[0].get("inbound_token"), str):
            raise CheckFailed(f"the press of the confirm button gave {events}, not one auth_confirm with an inbound token")
        return transaction, events[0]["inbound_token"]

    def deep_link_rebind(self) -> None:
        transaction = transaction_id()
        first = self.deep_link_url(self.tenants_url, transaction, tenant=SHOP_A)
        second = self.deep_link_url(self.tenants_url, transaction, tenant=SHOP_B)
        expected = BOT_USERNAME.format(bot_id=self.tenants_shop_a_bot)
        if username_of_link(first) != expected or username_of_link(second) != expected:
            raise CheckFailed(f"the links of the transaction name {username_of_link(first)!r} and {username_of_link(second)!r}, not twice {expected!r}")
        webhook = self.webhooks[self.tenants_shop_a_bot]
        status, _ = post_update(webhook, self.updates.text(f"/start {start_parameter(second)}"), webhook["secret_token"])
        if status != 200:
            raise CheckFailed(f"the update was answered {status}")
        events, _ = self.recorded()
        route = f"{self.events_path}/{SHOP_A}"
        seen = [(event["path"], event["body"].get("event"), event["body"].get("transaction_id")) for event in events]
        if seen != [(route, "auth_start", transaction)]:
            raise CheckFailed(f"expected one auth_start of the transaction on {route}, the recorder saw {seen}")

    def webhook_replay(self) -> None:
        update = self.updates.text("Hello")
        for _ in range(2):
            status, _ = post_update(self.standalone_webhook, update, self.standalone_webhook["secret_token"])
            if status != 200:
                raise CheckFailed(f"the update was answered {status}")
        events, _ = self.recorded()
        ids = [event["webhook_id"] for event in events]
        if len(ids) != 2 or ids[0] is None or ids[0] != ids[1]:
            raise CheckFailed(f"one update delivered twice gave the webhook-ids {ids}, not two equal ones")

    def identity_shape(self) -> None:
        with_photo = Updates(self.updates.user_id + PHOTO_USER_OFFSET)
        stub("POST", "/stand/user-photo", {"user_id": with_photo.user_id})
        for updates, has_photo in ((with_photo, True), (self.updates, False)):
            transaction = transaction_id()
            status, _ = post_update(self.standalone_webhook, updates.text(f"/start auth_{transaction}"), self.standalone_webhook["secret_token"])
            if status != 200:
                raise CheckFailed(f"the update was answered {status}")
            bodies = [event["body"] for event in self.recorded()[0] if event["body"].get("transaction_id") == transaction]
            if len(bodies) != 1 or not isinstance(bodies[0].get("identity"), dict):
                raise CheckFailed(f"the /start of a user {'with' if has_photo else 'without'} a photo gave {len(bodies)} event(s) with an identity")
            identity = bodies[0]["identity"]
            expected = IDENTITY_KEYS | ({AVATAR_KEY} if has_photo else set())
            if set(identity) != expected:
                raise CheckFailed(f"the identity of a user {'with' if has_photo else 'without'} a photo has the keys {sorted(identity)}, not {sorted(expected)}")
            metadata = identity.get("raw_metadata")
            if not isinstance(metadata, dict) or set(metadata) != RAW_METADATA_KEYS:
                raise CheckFailed(f"raw_metadata has the keys {sorted(metadata) if isinstance(metadata, dict) else metadata}, not {sorted(RAW_METADATA_KEYS)}")
            if has_photo and not str(identity.get(AVATAR_KEY, "")).startswith(AVATAR_PREFIX):
                raise CheckFailed("the avatar is not a data URI of an image")
        document = json.dumps(self.recorded()[0])
        if any(marker in document for marker in BOT_API_FILE_LINK_MARKERS):
            raise CheckFailed("an event carries a link to a file of the Bot API")

    def callback_no_message(self) -> None:
        transaction = transaction_id()
        buttons = self.prompt_buttons(transaction)
        status, _ = post_update(self.standalone_webhook, self.updates.press_without_message(buttons[CONFIRM_LABEL]), self.standalone_webhook["secret_token"])
        if status != 200:
            raise CheckFailed(f"the press was answered {status}")
        bodies = [event["body"] for event in self.recorded()[0] if event["body"].get("transaction_id") == transaction]
        if [body.get("event") for body in bodies] != ["auth_confirm"]:
            raise CheckFailed(f"a press without a message gave {[body.get('event') for body in bodies]}, not one auth_confirm")
        if bodies[0].get("inbound_token") is not None:
            raise CheckFailed("the event of a press without a message carries an inbound token")

    # ── The phone number on request ──────────────────────────────────────────

    def phone_request_payload(self, request: Any) -> dict[str, Any]:
        """The payload of a send-message of the kind of a request for the phone number, with ``request`` as its content."""
        return {
            "channel_user_id": str(self.updates.user_id),
            "text": "Share your phone number",
            "kind": PHONE_NUMBER_REQUEST_KIND,
            "phone_number_request": request,
        }

    def phone_request_keyboard(self) -> None:
        for action in ("skip", "cancel_sign_in"):
            stub("POST", "/stand/clear")
            request = {
                "transaction_id": transaction_id(),
                "share_button_label": SHARE_BUTTON_LABEL,
                "secondary_action": action,
                "secondary_button_label": SECONDARY_BUTTON_LABEL,
            }
            answer = self.call(self.standalone_url, "send-message", self.phone_request_payload(request), settings=self.marked_settings)
            self.result(answer, "send-message")
            sent = self.bot_api("sendMessage")
            if len(sent) != 1 or sent[0]["bot_id"] != self.standalone_bot:
                raise CheckFailed(f"the request with the action {action} gave {len(sent)} sendMessage of the bot, not 1")
            markup = reply_markup_of(sent[0]["parameters"])
            buttons = [button for row in markup.get("keyboard", []) if isinstance(row, list) for button in row if isinstance(button, dict)]
            seen = [(button.get("text"), button.get("request_contact", False)) for button in buttons]
            expected = [(SHARE_BUTTON_LABEL, True), (SECONDARY_BUTTON_LABEL, False)]
            if seen != expected:
                raise CheckFailed(f"the reply keyboard of the request with the action {action} has the buttons {seen}, not {expected}")
            for switch in ("one_time_keyboard", "resize_keyboard"):
                if markup.get(switch) is not True:
                    raise CheckFailed(f"the reply keyboard of the request has {switch} = {markup.get(switch)!r}, not true")

    def phone_request_no_content(self) -> None:
        # The member left out, and the member stated null: neither is a request the adapter can show.
        for payload in (self.phone_request_payload(None), {key: value for key, value in self.phone_request_payload(None).items() if key != "phone_number_request"}):
            envelope = {"version": WIRE_VERSION, "operation": "send-message", "channel_type": CHANNEL_TYPE, "tenant": None, "adapter_settings": None, "payload": payload}
            body = json.dumps(envelope)
            status, answer = http("POST", self.standalone_url, body.encode("utf-8"), sign(self.transport_secret, body))
            try:
                document = json.loads(answer or b"null")
            except ValueError:
                document = None
            if status == 200 and isinstance(document, dict) and "result" in document:
                raise CheckFailed("a request for the phone number without its content was answered 200 with a result")
            self.expect_no_sending()

    def unaddressed_text(self) -> None:
        status, _ = post_update(self.standalone_webhook, self.updates.text(SECONDARY_BUTTON_LABEL), self.standalone_webhook["secret_token"])
        if status != 200:
            raise CheckFailed(f"the update was answered {status}")
        events, _ = self.recorded()
        seen = [(event["path"], event["body"].get("event"), event["body"].get("text")) for event in events]
        expected = [(self.events_path, "unaddressed", SECONDARY_BUTTON_LABEL)]
        if seen != expected:
            raise CheckFailed(f"expected {expected}, the recorder saw {seen}")

    # ── The start by a code ──────────────────────────────────────────────────

    def expect_code_event(self, webhook: dict[str, str], route: str, text: str, *, forwarding: bool) -> None:
        """A message ``text`` of the user to a bot gives one auth_start_by_code with ``text`` as its code, on ``route``, and is not forwarded."""
        stub("POST", "/stand/clear")
        status, _ = post_update(webhook, self.updates.text(text), webhook["secret_token"])
        if status != 200:
            raise CheckFailed(f"the update was answered {status}")
        events, forwards = self.recorded()
        # The code is compared, never printed: it is a secret of a sign-in.
        seen = [(event["path"], event["body"].get("event"), event["body"].get("code") == text) for event in events]
        if seen != [(route, AUTH_START_BY_CODE, True)]:
            raise CheckFailed(f"expected one {AUTH_START_BY_CODE} on {route} with the code as sent, the recorder saw {seen}")
        if events[0]["body"].get("identity", {}).get("channel_user_id") != str(self.updates.user_id):
            raise CheckFailed("the event of the code does not name the user who sent it")
        if forwards:
            raise CheckFailed(f"{len(forwards)} update(s) were forwarded{'' if forwarding else ' without a forward address'}")

    def code_start_only_classify(self) -> None:
        webhook = self.webhooks[self.code_only_bot]
        route = f"{self.events_path}/{self.code_only_tenant}"

        # Digits, as sent and split by a space and a dash: an event of the code, not forwarded.
        for text in (self.code_text, self.code_text_separated):
            self.expect_code_event(webhook, route, text, forwarding=True)

        # The link of a transaction still starts it.
        stub("POST", "/stand/clear")
        transaction = transaction_id()
        status, _ = post_update(webhook, self.updates.text(f"/start {DEEP_LINK_MARKER}{transaction}"), webhook["secret_token"])
        if status != 200:
            raise CheckFailed(f"the update was answered {status}")
        events, forwards = self.recorded()
        seen = [(event["path"], event["body"].get("event"), event["body"].get("transaction_id")) for event in events]
        if seen != [(route, "auth_start", transaction)] or forwards:
            raise CheckFailed(f"expected one auth_start of the transaction on {route} and no forward, the recorder saw {seen} and {len(forwards)} forward(s)")

        # Any other text is forwarded and reaches nobody else.
        stub("POST", "/stand/clear")
        status, body = post_update(webhook, self.updates.text("hello"), webhook["secret_token"])
        if status != 200:
            raise CheckFailed(f"the update was answered {status}")
        events, forwards = self.recorded()
        if events or forwards != [body]:
            raise CheckFailed(f"a plain text gave {len(events)} event(s) and {len(forwards)} forward(s), expected none and the update byte for byte")

    def code_start_deep_link(self) -> None:
        # Only: the link opens the chat of the bot and carries no start parameter.
        link = self.deep_link_url(self.code_only_url, transaction_id(), tenant=self.code_only_tenant)
        address = urlsplit(link)
        expected = BOT_USERNAME.format(bot_id=self.code_only_bot)
        if address.netloc != DEEP_LINK_HOST or username_of_link(link) != expected:
            raise CheckFailed(f"the deep link of the mode only is not a link of {expected!r}")
        if "start" in parse_qs(address.query):
            raise CheckFailed("the deep link of the mode only carries a start parameter")

        # Fallback: the link carries the transaction, and a code is taken beside it.
        transaction = transaction_id()
        link = self.deep_link_url(self.code_fallback_url, transaction)
        if username_of_link(link) != BOT_USERNAME.format(bot_id=self.code_fallback_bot) or start_parameter(link) != DEEP_LINK_MARKER + transaction:
            raise CheckFailed("the deep link of the mode fallback is not the link of its bot with start=auth_<transaction>")
        self.expect_code_event(self.webhooks[self.code_fallback_bot], self.events_path, self.code_text, forwarding=False)

    def code_start_off_forwards(self) -> None:
        # The mode off: digits are a plain text — forwarded, no event.
        status, body = post_update(self.forward_webhook, self.updates.text(self.code_text), self.forward_webhook["secret_token"])
        if status != 200:
            raise CheckFailed(f"the update was answered {status}")
        events, forwards = self.recorded()
        if events or forwards != [body]:
            raise CheckFailed(f"a message of digits with CODE_START=off gave {len(events)} event(s) and {len(forwards)} forward(s), expected none and the update byte for byte")

    # ── The state across a recreation of the container ───────────────────────

    def survive_before(self, key: str, transaction: str) -> dict[str, Any]:
        """Phase 1: a message under ``key`` and the deep link of ``transaction`` for shop-b; what phase 2 needs."""
        stub("POST", "/stand/clear")
        first = self.result(self.send_message(self.standalone_url, key=key), "send-message")
        start = start_parameter(self.deep_link_url(self.standalone_url, transaction, tenant=SHOP_B))
        return {"result": first, "start": start}

    def survive_after(self, key: str, transaction: str, before: dict[str, Any]) -> None:
        """Phase 2: the repeat under ``key`` and the transaction brought in on the bot of the default record."""
        second = self.result(self.send_message(self.standalone_url, key=key), "send-message")
        if second != before["result"]:
            raise CheckFailed(f"the repeat after the recreation was answered {second}, the first call {before['result']}")
        sent = self.bot_api("sendMessage")
        if len(sent) != 1:
            raise CheckFailed(f"the call before and its repeat after the recreation gave {len(sent)} sendMessage, not 1")
        webhook = stub("GET", "/stand/webhooks").get(self.standalone_bot)
        if webhook is None:
            raise CheckFailed("the recreated adapter has not registered its webhook")
        status, _ = post_update(webhook, self.updates.text(f"/start {before['start']}"), webhook["secret_token"])
        if status != 200:
            raise CheckFailed(f"the update was answered {status}")
        events, _ = self.recorded()
        route = f"{self.events_path}/{SHOP_B}"
        seen = [(event["path"], event["body"].get("event"), event["body"].get("transaction_id")) for event in events]
        if seen != [(route, "auth_start", transaction)]:
            raise CheckFailed(f"expected one auth_start of the transaction on {route}, the recorder saw {seen}")


def sign(secret: str, body: str, message_id: str | None = None) -> dict[str, str]:
    """Standard Webhooks headers of a message (scheme v1): HMAC-SHA256 over ``id.timestamp.body``."""
    message_id = message_id or idempotency_key()
    timestamp = str(int(time.time()))
    key = base64.b64decode(secret.removeprefix("whsec_"))
    digest = hmac.new(key, f"{message_id}.{timestamp}.{body}".encode("utf-8"), hashlib.sha256).digest()
    return {
        "content-type": "application/json",
        "webhook-id": message_id,
        "webhook-timestamp": timestamp,
        "webhook-signature": "v1," + base64.b64encode(digest).decode("ascii"),
    }


# ── Waiting and the report ───────────────────────────────────────────────────


def wait_webhooks(bot_ids: list[str], timeout: float) -> bool:
    deadline = time.monotonic() + timeout
    missing = set(bot_ids)
    while time.monotonic() < deadline:
        try:
            missing = set(bot_ids) - set(stub("GET", "/stand/webhooks"))
        except (CheckFailed, OSError):
            pass
        if not missing:
            return True
        time.sleep(POLL_SECONDS)
    print(f"checks: no webhook registered within {timeout:.0f} s for bot(s) {', '.join(sorted(missing))}", file=sys.stderr)
    return False


def wait_adapter(url: str, timeout: float) -> bool:
    """Waits until the adapter answers an unsigned call with 403: it is up and checks the signature."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            if http("POST", url, b"", {"content-type": "application/json"})[0] == 403:
                return True
        except OSError:
            pass
        time.sleep(POLL_SECONDS)
    print(f"checks: the adapter at {url} did not come up within {timeout:.0f} s", file=sys.stderr)
    return False


def suite_report(document: str, code_start: bool) -> bool:
    """Whether the report holds: every scenario as expected, the required ones present, the excluded absent."""
    try:
        report = json.loads(document)
        scenarios = {scenario["id"]: scenario for scenario in report["scenarios"]}
    except (ValueError, KeyError, TypeError):
        print("checks: the report of the suite is not a report", file=sys.stderr)
        return False
    problems = []
    if report.get("outcome") != "passed":
        problems.append(f"outcome is {report.get('outcome')!r}")
    required_scenarios = REQUIRED_SCENARIOS + ([CODE_START_SCENARIO] if code_start else [])
    excluded_scenarios = LINK_SCENARIOS if code_start else [CODE_START_SCENARIO]
    for required in required_scenarios:
        if required not in scenarios:
            problems.append(f"{required} is missing")
    for excluded in excluded_scenarios:
        if excluded in scenarios:
            problems.append(f"{excluded} is in the report, expected absent")
    for scenario_id, scenario in scenarios.items():
        expected = "not_applicable" if scenario_id == NOT_APPLICABLE_SCENARIO else "passed"
        if scenario.get("status") != expected:
            problems.append(f"{scenario_id} is {scenario.get('status')!r} ({scenario.get('reason')}), expected {expected}")
    for problem in problems:
        print(f"checks: {problem}", file=sys.stderr)
    return not problems


def channel_state(url: str, channel: str) -> str | None:
    try:
        status, body = http("GET", url)
        report = json.loads(body)
    except (OSError, ValueError):
        return None
    for check in report.get("checks", []) if status == 200 else []:
        state = check.get("data", {}).get(channel)
        if state is not None:
            return state
    return None


def wait_channel(url: str, channel: str, timeout: float) -> bool:
    deadline = time.monotonic() + timeout
    state = None
    while time.monotonic() < deadline:
        state = channel_state(url, channel)
        if state == "healthy":
            return True
        time.sleep(POLL_SECONDS)
    print(f"checks: {channel} is {state or 'not reported'} after {timeout:.0f} s", file=sys.stderr)
    return False


def survive(arguments: list[str]) -> int:
    """The phases of state.survives-recreate: 0 when the phase held, 1 when it did not."""
    command, key, transaction = arguments[:3]
    checks = OwnChecks()
    try:
        if command == "survive-before":
            print(json.dumps(checks.survive_before(key, transaction)), flush=True)
        else:
            checks.survive_after(key, transaction, json.loads(arguments[3]))
    except CheckFailed as failure:
        print(f"checks: {failure}", file=sys.stderr)
        return 1
    return 0


def main(arguments: list[str]) -> int:
    """Exit code 0 — everything held, 1 — a check failed, 2 — the checks could not run."""
    command = arguments[0] if arguments else ""
    try:
        if command == "own":
            return 0 if OwnChecks().run() else 1
        if (command == "survive-before" and len(arguments) == 3) or (command == "survive-after" and len(arguments) == 4):
            return survive(arguments)
        if command == "wait-webhooks" and len(arguments) == 2:
            return 0 if wait_webhooks(env("STAND_BOT_IDS").split(","), float(arguments[1])) else 1
        if command == "wait-adapter" and len(arguments) == 3:
            return 0 if wait_adapter(arguments[1], float(arguments[2])) else 1
        if command == "suite-report" and arguments[1:] in ([], [CODE_START_RUN]):
            return 0 if suite_report(sys.stdin.read(), code_start=len(arguments) == 2) else 1
        if command == "wait-channel" and len(arguments) == 4:
            return 0 if wait_channel(arguments[1], arguments[2], float(arguments[3])) else 1
    except (SetupError, CheckFailed, OSError, ValueError) as error:
        print(f"checks: {error}", file=sys.stderr)
        return 2
    print(
        "usage: checks.py own | survive-before <key> <transaction> | survive-after <key> <transaction> <first> "
        "| wait-webhooks <seconds> | wait-adapter <url> <seconds> | suite-report [code-start] "
        "| wait-channel <url> <channel> <seconds>",
        file=sys.stderr,
    )
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
