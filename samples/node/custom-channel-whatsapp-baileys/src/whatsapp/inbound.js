// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Which inbound WhatsApp messages are events of Veriqa, the identity of their sender, and their delivery.
//
// The adapter decides itself, before anything goes to Veriqa, so a message that is not an event of Veriqa
// never leaves the adapter. A text of a personal chat with the auth_ marker starts a sign-in, as in the
// built-in WhatsApp channel; the first vote of the recipient of a prompt poll answers it.

import { createHash } from "node:crypto";

import { fingerprint, logger } from "../common.js";
import { Event, Field, TRANSACTION_ID_LENGTH, isTransactionId } from "../core/wire.js";
import { ChatKind, MessageType } from "./port.js";

const log = logger("whatsapp.inbound");

/** The marker Veriqa puts into its deep links — the same as the built-in WhatsApp channel uses. */
export const AUTH_MARKER = "auth_";

/** The prefix of the webhook-id of an event: the rest is derived from the record and the message. */
export const EVENT_ID_PREFIX = "baileys-";

// Length of the hexadecimal digest after the prefix of a webhook-id: 128 bits of SHA-256.
const EVENT_ID_DIGEST_LENGTH = 32;

/**
 * The transaction of the ``auth_`` marker anywhere in a text, or ``null``: the prefilled message of a deep
 * link carries the marker on a line of its own, below the opening line. The identifier has a fixed length
 * and alphabet, which keeps the surrounding text from making a false match; overlapping occurrences such as
 * ``auth_auth_…`` are all tried.
 */
export function extractAuthTransactionId(text) {
  let from = 0;
  for (;;) {
    const index = text.indexOf(AUTH_MARKER, from);
    if (index < 0) {
      return null;
    }
    const candidate = text.slice(index + AUTH_MARKER.length, index + AUTH_MARKER.length + TRANSACTION_ID_LENGTH);
    if (isTransactionId(candidate)) {
      return candidate;
    }
    from = index + 1;
  }
}

/**
 * The ``identity`` member of an event: the number is the user, the profile name — or the number — is the name.
 * The built-in WhatsApp channel of Veriqa knows the user by the business-scoped user ID (BSUID) instead; this
 * sample keys the user by the number, so a message without one is dropped.
 */
export function identityOf(message) {
  const displayName = (message.pushName ?? "").trim() || message.sender;
  return {
    channel_user_id: message.sender,
    display_name: displayName,
    first_name: displayName,
    phone_number: message.sender,
    is_bot: false,
    raw_metadata: {
      whatsapp_message_id: message.messageId,
      message_type: message.type,
    },
  };
}

/**
 * The webhook-id of the event of a message: the same for every delivery of the same message to the same
 * record, so a repeat reaches Veriqa under the same idempotency key.
 */
export function eventIdOf(label, messageId) {
  const digest = createHash("sha256").update(JSON.stringify([label, messageId]), "utf8").digest("hex");
  return EVENT_ID_PREFIX + digest.slice(0, EVENT_ID_DIGEST_LENGTH);
}

/** Decides what each inbound message of the port is and posts the events of Veriqa to the sink. */
export class WhatsAppIntake {
  constructor(polls, sink) {
    this.polls = polls;
    this.sink = sink;
  }

  /** One inbound message of a record: an event of Veriqa, or nothing. */
  async take(message) {
    const decision = this.classify(message);
    if (decision === null) {
      return;
    }
    const { account } = message;
    log.info(`Message ${message.messageId} of record ${account.label} from user ${fingerprint(message.sender)} is a ${decision.event} event.`);
    let delivered = false;
    try {
      delivered = await this.sink.publish({
        eventId: eventIdOf(account.label, message.messageId),
        name: decision.event,
        fields: { [Field.TRANSACTION_ID]: decision.transactionId, [Field.IDENTITY]: identityOf(message) },
        fallbackTenant: account.tenant,
      });
    } finally {
      if (!delivered && decision.pollMessageId !== undefined) {
        // A vote whose event did not reach Veriqa answers nothing: the poll is open to the next vote again.
        this.polls.releaseVote(account.label, decision.pollMessageId, message.messageId);
      }
    }
  }

  /** The event of a message and its transaction, or ``null`` when the message is not an event of Veriqa. */
  classify(message) {
    const { account } = message;
    if (message.chat !== ChatKind.PERSONAL || message.fromMe) {
      log.debug(`Message ${message.messageId} of record ${account.label} is not from a personal chat of a user.`);
      return null;
    }
    if (message.type !== MessageType.TEXT && message.type !== MessageType.POLL_VOTE) {
      log.debug(`Message ${message.messageId} of record ${account.label} is neither a text nor a vote.`);
      return null;
    }
    if (message.sender === null) {
      // WhatsApp addressed the sender by a LID without a phone number: the user of Veriqa is the number.
      log.warning(`A message of record ${account.label} came without the phone number of its sender; it is dropped.`);
      return null;
    }
    return message.type === MessageType.TEXT ? this.classifyText(message) : this.classifyVote(message);
  }

  classifyText(message) {
    const transactionId = extractAuthTransactionId((message.text ?? "").trim());
    if (transactionId === null) {
      log.debug(`Message ${message.messageId} of record ${message.account.label} has no sign-in code.`);
      return null;
    }
    return { event: Event.AUTH_START, transactionId };
  }

  /**
   * The answer of a vote: the first vote of the recipient of a kept poll for one of its options whose event
   * reaches Veriqa. A vote taken back, a later vote — for the same option or another — and a vote of anyone
   * else answer nothing.
   */
  classifyVote(message) {
    const { account } = message;
    const poll = message.pollMessageId === null ? null : this.polls.pollOf(account.label, message.pollMessageId);
    if (poll === null) {
      log.warning(`A vote of record ${account.label} is on a poll that is not kept, or kept no longer; it is dropped.`);
      return null;
    }
    if (message.sender !== poll.recipient) {
      log.debug(`A vote on poll ${poll.messageId} of record ${account.label} is not from its recipient.`);
      return null;
    }
    if (message.selectedOptions === null) {
      log.warning(`A vote on poll ${poll.messageId} of record ${account.label} could not be read; it is dropped.`);
      return null;
    }
    const [option] = message.selectedOptions;
    const event = option === poll.confirmOption ? Event.AUTH_CONFIRM : option === poll.declineOption ? Event.AUTH_DECLINE : null;
    if (message.selectedOptions.length !== 1 || event === null) {
      log.debug(`A vote on poll ${poll.messageId} of record ${account.label} selects no single option of it.`);
      return null;
    }
    if (!this.polls.claimVote(account.label, poll.messageId, message.messageId)) {
      log.info(`Poll ${poll.messageId} of record ${account.label} is answered already; a later vote is dropped.`);
      return null;
    }
    return { event, transactionId: poll.transactionId, pollMessageId: poll.messageId };
  }
}
