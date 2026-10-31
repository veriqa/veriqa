// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Intake of the inbound WhatsApp messages of Twilio: one webhook path per sender, one decision per message.
//
// Each message either becomes an event of Veriqa or is dropped. The signature of Twilio is checked by the
// validateRequest of the official SDK with the Auth Token of the sender the path names, over the public URL
// Twilio posted to: behind a proxy or a tunnel the URL the adapter sees is not the one Twilio signed.

import twilio from "twilio";

import { describeTenant, fingerprint, logger, readBoundedBody, sendEmpty } from "../common.js";
import { Field } from "../core/wire.js";
import { InboundParameter, classify, identityOf, single } from "./inbound.js";

const log = logger("twilio.intake");

/** The header Twilio puts its signature of a webhook into. */
export const SIGNATURE_HEADER = "x-twilio-signature";

/** The prefix of the webhook-id of an event: the rest is the SID of the message it came from. */
export const EVENT_ID_PREFIX = "twilio-";

// The answer to a webhook: an empty TwiML document, so Twilio replies nothing to the user by itself.
const EMPTY_TWIML = '<?xml version="1.0" encoding="UTF-8"?><Response/>';

export class TwilioIntake {
  constructor(config, sink) {
    this.publicUrl = config.publicUrl;
    this.sink = sink;
    this.byPath = new Map(config.accounts.map((account) => [account.webhookPath, account]));
  }

  /** The sender whose webhook path ``pathname`` is, or ``null``. */
  accountAt(pathname) {
    return this.byPath.get(pathname) ?? null;
  }

  /** ``POST /twilio/{sender digits}``: one inbound message of one sender. */
  async webhook(request, response, account) {
    // A request without a signature has no effect, and its body is not read.
    const signature = request.headers[SIGNATURE_HEADER];
    if (typeof signature !== "string" || !signature) {
      sendEmpty(response, 403);
      return;
    }

    const body = await readBoundedBody(request);
    if (body === null) {
      sendEmpty(response, 413);
      return;
    }
    const parameters = formParameters(body);
    // Twilio signs the full URL it posted to, the query string included, with the Auth Token of the account
    // of the sender: another account's token does not validate.
    if (!twilio.validateRequest(account.authToken, signature, this.publicUrl + request.url, parameters)) {
      sendEmpty(response, 403);
      return;
    }

    // The message is taken once, and the answer to Twilio does not depend on what became of the event: an
    // event Veriqa did not take after the retries of the delivery is not handed back to Twilio.
    await this.take(account, parameters);
    response.writeHead(200, { "content-type": "text/xml; charset=utf-8" });
    response.end(EMPTY_TWIML);
  }

  /** Decides what one message is and acts on it. */
  async take(account, parameters) {
    const messageSid = single(parameters, InboundParameter.MESSAGE_SID);
    const { event, transactionId, phone } = classify(parameters);
    if (phone === null) {
      log.warning(`A message for ${describeTenant(account.tenant)} from a sender that is not a phone number in E.164 is dropped.`);
      return;
    }
    if (event === null) {
      log.debug(`Message ${messageSid ?? "without a SID"} for ${describeTenant(account.tenant)} is not an event of Veriqa.`);
      return;
    }
    if (!messageSid) {
      log.warning(`A ${event} message for ${describeTenant(account.tenant)} without a MessageSid is dropped.`);
      return;
    }

    log.info(`Message ${messageSid} from user ${fingerprint(phone)} is a ${event} event.`);
    // The identifier of the event follows from the message: a repeated delivery of it, by Twilio or by this
    // adapter, reaches Veriqa under the same webhook-id.
    await this.sink.publish({
      eventId: EVENT_ID_PREFIX + messageSid,
      name: event,
      fields: { [Field.TRANSACTION_ID]: transactionId, [Field.IDENTITY]: identityOf(parameters, phone) },
      fallbackTenant: account.tenant,
    });
  }
}

/**
 * The parameters of a form body as the SDK validates them: a name posted once maps to its value, a name
 * posted more than once to the list of its values.
 */
function formParameters(body) {
  // No prototype: a posted name such as __proto__ stays an ordinary parameter.
  const parameters = Object.create(null);
  for (const [name, value] of new URLSearchParams(body.toString("utf8"))) {
    const known = parameters[name];
    if (known === undefined) {
      parameters[name] = value;
    } else if (Array.isArray(known)) {
      known.push(value);
    } else {
      parameters[name] = [known, value];
    }
  }
  return parameters;
}
