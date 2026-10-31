# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
#
# Image of the stand of the CI gate: the platform stub and the own checks, Node alone without packages.
# Usage: docker build -t veriqa-sample-whatsapp-baileys-stand ci

FROM node:24-slim

WORKDIR /stand

COPY whatsapp.mjs stub.mjs checks.mjs ./

# The user the official image ships.
USER node

EXPOSE 9000

CMD ["node", "stub.mjs"]
