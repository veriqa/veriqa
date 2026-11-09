// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Contracts.Audit;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;

namespace Veriqa.Core.AuditTrail.Receiver;

/// <summary>
/// Maps a transaction bus event onto an audit record of the fixed schema.
/// </summary>
/// <remarks>
/// The set of audited events is an explicit map, not reflection over the event hierarchy: a new
/// bus event must be added to the audit vocabulary deliberately, and one that is not listed here
/// simply produces no record. Intermediate channel statuses are outside the audited set — they
/// change no state and are not security-significant.
/// <para>
/// Every attribute of a record comes from the event itself — either from its own fields or from the
/// attribution the publisher attached to it while the transaction was still there. The mapper never
/// reads the transaction store: delivery is asynchronous, the transaction may already be gone, and
/// a record that reads it would carry a different attribution depending on who won that race.
/// An attribute the publisher did not attach yields the closing rules — the system actor and an
/// empty attribute — deterministically: a refusal of the interaction surface carries the tenant
/// at most, and every other attribute of its record falls back to them; an admissibility refusal
/// of the transaction service and a completion-behaviour clamp carry the whole attribution.
/// </para>
/// </remarks>
internal static class AuditRecordMapper
{
    /// <summary>
    /// Separator between the channel type and the masked identifier in the actor value.
    /// </summary>
    private const string ActorSeparator = ":";

    /// <summary>
    /// Resolves the stable action code of an event.
    /// </summary>
    /// <param name="transactionEvent">Bus event.</param>
    /// <returns>Action code, or null when the event is not audited.</returns>
    internal static string? MapAction(TransactionEvent transactionEvent) => transactionEvent switch
    {
        TransactionCreatedEvent => AuditActionCodes.TransactionCreated,
        TransactionActivatedEvent => AuditActionCodes.TransactionActivated,
        TransactionChannelIdentityAttachedEvent => AuditActionCodes.TransactionChannelIdentityAttached,
        TransactionConfirmedEvent => AuditActionCodes.TransactionConfirmed,
        TransactionCompletedEvent => AuditActionCodes.TransactionCompleted,
        TransactionFailedEvent => AuditActionCodes.TransactionFailed,
        TransactionExpiredEvent => AuditActionCodes.TransactionExpired,

        // A channel security event carries the code its own channel declared; the core introduces
        // no second name for it.
        TransactionChannelAuditEvent channelAudit => channelAudit.EventCode,

        _ => null
    };

    /// <summary>
    /// Builds the audit record for an event.
    /// </summary>
    /// <param name="transactionEvent">Bus event.</param>
    /// <param name="action">Action code resolved by <see cref="MapAction"/>.</param>
    /// <param name="includeConfirmationParameters">
    /// Whether the record carries the parameters of the confirmed operation.
    /// </param>
    /// <param name="channelInboundVerification">
    /// Token of the verification level the configuration declares for the channel of the record; null
    /// when the record belongs to no channel or no level declared a value.
    /// </param>
    /// <returns>Audit record.</returns>
    internal static AuditRecord Map(
        TransactionEvent transactionEvent,
        string action,
        bool includeConfirmationParameters,
        string? channelInboundVerification) =>
        new()
        {
            // The moment of the event, not of its handling: with asynchronous delivery the two differ.
            Timestamp = transactionEvent.OccurredAt,
            Actor = ResolveActor(transactionEvent),
            Action = action,
            Target = transactionEvent.TransactionId.ToString(),
            Result = ResolveResult(transactionEvent),
            Metadata = BuildMetadata(
                transactionEvent,
                includeConfirmationParameters,
                channelInboundVerification)
        };

