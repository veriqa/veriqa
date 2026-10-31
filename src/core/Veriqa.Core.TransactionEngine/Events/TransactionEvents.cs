// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Events;

/// <summary>
/// Base class of a transaction event.
/// All events contain the transaction identifier and the moment of occurrence.
/// </summary>
public abstract record TransactionEvent
{
    /// <summary>
    /// Identifier of the transaction the event occurred for.
    /// </summary>
    public required TransactionId TransactionId { get; init; }

    /// <summary>
    /// Moment the event occurred (UTC).
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Attribution of the transaction captured at publication time; null when the publisher has no
    /// transaction at hand. A subscriber reads the attributes from here instead of the store: the
    /// transaction may already be gone by the time the event is handled.
    /// </summary>
    public TransactionEventContext? Context { get; init; }
}

/// <summary>
/// Transaction created.
/// </summary>
public sealed record TransactionCreatedEvent : TransactionEvent
{
    /// <summary>
    /// Type of the created transaction.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Transaction TTL expiration time.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Transaction activated (State → Pending).
/// </summary>
public sealed record TransactionActivatedEvent : TransactionEvent;

/// <summary>
/// Transaction confirmed by the user (State → Confirmed).
/// </summary>
public sealed record TransactionConfirmedEvent : TransactionEvent
{
    /// <summary>
    /// Type of the channel through which the confirmation occurred.
    /// </summary>
    public required string ChannelType { get; init; }
}

/// <summary>
/// Channel identity attached to a transaction that stays Pending: the core accepted the user's
/// action in the channel and now waits for the confirming answer on its own web page.
/// Additive event — the lifecycle state does not change.
/// </summary>
public sealed record TransactionChannelIdentityAttachedEvent : TransactionEvent
{
    /// <summary>
    /// Type of the channel the identity came from.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Whether the answer is taken only from the browser the identity was attached from
    /// (<see cref="ChannelSubjectBinding.AnswerBrowserBound"/>): the question is then already open in that
    /// browser, and no other page of the transaction leaves for it.
    /// </summary>
    public bool AnswerBrowserBound { get; init; }
}

/// <summary>
/// A step of the claims completion began (SPEC-041 L91): the transaction stays Pending, and the pages
/// learn which step to show. Additive event — the lifecycle state does not change.
/// </summary>
/// <remarks>
/// Published by the engine when a step begins — after the confirmation that set the completion mark,
/// and after an outcome that moved the completion to the next step — and again by whoever changes the
/// surface of the web step. <see cref="Step"/> and <see cref="Surface"/> are read from the transaction
/// as it is stored after the step starter ran, so they state what the store holds.
/// </remarks>
public sealed record TransactionClaimCompletionStepEvent : TransactionEvent
{
    /// <summary>
    /// Current step of the claims completion.
    /// </summary>
    public required ClaimCompletionStep Step { get; init; }

    /// <summary>
    /// Surface of the web step; null — the web step has not begun (always null on the phone step).
    /// </summary>
    public WebStepSurface? Surface { get; init; }
}

/// <summary>
/// Transaction completed successfully (State → Completed).
/// </summary>
public sealed record TransactionCompletedEvent : TransactionEvent
{
    /// <summary>
    /// Final user identifier (sub claim).
    /// </summary>
    /// <remarks>
    /// In-process payload only, excluded from serialization (SPEC-001 §10.3): an event leaves the
    /// process through an outbound publisher verbatim, and the default subject carries the channel
    /// user identifier in the clear. Outside the process the user is attributed by the masked
    /// identifier of <see cref="TransactionEvent.Context"/> alone.
    /// </remarks>
    [JsonIgnore]
    public string? Subject { get; init; }
}

/// <summary>
/// Transaction ended with an error (State → Failed).
/// </summary>
public sealed record TransactionFailedEvent : TransactionEvent
{
    /// <summary>
    /// Failure reason code.
    /// </summary>
    public required string ReasonCode { get; init; }
}

/// <summary>
/// Transaction expired by TTL (State → Expired).
/// </summary>
public sealed record TransactionExpiredEvent : TransactionEvent
{
    /// <summary>
    /// Reason code of the terminal state, stated by the publisher that performed the transition —
    /// symmetric with <see cref="TransactionFailedEvent.ReasonCode"/>.
    /// </summary>
    public required string ReasonCode { get; init; }

    /// <summary>
    /// TTL deadline the transaction ended at — the moment of the expiry itself, symmetric with
    /// <see cref="TransactionCreatedEvent.ExpiresAt"/>.
    /// </summary>
    /// <remarks>
    /// Stated separately from <see cref="TransactionEvent.OccurredAt"/> because the two are not the
    /// same moment here: the transition is performed by a sweep running on its own interval, so the
    /// moment of publication is the moment the expiry was NOTICED and lies up to one pass after the
    /// deadline it reports. A subscriber that tells the user when the transaction ended reads this
    /// one; a subscriber journalling when the event happened reads <c>OccurredAt</c>.
    /// </remarks>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Channel-specific intermediate transaction status changed (SPEC-016 §10.1).
/// An additive event: it does not change the transaction lifecycle state and is published
/// by a channel adapter to inform the UI about intermediate steps
/// (e.g. email: "message sent", "mail received").
/// </summary>
public sealed record TransactionChannelStatusChangedEvent : TransactionEvent
{
    /// <summary>
    /// Type of the channel that published the status (e.g. "email").
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Channel-specific status code (e.g. "sent", "mail_received").
    /// The code semantics are defined by the channel adapter.
    /// </summary>
    public required string ChannelStatus { get; init; }
}

/// <summary>
/// Channel audit event: a security-significant occurrence that does not change
/// the transaction lifecycle state (SPEC-016 EM-032, EM-066).
/// Consumers — external audit consumers (e.g. via the RabbitMQ publisher) and logs.
/// </summary>
public sealed record TransactionChannelAuditEvent : TransactionEvent
{
    /// <summary>
    /// Type of the channel that published the event (e.g. "email").
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Audit event code (e.g. "email_sender_verification_failed").
    /// </summary>
    public required string EventCode { get; init; }

    /// <summary>
    /// Safe event details (no PII in the clear — e.g. masked email, policy).
    /// </summary>
    public string? Details { get; init; }
}
