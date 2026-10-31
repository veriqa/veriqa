// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Composition of the adapter: the side of the core and the side of the platform joined in one HTTP server.

import { createServer } from "node:http";

import { ConfigError, describeTenant, logger, sendEmpty } from "./common.js";
import { CallInput } from "./core/calls.js";
import { CoreEnv } from "./core/config.js";
import { EventDelivery } from "./core/events.js";
import { IdempotentCalls } from "./core/idempotency.js";
import { TransportSignature } from "./core/signature.js";
import { AdapterStore } from "./core/store.js";
import { TwilioIntake } from "./twilio/intake.js";
import { TwilioPlatform } from "./twilio/platform.js";
import { TwilioRest } from "./twilio/rest.js";

const log = logger("app");

/** Path of the input of the calls of Veriqa: the base URL of the registration points at the root. */
export const CALL_PATH = "/";

/**
 * The adapter: one server with the input of the calls of Veriqa and the inbound webhooks of the senders.
 * Every request is served on its own, so a call of Veriqa is taken while an event of this adapter is still
 * on its way to Veriqa. A data directory the store cannot be opened in throws a ``ConfigError``.
 */
export function createAdapter(core, twilioConfig) {
  const store = openStore(core);
  const signature = new TransportSignature(core.transportSecrets);
  const platform = new TwilioPlatform(twilioConfig, new TwilioRest(twilioConfig.apiUrl));
  const delivery = new EventDelivery(core.veriqaUrl, core.channelType, signature, store);
  const calls = new CallInput(core.channelType, signature, platform, new IdempotentCalls(store), store);
  const intake = new TwilioIntake(twilioConfig, delivery);

  const route = (request, response) => {
    const pathname = request.url.split("?", 1)[0];
    if (pathname === CALL_PATH) {
      return request.method === "POST" ? calls.handle(request, response) : sendEmpty(response, 405);
    }
    const account = intake.accountAt(pathname);
    if (account !== null) {
      return request.method === "POST" ? intake.webhook(request, response, account) : sendEmpty(response, 405);
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
    `Adapter for channel ${core.channelType}: ${twilioConfig.accounts.length} sender(s), for `
      + `${twilioConfig.accounts.map((account) => describeTenant(account.tenant)).join(", ")}.`,
  );
  let stopped = null;
  return {
    server,
    /**
     * Stops taking connections, waits for the requests in progress to end, then drops the connections left
     * and closes the store: no handler ever finds the store closed. Repeated calls share the one stop.
     */
    close() {
      stopped ??= new Promise((resolve) => {
        onIdle = resolve;
        server.close();
        if (inProgress === 0) {
          resolve();
        }
      }).then(() => {
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