    /// <summary>
    /// Resolves who acted: the application for the transitions it drives, the masked channel
    /// identity for the ones the user drives, and the system agent for everything else — including
    /// the case where the initiator is not recoverable. A failure is split by its reason code
    /// (see <see cref="ResolveFailureActor"/>): it carries both user-driven and system ones.
    /// </summary>
    /// <param name="transactionEvent">Bus event.</param>
    /// <returns>Actor value of the record.</returns>
    private static string ResolveActor(TransactionEvent transactionEvent) =>
        transactionEvent switch
        {
            // Creation and activation follow the application's authorization request.
            TransactionCreatedEvent or TransactionActivatedEvent =>
                transactionEvent.Context?.ClientId ?? AuditActors.System,

            // Actions the user performed in the channel.
            TransactionChannelIdentityAttachedEvent
                or TransactionConfirmedEvent
                or TransactionChannelAuditEvent =>
                ResolveChannelIdentityActor(transactionEvent),

            // A failure is not one class of actor: the reason states who ended the transaction.
            TransactionFailedEvent failed => ResolveFailureActor(failed),

            // Completion and expiry are transitions nobody initiated directly.
            _ => AuditActors.System
        };

    /// <summary>
    /// Resolves who ended a failed transaction: the user who declined it in the channel, the
    /// application that cancelled it, and the system agent for every other reason.
    /// </summary>
    /// <remarks>
    /// SPEC-011 L34 derives the actor from the attribution the event itself carries: a decline
    /// brings the masked identity only where the transaction already holds one, a cancellation
    /// comes over the application's API, and everything else is systemic. Attributing a decline is
    /// not a requirement of the journal — the fact and the reason code carry the record — so the
    /// fallback below records a systemic actor instead of inventing one.
    /// </remarks>
    /// <param name="failed">Failure event.</param>
    /// <returns>Actor value of the record.</returns>
    private static string ResolveFailureActor(TransactionFailedEvent failed) =>
        failed.ReasonCode switch
        {
            // The masked actor is available only for a decline the transaction already carries a
            // channel identity for — that is the refusal answered on the core web page. A refusal
            // answered inside the channel reaches the journal without an identity, and the fallback
            // inside records it as systemic rather than inventing an actor.
            TransactionErrorCodes.DeclinedByUser => ResolveChannelIdentityActor(failed),

            // Cancellation comes over the API of the application, exactly like creation.
            TransactionErrorCodes.CancelledByClient => failed.Context?.ClientId ?? AuditActors.System,

            _ => AuditActors.System
        };

    /// <summary>
    /// Resolves the outcome of an event.
    /// </summary>
    /// <remarks>
    /// A channel security event carries no outcome of its own, and the core is not entitled to
    /// interpret codes a channel declared — so this whole class of events is recorded as a failure
    /// with the channel's code as the reason.
    /// </remarks>
    /// <param name="transactionEvent">Bus event.</param>
    /// <returns>Outcome of the record.</returns>
    private static AuditResult ResolveResult(TransactionEvent transactionEvent) =>
        transactionEvent switch
        {
            TransactionFailedEvent failed => new AuditResult
            {
                Outcome = AuditOutcome.Failure,
                ReasonCode = failed.ReasonCode
            },

            TransactionExpiredEvent expired => new AuditResult
            {
                Outcome = AuditOutcome.Failure,
                ReasonCode = expired.ReasonCode
            },

            TransactionChannelAuditEvent channelAudit => new AuditResult
            {
                Outcome = AuditOutcome.Failure,
                ReasonCode = channelAudit.EventCode
            },

            _ => new AuditResult { Outcome = AuditOutcome.Success }
        };

