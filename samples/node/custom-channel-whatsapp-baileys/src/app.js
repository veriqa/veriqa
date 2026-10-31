// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Composition of the adapter: the side of the core and the side of the platform joined in one HTTP server.

import { mkdirSync } from "node:fs";
import { createServer } from "node:http";

import { ConfigError, describeTenant, logger, sendEmpty } from "./common.js";
import { CallInput } from "./core/calls.js";
import { CoreEnv } from "./core/config.js";
import { EventDelivery } from "./core/events.js";
import { IdempotentCalls } from "./core/idempotency.js";
import { TransportSignature } from "./core/signature.js";
import { AdapterStore } from "./core/store.js";
import { PlatformMode, WhatsAppEnv } from "./whatsapp/config.js";
import { WhatsAppIntake } from "./whatsapp/inbound.js";
import { WhatsAppPlatform } from "./whatsapp/platform.js";
import { PollStore } from "./whatsapp/polls.js";
import { BaileysPort } from "./whatsapp/port/baileys.js";
import { StubPort } from "./whatsapp/port/stub.js";

const log = logger("app");

/** Path of the input of the calls of Veriqa: the base URL of the registration points at the root. */
export const CALL_PATH = "/";

// Access to the directory of the session states: the owner alone — they hold the keys of the sessions.
const SESSIONS_DIR_MODE = 0o700;

/**
 * The adapter: one server with the input of the calls of Veriqa and, in the ``stub`` mode alone, the service
 * path the platform stub posts the messages of the users to. Every request is served on its own, so a call
 * of Veriqa is taken while an event of this adapter is still on its way to Veriqa. A data or sessions
 * directory the adapter cannot use throws a ``ConfigError``.
 */
export function createAdapter(core, whatsAppConfig) {
  const store = openStore(core);
  const polls = new PollStore(store.database, core.idempotencyTtlSeconds);
  const port = openPort(whatsAppConfig, polls);
  const signature = new TransportSignature(core.transportSecrets);
  const platform = new WhatsAppPlatform(whatsAppConfig, port, polls);
  const delivery = new EventDelivery(core.veriqaUrl, core.channelType, signature, store);
  const calls = new CallInput(core.channelType, signature, platform, new IdempotentCalls(store), store);
  const intake = new WhatsAppIntake(polls, delivery);

  const route = (request, response) => {
    const pathname = request.url.split("?", 1)[0];
    if (pathname === CALL_PATH) {
      return request.method === "POST" ? calls.handle(request, response) : sendEmpty(response, 405);
    }
    if (port.servicePath !== undefined && pathname === port.servicePath) {
      return port.serve(request, response);
    }
    return sendEmpty(response, 404);
  };

  // Requests whose handling has not ended yet, and what to call when the last of them ends while stopping.
  let inProgress = 0;
  let onIdle = null;

  const server = createServer((request, response) => {
    inProgress += 1;
    Promise.resolve()
      .then(() => route(request, response))
      .catch((error) => {
        // Only the type of the failure is logged: its text may carry a body or an address.
        log.error(`A request failed: ${error?.name ?? typeof error}.`);
        if (response.headersSent) {
          response.destroy();
        } else {
          sendEmpty(response, 500);
        }
      })
      .finally(() => {
        inProgress -= 1;
        if (inProgress === 0) {
          onIdle?.();
        }
      });
  });

  log.info(
    `Adapter for channel ${core.channelType}, platform ${whatsAppConfig.mode}: ${whatsAppConfig.accounts.length} `
      + `WhatsApp record(s), for ${whatsAppConfig.accounts.map((account) => describeTenant(account.tenant)).join(", ")}.`,
  );
  let stopped = null;
  return {
    server,
    /** Starts the sessions of the platform; their inbound messages go to the intake. */
    start() {
      return port.start((message) => intake.take(message));
    },
    /**
     * Ends the sessions of the platform and the handling of their messages, stops taking connections, waits
     * for the requests in progress to end, then drops the connections left and closes the store: no handler
     * ever finds the store closed. Repeated calls share the one stop.
     */
    close() {
      stopped ??= port.stop()
        .then(() => new Promise((resolve) => {
          onIdle = resolve;
          server.close();
          if (inProgress === 0) {
            resolve();
          }
        }))
        .then(() => {
          server.closeAllConnections();
          store.close();
        });
      return stopped;
    },
  };
}

/** The store in the data directory; a directory it cannot be opened in stops the start. */
function openStore(core) {
  try {
    return new AdapterStore(core.dataDir, core.idempotencyTtlSeconds);
  } catch (error) {
    // Only the code of the failure is given: its text carries the path.
    throw new ConfigError(`${CoreEnv.DATA_DIR} names a directory the store cannot be opened in (${error?.code ?? error?.name})`);
  }
}

/** The port of the mode of the configuration; a sessions directory it cannot make stops the start. */
function openPort(whatsAppConfig, polls) {
  if (whatsAppConfig.mode === PlatformMode.STUB) {
    return new StubPort(whatsAppConfig);
  }
  try {
    mkdirSync(whatsAppConfig.sessionsDir, { recursive: true, mode: SESSIONS_DIR_MODE });
  } catch (error) {
    throw new ConfigError(`${WhatsAppEnv.SESSIONS_DIR} names a directory the sessions cannot be kept in (${error?.code ?? error?.name})`);
  }
  return new BaileysPort(whatsAppConfig, polls);
}
