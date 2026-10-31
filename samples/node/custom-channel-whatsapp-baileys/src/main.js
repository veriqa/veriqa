// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Entry point: node src/main.js. Reads the settings from the environment and serves the adapter.

import { createAdapter } from "./app.js";
import { ConfigError, logger, setLogLevel } from "./common.js";
import { CoreEnv, coreConfigFromEnv } from "./core/config.js";
import { whatsAppConfigFromEnv } from "./whatsapp/config.js";

// Every interface of the adapter listens here; the network around it decides who reaches it.
const LISTEN_HOST = "0.0.0.0";

// Exit code of a start refused for its configuration: a variable, the port or the data directory.
const EXIT_CONFIGURATION = 2;

// Exit code of a stop that gave up on the requests in progress.
const EXIT_STOP_FORCED = 1;

// How long a stop waits for the requests in progress: below the 10 seconds a container runtime such as
// Docker gives a container before it kills it, so the adapter ends on its own.
const STOP_GRACE_MS = 8_000;

const log = logger("main");

/** Ends a start refused for its configuration with the message of the refusal; other failures are thrown. */
function refuseStart(error) {
  if (!(error instanceof ConfigError)) {
    throw error;
  }
  process.stderr.write(`Configuration error: ${error.message}.\n`);
  process.exit(EXIT_CONFIGURATION);
}

let core;
let adapter;
try {
  core = coreConfigFromEnv(process.env);
  const whatsAppConfig = whatsAppConfigFromEnv(process.env, core.dataDir);
  setLogLevel(core.logLevel);
  adapter = createAdapter(core, whatsAppConfig);
} catch (error) {
  refuseStart(error);
}

// A port the adapter cannot listen on — taken, or not allowed to the user — stops the start like any other
// setting it cannot use.
const refuseListen = (error) =>
  refuseStart(new ConfigError(`${CoreEnv.PORT} names a port the adapter cannot listen on (${error?.code ?? error?.name})`));
adapter.server.once("error", refuseListen);
adapter.server.listen(core.port, LISTEN_HOST, () => {
  adapter.server.off("error", refuseListen);
  log.info(`Listening on port ${core.port}.`);
  // The sessions start once the calls of Veriqa can be taken: an inbound message leads to one at once.
  adapter.start().catch((error) => log.error(`The sessions did not start: ${error?.name ?? typeof error}.`));
});

// The adapter runs as the first process of its container: it stops on the signals of the runtime itself,
// ending the sessions and finishing the requests in progress first. A second signal of the other kind joins
// the same stop.
for (const signal of ["SIGTERM", "SIGINT"]) {
  process.once(signal, () => {
    log.info(`Stopping on ${signal}: finishing the requests in progress.`);
    setTimeout(() => {
      log.warning(`The requests in progress did not end within ${STOP_GRACE_MS / 1000} seconds; stopping without them.`);
      process.exit(EXIT_STOP_FORCED);
    }, STOP_GRACE_MS).unref();
    adapter.close().then(() => {
      log.info("Stopped.");
      process.exit(0);
    });
  });
}
