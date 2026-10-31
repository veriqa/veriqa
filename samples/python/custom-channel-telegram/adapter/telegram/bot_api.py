# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""A thin client of the Telegram Bot API.

The token of a bot is part of every request address of the Bot API, so nothing here lets an address out:
errors carry the method and the description Telegram gave, never the URL or the exception text of the
HTTP client, and the loggers of the HTTP client are kept quiet by the composition of the adapter.
"""

from __future__ import annotations

from typing import Any

import httpx

from adapter.core.seam import PlatformError

# How long one Bot API request may take, in seconds.
REQUEST_TIMEOUT_SECONDS = 10.0


class BotApiError(PlatformError):
    """The Bot API refused a request or could not be reached."""


class BotApi:
    """Calls the methods of the Bot API at ``base_url`` — ``https://api.telegram.org`` or a stand-in."""

    def __init__(self, base_url: str, http: httpx.AsyncClient) -> None:
        self._base_url = base_url
        self._http = http

    async def call(self, token: str, method: str, parameters: dict[str, Any] | None = None, timeout: float = REQUEST_TIMEOUT_SECONDS) -> Any:
        """The ``result`` of a method; a refusal or a transport failure raises ``BotApiError``."""
        try:
            response = await self._http.post(f"{self._base_url}/bot{token}/{method}", json=parameters or {}, timeout=timeout)
        except httpx.HTTPError as exception:
            raise BotApiError(f"{method}: {type(exception).__name__}") from None

        try:
            answer = response.json()
        except ValueError:
            raise BotApiError(f"{method}: status {response.status_code} without a JSON answer") from None

        if not isinstance(answer, dict) or answer.get("ok") is not True:
            description = answer.get("description") if isinstance(answer, dict) else None
            raise BotApiError(f"{method}: {description if isinstance(description, str) else f'status {response.status_code}'}")
        return answer.get("result")

    async def download(self, token: str, file_path: str, limit: int) -> bytes | None:
        """The bytes of a file of the bot, or ``None`` when the file is longer than ``limit``; the download
        stops at the chunk that crosses the limit."""
        try:
            async with self._http.stream("GET", f"{self._base_url}/file/bot{token}/{file_path}", timeout=REQUEST_TIMEOUT_SECONDS) as response:
                if response.status_code != httpx.codes.OK:
                    raise BotApiError(f"file download: status {response.status_code}")
                content = bytearray()
                async for chunk in response.aiter_bytes():
                    content.extend(chunk)
                    if len(content) > limit:
                        return None
                return bytes(content)
        except httpx.HTTPError as exception:
            raise BotApiError(f"file download: {type(exception).__name__}") from None
