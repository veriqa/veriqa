# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""The own setting of this adapter, carried by the opaque ``adapter_settings`` string of every call.

Veriqa forwards the string verbatim and never reads it; the operator sets it for the channel and, when a
tenant needs another value, for that tenant. This adapter reads a JSON object with one key:

    {"disable_notification": true}

``disable_notification`` goes to the Bot API with every message the adapter sends: Telegram delivers the
message silently. The string is secret-grade for Veriqa, so its value never reaches the log.
"""

from __future__ import annotations

import json
import logging

_log = logging.getLogger(__name__)

DISABLE_NOTIFICATION = "disable_notification"


def disable_notification(settings: str | None) -> bool:
    """The ``disable_notification`` of the settings string; ``False`` with a warning when it cannot be read."""
    if settings is None:
        _log.warning("adapter_settings is not set; %s defaults to false.", DISABLE_NOTIFICATION)
        return False
    try:
        parsed = json.loads(settings)
    except ValueError:
        _log.warning("adapter_settings is not JSON; %s defaults to false.", DISABLE_NOTIFICATION)
        return False
    value = parsed.get(DISABLE_NOTIFICATION) if isinstance(parsed, dict) else None
    if not isinstance(value, bool):
        _log.warning("adapter_settings has no boolean %s; it defaults to false.", DISABLE_NOTIFICATION)
        return False
    return value
