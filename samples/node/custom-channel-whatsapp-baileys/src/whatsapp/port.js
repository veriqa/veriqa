// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The port of the platform: the few things the adapter asks of WhatsApp, and the messages it gets back.
//
// Two implementations stand behind it, chosen by WHATSAPP_PLATFORM: ``baileys`` holds a WhatsApp Web session
// per record, ``stub`` talks HTTP to the platform stub of the stand. Everything above the port — the
// operations of the wire, the polls, the classification of the inbound messages — is the same in both, so
// the stand checks all of it but the library itself. The composition of the adapter picks the one to use.

/**
 * How long one operation of the platform may take, in milliseconds: below the operation timeout of the HTTP
 * channel of Veriqa (10 seconds by default), so a call answers inside it even when the platform hangs.
 */
export const PLATFORM_TIMEOUT_MS = 8_000;

/** The state of the session of one record, as health reports it. */
export const RecordState = Object.freeze({
  OK: "ok",
  NOT_PAIRED: "not paired",
  NOT_CONNECTED: "not connected",
});

/** Kinds of the chat of an inbound message. */
export const ChatKind = Object.freeze({
  PERSONAL: "personal",
  GROUP: "group",
  OTHER: "other",
});

/** Types of an inbound message, as the classification reads them. */
export const MessageType = Object.freeze({
  TEXT: "text",
  POLL_VOTE: "poll_vote",
  OTHER: "other",
});

/**
 * @typedef {object} PollRequest A poll of a prompt, its identifier made before it goes out.
 * @property {string} messageId
 * @property {string} question
 * @property {string[]} options The option of the confirmation first, the option of the refusal second.
 * @property {Buffer} secret 32 random bytes the votes on the poll are encrypted with.
 */

/**
 * @typedef {object} PlatformMessage One inbound message of a record, in the terms the classification reads.
 * @property {import("./config.js").WhatsAppAccount} account The record whose number it came to.
 * @property {string} messageId The identifier of the message on WhatsApp.
 * @property {string} chat One of ``ChatKind``.
 * @property {boolean} fromMe Whether the number of the record wrote it itself.
 * @property {string|null} sender The number of the sender in E.164, or ``null`` when WhatsApp gave none.
 * @property {string|null} pushName The name the sender set in their profile.
 * @property {string} type One of ``MessageType``.
 * @property {string|null} text The text of a text message.
 * @property {string|null} pollMessageId The poll a vote is on.
 * @property {string[]|null} selectedOptions The options a vote selects; empty when the vote is taken back,
 *   ``null`` when the vote could not be read.
 */

/**
 * @typedef {object} Port
 * @property {(onMessage: (message: PlatformMessage) => Promise<void>) => Promise<void>} start Starts the
 *   sessions; every inbound message of a record is handed to ``onMessage``.
 * @property {() => Promise<void>} stop Ends the sessions; no message is handed over after it.
 * @property {(account: object) => string} newMessageId A new identifier of a message of the record.
 * @property {(account: object, recipient: string, text: string) => Promise<string>} sendText Sends a text;
 *   the identifier of the message.
 * @property {(account: object, recipient: string, poll: PollRequest) => Promise<void>} sendPoll
 * @property {(account: object) => Promise<string>} stateOf One of ``RecordState``.
 * @property {string} [servicePath] The path of the service input of the port, when it has one.
 * @property {(request: object, response: object) => Promise<void>} [serve] Handles a request on that path.
 */
