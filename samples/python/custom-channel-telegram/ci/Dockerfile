# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
#
# Image of the stand of the CI gate: the platform stub and the own checks, standard library only.
# Usage: docker build -t veriqa-sample-telegram-stand ci

FROM python:3.13-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /stand

COPY stub.py checks.py ./

RUN useradd --system --uid 10001 --no-create-home --shell /usr/sbin/nologin stand
USER stand

EXPOSE 9000

CMD ["python", "stub.py"]
