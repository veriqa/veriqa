# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Reference external channel adapter of Veriqa for Telegram.

The package has two sides joined by one seam, ``adapter.core.seam``:

- ``adapter.core`` is the side of the core. It checks and makes the transport signature, reads the call
  envelopes, keeps the idempotency keys and the binding of a transaction to its tenant, and delivers the
  events to Veriqa. It knows nothing of Telegram.
- ``adapter.telegram`` is the side of the platform. It talks to the Bot API with the credentials of a
  tenant, takes the updates of Telegram in and decides which of them are events of Veriqa.

An author of an adapter for another channel replaces the second side and keeps the first.
"""
