# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
#
# Reference external WhatsApp adapter through Twilio for the HTTP channel of Veriqa.
# Usage: docker build -t veriqa-sample-whatsapp-twilio-adapter .
#        docker run --env-file .env -v adapter-data:/data veriqa-sample-whatsapp-twilio-adapter

FROM node:24-slim

ENV NODE_ENV=production

WORKDIR /app

# The dependencies first: this layer is reused as long as package.json does not change. The sample ships
# no lock file — package.json pins the direct dependencies to exact versions, and npm resolves the rest at
# build time — so npm install, not npm ci, and no lock file is written into the image either.
COPY package.json ./
RUN npm install --omit=dev --no-package-lock --no-audit --no-fund \
    && npm cache clean --force

COPY src ./src

# The data directory holds the SQLite file of the idempotency keys and the links of the transactions; it
# belongs to the non-root user the official image ships, so a named volume mounted over it inherits the owner.
RUN mkdir /data && chown node /data
USER node
VOLUME /data

# The calls of Veriqa (POST /) and the inbound webhooks of the senders (POST /twilio/<sender digits>).
EXPOSE 8000

CMD ["node", "src/main.js"]
