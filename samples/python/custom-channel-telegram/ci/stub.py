# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Platform stub of the stand: a stand-in Telegram, the stand API of the conformance suite and a recorder.

Three roles on one port, standard library only:

- **Bot API** — ``POST /bot<token>/<method>``, the methods the adapter calls. A token the stand did not
  issue is refused as Telegram refuses it. Webhooks registered with ``setWebhook`` are kept per bot.
- **Stand API of the conformance suite** — ``/conformance/reset``, ``/conformance/follow-link``,
  ``/conformance/send-text`` and ``/conformance/platform-log``. ``follow-link`` plays the user: ``/start``
  on the webhook of the bot the link names, then a press of the button of the prompt. ``send-text`` plays
  the user as well: a plain text message to the bot the link names, the link itself not opened.
  ``/conformance/scenario`` is not served: Telegram has no case in which it refuses to deliver a prompt.
- **Recorder** for the own checks of the stand — the events an adapter posts instead of Veriqa
  (``/registrar/api/channels/...``), the updates it forwards to the bot of the integrator
  (``/registrar/forward``) and the parameters of every Bot API request; read with ``GET /stand/records``,
  cleared with ``POST /stand/clear``; the registered webhooks at ``GET /stand/webhooks``.
- **Switches** of the own checks, all lifted by ``POST /stand/clear``: ``POST /stand/refuse-send`` makes
  ``sendMessage`` and ``editMessageText`` fail as Telegram fails a message it does not take;
  ``POST /stand/user-photo`` with ``{"user_id": <id>}`` gives that user a profile photo, served through
  ``getFile`` and the file download of the Bot API (``GET /file/bot<token>/<file path>``).

