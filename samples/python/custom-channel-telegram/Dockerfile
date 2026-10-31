# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
#
# Reference external Telegram adapter of the HTTP channel of Veriqa.
# Usage: docker build -t veriqa-sample-telegram-adapter .
#        docker run --env-file .env -v adapter-data:/data veriqa-sample-telegram-adapter

FROM python:3.13-slim

# No .pyc files next to the sources, and the log reaches the container log line by line.
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

# The dependencies first: this layer is reused as long as requirements.txt does not change.
COPY requirements.txt .
RUN pip install --no-cache-dir --requirement requirements.txt

COPY adapter ./adapter

# Non-root user. The data directory holds the SQLite file of the idempotency keys and the links of the
# transactions; it belongs to that user, so a named volume mounted over it inherits the owner.
RUN useradd --system --uid 10001 --no-create-home --shell /usr/sbin/nologin adapter \
    && mkdir /data \
    && chown adapter /data
USER adapter
VOLUME /data

# The calls of Veriqa (POST /) and, in the webhook mode, the webhooks of the bots (POST /telegram/<bot id>).
EXPOSE 8000

CMD ["python", "-m", "adapter"]
