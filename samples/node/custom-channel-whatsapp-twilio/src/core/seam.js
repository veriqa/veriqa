// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The seam between the side of the core and the side of the platform.
//
// The side of the core calls the platform through an object of the shape ``Platform`` below; the platform
// hands the events it recognised back through ``EventSink``. Everything that crosses the seam is in terms
// of the wire — chat and message identifiers, texts, event names — never in terms of a platform API. An
// adapter for another channel implements ``Platform`` and feeds an ``EventSink``; the side of the core
// stays as it is.

/**
 * The platform refused an operation or could not be reached.
 *
 * The message is safe for the log: it never carries a credential, a request address or a user's data.
 */
export class PlatformError extends Error {}

/**
 * The platform cannot carry the operation out for this user, and no repeat will change that: the answer of
 * the adapter is ``channel_cannot_continue``, which ends the transaction when it answers ``send-prompt``.
 */
export class PlatformCannotContinueError extends PlatformError {}

/**
 * @typedef {object} MessageRef Coordinates of a message on the platform.
 * @property {string} chatId
 * @property {string} messageId
 */

/**
 * @typedef {object} PromptRequest A confirmation prompt: the text Veriqa rendered and the captions of its
 *   two buttons.
 * @property {string} transactionId
 * @property {string} channelUserId
 * @property {string} text
 * @property {string} confirmLabel
 * @property {string} declineLabel
 */

/**
 * @typedef {object} OutcomeRequest The outcome of a transaction to show to a user.
 * @property {string} channelUserId
 * @property {string} statusText
 * @property {string|null} inboundToken
 * @property {MessageRef|null} promptMessage
 * @property {boolean} replacePrompt
 */

/**
 * @typedef {object} HealthReport Whether the platform answers for every configured credential.
 * @property {boolean} healthy
 * @property {string|null} details What failed, or ``null``.
 */

/**
 * @typedef {object} Platform Operations of the platform the calls of the core come down to.
 *
 * ``account`` is what ``accountFor`` returned for the tenant of the call; the side of the core never looks
 * into it. ``settings`` is the opaque ``adapter_settings`` string of the call, read by the platform alone.
 * A refusal is a ``PlatformError``; one the platform declares final for the user is a
 * ``PlatformCannotContinueError``.
 *
 * @property {(tenant: string|null) => object|null} accountFor The credentials the calls of a tenant use,
 *   or ``null`` when the tenant has none.
 * @property {(account: object, settings: string|null, request: PromptRequest) => Promise<MessageRef>} sendPrompt
 * @property {(account: object, settings: string|null, channelUserId: string, text: string, kind: string) => Promise<MessageRef>} sendMessage
 * @property {(account: object, settings: string|null, request: OutcomeRequest) => Promise<void>} reportOutcome
 * @property {(account: object, settings: string|null, transactionId: string, renderedText: string|null) => Promise<string>} deepLink
 *   ``renderedText`` is the text Veriqa rendered for the link, or ``null`` when the call carries none.
 * @property {() => Promise<HealthReport>} health Probes every configured credential.
 */

/**
 * @typedef {object} InboundEvent An event of Veriqa the platform recognised in its traffic.
 *
 * ``eventId`` becomes the ``webhook-id`` of the delivery: unique per event and the same on every delivery
 * of it. ``fields`` are the members of the event envelope besides the version, the name and the version of
 * the adapter. ``fallbackTenant`` is the route of an event whose transaction has no known tenant — the
 * tenant of the credential the platform traffic came in with.
 *
 * @property {string} eventId
 * @property {string} name
 * @property {object} fields
 * @property {string|null} fallbackTenant
 */

/**
 * @typedef {object} EventSink Where the platform hands the events of Veriqa to.
 * @property {(event: InboundEvent) => Promise<boolean>} publish Delivers an event to Veriqa; ``true``
 *   when Veriqa took it.
 */