The credential of a platform action is the numeric identifier of the bot — the part of its token before
the colon, which is not a secret. Tokens never reach the log.
"""

from __future__ import annotations

import json
import os
import re
import secrets
import sys
import threading
import time
import urllib.error
import urllib.request
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
from urllib.parse import parse_qs, urlsplit

# Settings of the stub, from the environment of its container.
PORT_ENV = "STUB_PORT"
BOT_TOKENS_ENV = "STUB_BOT_TOKENS"
USER_ID_ENV = "STUB_USER_ID"
DEFAULT_PORT = 9000

# Markers of Veriqa in a deep link and in the buttons of a prompt.
DEEP_LINK_MARKER = "auth_"
BUTTON_PREFIXES = {"confirm": "vq_confirm_", "decline": "vq_decline_"}

WEBHOOK_SECRET_HEADER = "X-Telegram-Bot-Api-Secret-Token"

# How long a delivery to a webhook may take: the adapter answers once Veriqa has taken the event.
DELIVERY_TIMEOUT_SECONDS = 60.0
# How long the user waits for the prompt after following a link, and how often the log is looked at.
PROMPT_WAIT_SECONDS = 30.0
PROMPT_POLL_SECONDS = 0.2

_BOT_PATH = re.compile(r"\A/bot([0-9]+:[A-Za-z0-9_-]+)/([A-Za-z]+)\Z")
_FILE_PATH = re.compile(r"\A/file/bot([0-9]+:[A-Za-z0-9_-]+)/(.+)\Z")
_REGISTRAR_EVENTS_PREFIX = "/registrar/api/channels/"
_REGISTRAR_FORWARD_PATH = "/registrar/forward"
_BOT_USERNAME = "stand_{bot_id}_bot"
_USERNAME_OF_LINK = re.compile(r"\A/(stand_([0-9]+)_bot)\Z")


# The profile photo of a user the switch names: its identifiers in the Bot API and a small JPEG image.
_PHOTO_FILE_ID = "stand-photo"
_PHOTO_FILE_UNIQUE_ID = "stand-photo-unique"
_PHOTO_FILE_PATH = "photos/stand-photo.jpg"
_PHOTO_JPEG = bytes.fromhex(
    "ffd8ffe000104a46494600010100000100010000ffdb004300080606070605080707070909080a0c140d0c0b0b0c1912130f"
    "141d1a1f1e1d1a1c1c20242e2720222c231c1c2837292c30313434341f27393d38323c2e333432ffc0000b08000100010101"
    "1100ffc4001f0000010501010101010100000000000000000102030405060708090a0bffc400b5100002010303020403"
    "050504040000017d01020300041105122131410613516107227114328191a1082342b1c11552d1f02433627282090a16"
    "1718191a25262728292a3435363738393a434445464748494a535455565758595a636465666768696a737475767778797a"
    "838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7"
    "d8d9dae1e2e3e4e5e6e7e8e9eaf1f2f3f4f5f6f7f8f9faffda0008010100003f00fbd3ffd9"
)

# What Telegram answers to a message it does not take while the switch of the stand is on.
_REFUSAL = {"ok": False, "error_code": 400, "description": "Bad Request: refused by the stand"}


def bot_id_of(token: str) -> str:
    return token.split(":", 1)[0]


class Platform:
    """The state of the stand-in Telegram and of the recorder, shared by the threads of the server."""

    def __init__(self, tokens: set[str], user_id: int) -> None:
        self._tokens = tokens
        self.user_id = user_id
        self.lock = threading.Lock()
        self._next_message_id = 100
        self._next_update_id = 1000
        self.webhooks: dict[str, dict[str, str]] = {}
        self.platform_log: list[dict[str, str]] = []
        self.bot_api_log: list[dict[str, Any]] = []
        self.events: list[dict[str, Any]] = []
        self.forwards: list[str] = []
        self.refuse_send = False
        self.users_with_photo: set[int] = set()

    # ── Bot API ──────────────────────────────────────────────────────────────

    def bot_api(self, token: str, method: str, parameters: dict[str, Any]) -> tuple[int, dict[str, Any]]:
        if token not in self._tokens:
            return HTTPStatus.UNAUTHORIZED, {"ok": False, "error_code": 401, "description": "Unauthorized"}
        bot_id = bot_id_of(token)
        handler = getattr(self, "_method_" + method, None)
        if handler is None:
            return HTTPStatus.NOT_FOUND, {"ok": False, "error_code": 404, "description": "Not Found"}
        with self.lock:
            if self.refuse_send and method in ("sendMessage", "editMessageText"):
                return HTTPStatus.BAD_REQUEST, _REFUSAL
            result = handler(bot_id, parameters)
            self.bot_api_log.append({"bot_id": bot_id, "method": method, "parameters": parameters, "result": result})
        return HTTPStatus.OK, {"ok": True, "result": result}

    def is_token(self, token: str) -> bool:
        return token in self._tokens

    def is_bot(self, bot_id: str) -> bool:
        return any(bot_id_of(token) == bot_id for token in self._tokens)

    def _method_getMe(self, bot_id: str, _: dict[str, Any]) -> dict[str, Any]:
        return {"id": int(bot_id), "is_bot": True, "first_name": f"Stand bot {bot_id}", "username": _BOT_USERNAME.format(bot_id=bot_id)}

    def _method_setWebhook(self, bot_id: str, parameters: dict[str, Any]) -> bool:
        self.webhooks[bot_id] = {"url": str(parameters.get("url", "")), "secret_token": str(parameters.get("secret_token", ""))}
        return True

    def _method_deleteWebhook(self, bot_id: str, _: dict[str, Any]) -> bool:
        self.webhooks.pop(bot_id, None)
        return True

    def _method_sendMessage(self, bot_id: str, parameters: dict[str, Any]) -> dict[str, Any]:
        message = self._message(parameters.get("chat_id"), parameters.get("text"))
        self.platform_log.append(self._action("sent", bot_id, message))
        return message

    def _method_editMessageText(self, bot_id: str, parameters: dict[str, Any]) -> dict[str, Any]:
        message = self._message(parameters.get("chat_id"), parameters.get("text"), parameters.get("message_id"))
        self.platform_log.append(self._action("edited", bot_id, message))
        return message

    def _method_answerCallbackQuery(self, _: str, __: dict[str, Any]) -> bool:
        return True

    def _method_getUserProfilePhotos(self, _: str, parameters: dict[str, Any]) -> dict[str, Any]:
        user_id = parameters.get("user_id")
        if str(user_id) not in {str(user) for user in self.users_with_photo}:
            return {"total_count": 0, "photos": []}
        sizes = [
            {"file_id": f"{_PHOTO_FILE_ID}-{side}", "file_unique_id": f"{_PHOTO_FILE_UNIQUE_ID}-{side}", "width": side, "height": side, "file_size": len(_PHOTO_JPEG)}
            for side in (160, 320, 640)
        ]
        return {"total_count": 1, "photos": [sizes]}

    def _method_getFile(self, _: str, parameters: dict[str, Any]) -> dict[str, Any]:
        file_id = str(parameters.get("file_id", ""))
        return {"file_id": file_id, "file_unique_id": _PHOTO_FILE_UNIQUE_ID, "file_size": len(_PHOTO_JPEG), "file_path": _PHOTO_FILE_PATH}

    def _message(self, chat_id: Any, text: Any, message_id: Any = None) -> dict[str, Any]:
        if message_id is None:
            self._next_message_id += 1
            message_id = self._next_message_id
        chat = int(chat_id) if isinstance(chat_id, str) and chat_id.isdigit() else chat_id
        return {"message_id": message_id, "date": int(time.time()), "chat": {"id": chat, "type": "private"}, "text": text}

    @staticmethod
    def _action(kind: str, bot_id: str, message: dict[str, Any]) -> dict[str, str]:
        return {"kind": kind, "credential": bot_id, "chat_id": str(message["chat"]["id"]), "message_id": str(message["message_id"])}

    # ── The user of the stand ────────────────────────────────────────────────

    def next_update_id(self) -> int:
        with self.lock:
            self._next_update_id += 1
            return self._next_update_id

    def user(self) -> dict[str, Any]:
        return {"id": self.user_id, "is_bot": False, "first_name": "Stand", "last_name": "User", "username": "stand_user", "language_code": "en"}

    def text_update(self, text: str) -> dict[str, Any]:
        """An update with a private text message of the user to a bot."""
        return {
            "update_id": self.next_update_id(),
            "message": {
                "message_id": self.next_update_id(),
                "date": int(time.time()),
                "chat": {"id": self.user_id, "type": "private"},
                "from": self.user(),
                "text": text,
            },
        }

    def follow_link(self, url: str, answer: str) -> None:
        """Opens a deep link as the user and presses the button ``answer`` of the prompt that comes."""
        bot_id = bot_of_link(url)
        start = parse_qs(urlsplit(url).query).get("start", [""])[0]
        if bot_id is None or not start.startswith(DEEP_LINK_MARKER):
            _log(f"follow-link: not a deep link of a stand bot: {url}")
            return
        transaction_id = start[len(DEEP_LINK_MARKER):]
        seen = self._bot_api_count()

        if not self.deliver(bot_id, self.text_update(f"/start {start}")):
            return

        callback_data = BUTTON_PREFIXES[answer] + transaction_id
        prompt = self._wait_for_prompt(callback_data, seen)
        if prompt is None:
            _log(f"follow-link: no prompt with the button {answer} came within {PROMPT_WAIT_SECONDS:.0f} s")
            return
        sender, message = prompt
        press = {
            "update_id": self.next_update_id(),
            "callback_query": {
                "id": secrets.token_hex(8),
                "from": self.user(),
                "chat_instance": "stand",
                "data": callback_data,
                "message": message,
            },
        }
        self.deliver(sender, press)

    def deliver(self, bot_id: str, update: dict[str, Any]) -> bool:
        """Posts an update to the webhook of a bot, as Telegram does; ``True`` on a 2xx answer."""
        with self.lock:
            webhook = self.webhooks.get(bot_id)
        if webhook is None:
            _log(f"delivery: bot {bot_id} has no webhook")
            return False
        request = urllib.request.Request(
            webhook["url"],
            data=json.dumps(update).encode("utf-8"),
            headers={"content-type": "application/json", WEBHOOK_SECRET_HEADER: webhook["secret_token"]},
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=DELIVERY_TIMEOUT_SECONDS) as response:
                return 200 <= response.status < 300
        except (urllib.error.URLError, TimeoutError) as error:
            _log(f"delivery to bot {bot_id} failed: {type(error).__name__}")
            return False

    def _bot_api_count(self) -> int:
        with self.lock:
            return len(self.bot_api_log)

    def _wait_for_prompt(self, callback_data: str, seen: int) -> tuple[str, dict[str, Any]] | None:
        """The bot and the message of the first prompt since ``seen`` that carries the button."""
        deadline = time.monotonic() + PROMPT_WAIT_SECONDS
        while time.monotonic() < deadline:
            with self.lock:
                for entry in self.bot_api_log[seen:]:
                    if entry["method"] == "sendMessage" and callback_data in _buttons_of(entry["parameters"]):
                        return entry["bot_id"], entry["result"]
            time.sleep(PROMPT_POLL_SECONDS)
        return None


def bot_of_link(url: str) -> str | None:
    """The identifier of the bot a link of Telegram names by its user name; ``None`` for any other link."""
    match = _USERNAME_OF_LINK.match(urlsplit(url).path)
    return match.group(2) if match is not None else None


def _buttons_of(parameters: dict[str, Any]) -> list[str]:
    markup = parameters.get("reply_markup")
    rows = markup.get("inline_keyboard", []) if isinstance(markup, dict) else []
    return [button.get("callback_data") for row in rows if isinstance(row, list) for button in row if isinstance(button, dict)]


def _log(text: str) -> None:
    print(f"stub: {text}", file=sys.stderr, flush=True)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    platform: Platform

    def log_message(self, format: str, *args: Any) -> None:  # noqa: A002 - the signature of the base class
        # The request line carries the token of a bot; the stub logs its own decisions instead.
        pass

    def do_GET(self) -> None:
        path = urlsplit(self.path).path
        download = _FILE_PATH.match(path)
        if download is not None:
            if self.platform.is_token(download.group(1)) and download.group(2) == _PHOTO_FILE_PATH:
                self._bytes(HTTPStatus.OK, "image/jpeg", _PHOTO_JPEG)
            else:
                self._empty(HTTPStatus.NOT_FOUND)
            return
        with self.platform.lock:
            if path == "/conformance/platform-log":
                self._json(HTTPStatus.OK, {"actions": list(self.platform.platform_log)})
            elif path == "/stand/records":
                self._json(HTTPStatus.OK, {"events": self.platform.events, "forwards": self.platform.forwards, "bot_api": self.platform.bot_api_log})
            elif path == "/stand/webhooks":
                self._json(HTTPStatus.OK, self.platform.webhooks)
            else:
                self._empty(HTTPStatus.NOT_FOUND)

    def do_POST(self) -> None:
        path = urlsplit(self.path).path
        body = self._read_body()

        bot_call = _BOT_PATH.match(path)
        if bot_call is not None:
            status, answer = self.platform.bot_api(bot_call.group(1), bot_call.group(2), _json_object(body))
            self._json(status, answer)
        elif path == "/conformance/reset":
            with self.platform.lock:
                self.platform.platform_log.clear()
            self._empty(HTTPStatus.NO_CONTENT)
        elif path == "/conformance/follow-link":
            request = _json_object(body)
            url, answer = request.get("url"), request.get("answer")
            if not isinstance(url, str) or answer not in BUTTON_PREFIXES:
                self._empty(HTTPStatus.BAD_REQUEST)
                return
            # Accepted at once; the user acts in the background, while this server keeps answering the
            # Bot API requests of the same chain.
            threading.Thread(target=self.platform.follow_link, args=(url, answer), daemon=True).start()
            self._empty(HTTPStatus.ACCEPTED)
        elif path == "/conformance/send-text":
            request = _json_object(body)
            url, text = request.get("url"), request.get("text")
            bot_id = bot_of_link(url) if isinstance(url, str) else None
            if bot_id is None or not self.platform.is_bot(bot_id) or not isinstance(text, str):
                self._empty(HTTPStatus.BAD_REQUEST)
                return
            # Accepted at once, as follow-link is: the adapter answers the update once the event is taken. The
            # text is the code of a sign-in and is not logged.
            update = self.platform.text_update(text)
            threading.Thread(target=self.platform.deliver, args=(bot_id, update), daemon=True).start()
            self._empty(HTTPStatus.ACCEPTED)
        elif path.startswith(_REGISTRAR_EVENTS_PREFIX):
            with self.platform.lock:
                self.platform.events.append({"path": path, "webhook_id": self.headers.get("webhook-id"), "body": _json_object(body)})
            self._empty(HTTPStatus.OK)
        elif path == _REGISTRAR_FORWARD_PATH:
            with self.platform.lock:
                self.platform.forwards.append(body.decode("utf-8", errors="replace"))
            self._empty(HTTPStatus.OK)
        elif path == "/stand/refuse-send":
            with self.platform.lock:
                self.platform.refuse_send = True
            self._empty(HTTPStatus.NO_CONTENT)
        elif path == "/stand/user-photo":
            user_id = _json_object(body).get("user_id")
            if not isinstance(user_id, int):
                self._empty(HTTPStatus.BAD_REQUEST)
                return
            with self.platform.lock:
                self.platform.users_with_photo.add(user_id)
            self._empty(HTTPStatus.NO_CONTENT)
        elif path == "/stand/clear":
            with self.platform.lock:
                self.platform.events.clear()
                self.platform.forwards.clear()
                self.platform.bot_api_log.clear()
                self.platform.refuse_send = False
                self.platform.users_with_photo.clear()
            self._empty(HTTPStatus.NO_CONTENT)
        else:
            self._empty(HTTPStatus.NOT_FOUND)

    def _read_body(self) -> bytes:
        """The body of the request, sent with a length or in chunks (the .NET client of the suite chunks JSON)."""
        if "chunked" not in self.headers.get("transfer-encoding", "").lower():
            return self.rfile.read(int(self.headers.get("content-length") or 0))
        chunks: list[bytes] = []
        while True:
            size = int(self.rfile.readline().split(b";", 1)[0].strip() or b"0", 16)
            if size == 0:
                # Trailer fields, if any, end with an empty line.
                while self.rfile.readline() not in (b"\r\n", b"\n", b""):
                    pass
                return b"".join(chunks)
            chunks.append(self.rfile.read(size))
            self.rfile.readline()

    def _json(self, status: int, document: Any) -> None:
        payload = json.dumps(document).encode("utf-8")
        self.send_response(status)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def _bytes(self, status: int, content_type: str, payload: bytes) -> None:
        self.send_response(status)
        self.send_header("content-type", content_type)
        self.send_header("content-length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def _empty(self, status: int) -> None:
        self.send_response(status)
        self.send_header("content-length", "0")
        self.end_headers()


def _json_object(body: bytes) -> dict[str, Any]:
    try:
        parsed = json.loads(body) if body else {}
    except ValueError:
        return {}
    return parsed if isinstance(parsed, dict) else {}


def main() -> None:
    tokens = {token.strip() for token in os.environ.get(BOT_TOKENS_ENV, "").split(",") if token.strip()}
    if not tokens:
        _log(f"{BOT_TOKENS_ENV} is not set")
        sys.exit(2)
    Handler.platform = Platform(tokens, int(os.environ.get(USER_ID_ENV, "1")))
    port = int(os.environ.get(PORT_ENV, DEFAULT_PORT))
    server = ThreadingHTTPServer(("0.0.0.0", port), Handler)
    server.daemon_threads = True
    _log(f"listening on port {port} for {len(tokens)} bot(s)")
    server.serve_forever()


if __name__ == "__main__":
    main()
