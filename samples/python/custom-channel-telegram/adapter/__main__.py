# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""Entry point: ``python -m adapter``. Reads the settings from the environment and serves the adapter."""

from __future__ import annotations

import errno
import logging
import os
import socket
import sys

import uvicorn

from adapter.app import create_app
from adapter.common import ConfigError
from adapter.core.config import CoreConfig, CoreEnv
from adapter.telegram.config import TelegramConfig

# Every interface of the adapter listens here; the network around it decides who reaches it.
LISTEN_HOST = "0.0.0.0"

# Loggers of the HTTP client log every request address at INFO, and an address of the Bot API carries the
# token of a bot: they are held at WARNING.
_QUIET_LOGGERS = ("httpx", "httpcore")

# Exit code of a start refused for its configuration: a variable, the port or the data directory.
EXIT_CONFIGURATION = 2


def _refuse_start(error: ConfigError) -> None:
    """Ends a start refused for its configuration with the message of the refusal."""
    print(f"Configuration error: {error}.", file=sys.stderr)
    sys.exit(EXIT_CONFIGURATION)


def _listen(port: int) -> socket.socket:
    """The listening socket, bound before the server starts: a port the adapter cannot listen on — taken, or
    not allowed to the user — stops the start like any other setting it cannot use."""
    try:
        return socket.create_server((LISTEN_HOST, port))
    except OSError as error:
        code = errno.errorcode.get(error.errno, type(error).__name__)
        raise ConfigError(f"{CoreEnv.PORT} names a port the adapter cannot listen on ({code})") from None


def main() -> None:
    try:
        core = CoreConfig.from_env(os.environ)
        telegram = TelegramConfig.from_env(os.environ)
    except ConfigError as error:
        _refuse_start(error)

    logging.basicConfig(level=core.log_level, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    for name in _QUIET_LOGGERS:
        logging.getLogger(name).setLevel(logging.WARNING)

    try:
        app = create_app(core, telegram)
        listener = _listen(core.port)
    except ConfigError as error:
        _refuse_start(error)

    # The access log is off: the adapter logs its own decisions, and the addresses it serves carry nothing
    # an operator needs beyond them. The server serves the socket bound above.
    server = uvicorn.Server(uvicorn.Config(app, access_log=False, log_config=None))
    server.run(sockets=[listener])


if __name__ == "__main__":
    main()
