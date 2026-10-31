# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
#
# Reference external WhatsApp adapter through Baileys for the HTTP channel of Veriqa. Baileys is an
# unofficial WhatsApp Web client, and the image holds GPL-3.0 code (libsignal, a dependency of
# Baileys) — see the README before running or distributing it.
# Usage: docker build -t veriqa-sample-whatsapp-baileys-adapter .
#        docker run --env-file .env -v adapter-data:/data veriqa-sample-whatsapp-baileys-adapter

FROM node:24-slim

ENV NODE_ENV=production

WORKDIR /app

# The dependencies first: this layer is reused as long as package.json does not change. The sample ships
# no lock file — package.json pins the direct dependencies to exact versions, and npm resolves the rest at
# build time — so npm install, not npm ci, and no lock file is written into the image either. The peer
# dependencies of Baileys are left out: they process media (images, audio, link previews), which this
# adapter never sends, and Baileys loads them only when it does.
COPY package.json ./
RUN npm install --omit=dev --omit=peer --no-package-lock --no-audit --no-fund \
    && npm cache clean --force

COPY src ./src

# The data directory holds the SQLite file of the adapter and, under sessions/, the WhatsApp Web session
# state of every record — the keys of the linked device. It belongs to the non-root user the official
# image ships, so a named volume mounted over it inherits the owner.
RUN mkdir /data && chown node /data
USER node
VOLUME /data

# The calls of Veriqa (POST /). WhatsApp is reached over the outbound sessions of the adapter: nothing comes in.
EXPOSE 8000

CMD ["node", "src/main.js"]
