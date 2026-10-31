// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Delivery of the events of Veriqa to the core: signed, on the route of the tenant of the transaction,
// retried on a transport failure under the same webhook-id.

import { describeTenant, logger, sleep } from "../common.js";
import { ADAPTER_VERSION, Field, WIRE_VERSION } from "./wire.js";

const log = logger("core.events");

// Attempts of one delivery, the time one attempt may take and the pauses between them. An attempt waits for
// Veriqa to process the event, and processing auth_start includes the counter send-prompt Veriqa makes to
// this adapter within its own retry budget of 20 seconds — hence the timeout above that budget.
const EVENT_ATTEMPTS = 3;
const EVENT_ATTEMPT_TIMEOUT_SECONDS = 30;
const EVENT_RETRY_PAUSES_SECONDS = [1, 2];

/**
 * How long after the first delivery the adapter may deliver the same event again: the lower bound of the
 * retention of the idempotency keys Veriqa derives from its events.
 */
export const EVENT_REDELIVERY_WINDOW_SECONDS =
  EVENT_ATTEMPTS * EVENT_ATTEMPT_TIMEOUT_SECONDS + EVENT_RETRY_PAUSES_SECONDS.reduce((sum, pause) => sum + pause, 0);

// The status of Veriqa that is repeated besides 5xx, and the status that means a configuration error.
const TOO_MANY_REQUESTS = 429;
const FORBIDDEN = 403;

const JSON_CONTENT_TYPE = "application/json";

/** The path of the webhook route of Veriqa for a channel and a tenant; ``null`` is the implicit tenant. */
export function webhookPath(channelType, tenant) {
  const path = `/api/channels/${encodeURIComponent(channelType)}/webhook`;
  return tenant === null ? path : `${path}/${encodeURIComponent(tenant)}`;
}

/**
 * Posts the events the platform recognised to Veriqa: the ``EventSink`` of the seam.
 *
 * The route is the tenant of the transaction the event names, as the first deep-link or send-prompt call of
 * that transaction recorded it; an event with no such transaction goes on the route of the tenant of the
 * credential the platform traffic came in with. Nothing is locked while a delivery is in progress: Veriqa
 * answers auth_start only after it has called this adapter back with send-prompt.
 */
export class EventDelivery {
  constructor(veriqaUrl, channelType, signature, store) {
    this.veriqaUrl = veriqaUrl;
    this.channelType = channelType;
    this.signature = signature;
    this.store = store;
  }

  async publish(event) {
    const tenant = this.routeTenantOf(event);
    const url = this.veriqaUrl + webhookPath(this.channelType, tenant);
    const body = JSON.stringify({
      [Field.VERSION]: WIRE_VERSION,
      [Field.EVENT]: event.name,
      [Field.ADAPTER_VERSION]: ADAPTER_VERSION,
      ...event.fields,
    });

    for (let attempt = 1; attempt <= EVENT_ATTEMPTS; attempt += 1) {
      // Every attempt is signed anew: the timestamp moves, the webhook-id stays.
      const headers = { ...this.signature.sign(event.eventId, body), "content-type": JSON_CONTENT_TYPE };
      let response;
      try {
        response = await fetch(url, {
          method: "POST",
          headers,
          body,
          redirect: "manual",
          signal: AbortSignal.timeout(EVENT_ATTEMPT_TIMEOUT_SECONDS * 1000),
        });
      } catch (error) {
        log.warning(`Delivery of a ${event.name} event, attempt ${attempt}: ${error.name}.`);
        response = null;
      }

      if (response !== null) {
        // The body of the answer of Veriqa is not read, and not logged.
        await response.body?.cancel().catch(() => {});
        if (response.ok) {
          log.info(`A ${event.name} event was delivered to the route of ${describeTenant(tenant)}.`);
          return true;
        }
        if (response.status === FORBIDDEN) {
          // The transport secret or the owner of the registration does not match: a configuration error
          // that a repeat does not fix.
          log.error(`Veriqa refused a ${event.name} event with 403: check the transport secret and the channel type.`);
          return false;
        }
        if (response.status !== TOO_MANY_REQUESTS && response.status < 500) {
          log.error(`Veriqa answered a ${event.name} event with ${response.status}; the event is not repeated.`);
          return false;
        }
        log.warning(`Delivery of a ${event.name} event, attempt ${attempt}: status ${response.status}.`);
      }

      if (attempt < EVENT_ATTEMPTS) {
        await sleep(EVENT_RETRY_PAUSES_SECONDS[attempt - 1] * 1000);
      }
    }

    log.error(`A ${event.name} event was not delivered after ${EVENT_ATTEMPTS} attempts.`);
    return false;
  }

  routeTenantOf(event) {
    const transactionId = event.fields[Field.TRANSACTION_ID];
    const route = typeof transactionId === "string" ? this.store.routeOf(transactionId) : null;
    return route !== null ? route.tenant : event.fallbackTenant;
  }
}
