# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""The identity of a Telegram user in an event — field for field what the built-in Telegram channel of
Veriqa takes from the same user, so the claims of both channels match."""

from __future__ import annotations

import base64
import logging
from typing import Any

from adapter.common import fingerprint
from adapter.core.seam import PlatformError
from adapter.telegram.bot_api import BotApi

_log = logging.getLogger(__name__)

# Largest avatar image carried in an event: 64 KB, the limit Veriqa puts on an avatar data URI.
MAX_AVATAR_BYTES = 65_536

# Signatures of the image types an avatar may have, and their media types.
_IMAGE_SIGNATURES = (
    (b"\xff\xd8\xff", "image/jpeg"),
    (b"\x89PNG\r\n\x1a\n", "image/png"),
)


def identity_of(user: dict[str, Any], avatar_url: str | None = None) -> dict[str, Any]:
    """The ``identity`` member of an event for the Telegram user ``user``; optional fields the user lacks
    are left out."""
    first_name = user.get("first_name") or ""
    last_name = user.get("last_name")
    identity: dict[str, Any] = {
        "channel_user_id": str(user["id"]),
        "display_name": f"{first_name} {last_name}" if last_name else first_name,
        "is_bot": bool(user.get("is_bot", False)),
        "first_name": first_name,
        "last_name": last_name,
        "username": user.get("username"),
        "locale": user.get("language_code"),
        "avatar_url": avatar_url,
        "raw_metadata": {"is_premium": bool(user.get("is_premium", False)), "telegram_id": user["id"]},
    }
    return {name: value for name, value in identity.items() if value is not None}


async def avatar_of(api: BotApi, token: str, user_id: int) -> str | None:
    """The latest profile photo of a user as a data URI, or ``None``.

    The image itself travels, never a Bot API file link: that link carries the token of the bot. No photo,
    a file over the limit, an image of another type or a refusal of the Bot API — the event goes without
    an avatar.
    """
    try:
        photos = await api.call(token, "getUserProfilePhotos", {"user_id": user_id, "offset": 0, "limit": 1})
        sizes = photos["photos"][0] if photos.get("photos") else []
        if not sizes:
            return None
        # The smallest size of the photo: it is the first one Telegram lists.
        file = await api.call(token, "getFile", {"file_id": sizes[0]["file_id"]})
        file_path = file.get("file_path")
        if not file_path or file.get("file_size", 0) > MAX_AVATAR_BYTES:
            return None
        image = await api.download(token, file_path, MAX_AVATAR_BYTES)
    except (PlatformError, KeyError, IndexError, TypeError, AttributeError) as exception:
        _log.warning("No avatar for user %s: %s.", fingerprint(str(user_id)), _reason_of(exception))
        return None

    if image is None:
        return None
    for signature, media_type in _IMAGE_SIGNATURES:
        if image.startswith(signature):
            return f"data:{media_type};base64,{base64.b64encode(image).decode('ascii')}"
    return None


def _reason_of(exception: Exception) -> str:
    # A platform error is safe for the log; an unexpected answer shape is named by its type only.
    return str(exception) if isinstance(exception, PlatformError) else f"unexpected answer ({type(exception).__name__})"