    /// <summary>
    /// Builds the closed set of safe attributes of the record. Nothing beyond these attributes is
    /// carried over from the event.
    /// </summary>
    /// <param name="transactionEvent">Bus event.</param>
    /// <param name="includeConfirmationParameters">
    /// Whether the record carries the parameters of the confirmed operation.
    /// </param>
    /// <param name="channelInboundVerification">
    /// Token of the verification level the configuration declares for the channel of the record.
    /// </param>
    /// <returns>Record metadata; every attribute is null when it is unknown.</returns>
    private static AuditMetadata BuildMetadata(
        TransactionEvent transactionEvent,
        bool includeConfirmationParameters,
        string? channelInboundVerification) =>
        new()
        {
            // The creation event states the type itself, which keeps the attribute populated even
            // for a publisher that attaches no attribution.
            TransactionType = transactionEvent.Context?.TransactionType
                ?? (transactionEvent as TransactionCreatedEvent)?.Type,
            ChannelType = ResolveChannelType(transactionEvent),
            CorrelationId = transactionEvent.Context?.CorrelationId,

            // Owner of the event, taken from the attribution and from nowhere else — the transaction
            // it was captured from may already be gone. A blank value never reaches here: the
            // attribution normalizes it at the point of capture.
            TenantId = transactionEvent.Context?.TenantId,
            ClientId = transactionEvent.Context?.ClientId,

            // The language and the time zone the question was actually put in. They come from the
            // attribution for the sharpest form of the same reason: the transaction is swept minutes
            // after it completes, and it is the only carrier of these two.
            UiLocale = transactionEvent.Context?.UiLocale,
            UiTimeZone = transactionEvent.Context?.UiTimeZone,

            // Carried through verbatim: the composition of these details is defined by the
            // channel's own audit rules, and parsing them here would be a second name dictionary.
            ChannelDetails = (transactionEvent as TransactionChannelAuditEvent)?.Details,

            // Resolved by the receiver, next to the audit mode it already resolves — an attribute of
            // its own and never a fragment appended to ChannelDetails, whose composition belongs to
            // the channel and not to the core.
            ChannelInboundVerification = channelInboundVerification,

            // The one attribute the deployment has to ask for: subject data of the relying party.
            ConfirmationParameters = includeConfirmationParameters
                ? MapConfirmationParameters(transactionEvent)
                : null
        };

    /// <summary>
    /// Copies the parameters of the confirmed operation out of the attribution the event carries.
    /// </summary>
    /// <remarks>
    /// A copy into the record and not a reference to the transaction: the transaction is swept
    /// minutes after it completes, while the record lives for days or months, so anything the
    /// journal has to answer on its own has to be inside it.
    /// </remarks>
    /// <param name="transactionEvent">Bus event.</param>
    /// <returns>Parameters of the operation, or null when the event carries none.</returns>
    private static AuditConfirmationParameters? MapConfirmationParameters(TransactionEvent transactionEvent)
    {
        if (transactionEvent.Context?.ConfirmationParameters is not { } parameters)
        {
            return null;
        }

        return new AuditConfirmationParameters
        {
            ActionType = parameters.ActionType,
            SlotValues = parameters.SlotValues
        };
    }

    /// <summary>
    /// Resolves the channel type: from the event when it states one, otherwise from the attribution
    /// of the identity attached to the transaction.
    /// </summary>
    /// <param name="transactionEvent">Bus event.</param>
    /// <returns>Channel type, or null when unknown.</returns>
    /// <remarks>
    /// Visible to the receiver as well, because the receiver has to ask the resolver ABOUT THE SAME
    /// channel this method attributes the record to: a second rule of "which channel is this event's"
    /// would let the <c>ChannelType</c> of a record and its declared verification level speak about
    /// two different channels.
    /// </remarks>
    internal static string? ResolveChannelType(TransactionEvent transactionEvent) =>
        transactionEvent switch
        {
            TransactionConfirmedEvent confirmed => confirmed.ChannelType,
            TransactionChannelIdentityAttachedEvent attached => attached.ChannelType,
            TransactionChannelAuditEvent channelAudit => channelAudit.ChannelType,
            _ => transactionEvent.Context?.ChannelType
        };

    /// <summary>
    /// Builds the masked actor value out of the attribution of the event.
    /// </summary>
    /// <param name="transactionEvent">Bus event.</param>
    /// <returns>
    /// Masked identity in the form "channel:**id", or the system agent when the event carries no
    /// channel identity.
    /// </returns>
    private static string ResolveChannelIdentityActor(TransactionEvent transactionEvent) =>
        transactionEvent.Context?.MaskedChannelUserId is { } maskedChannelUserId
        && ResolveChannelType(transactionEvent) is { } channelType
            ? string.Concat(channelType, ActorSeparator, maskedChannelUserId)
            : AuditActors.System;
}
