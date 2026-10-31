// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.TransactionEngine.Constants;

/// <summary>
/// Observable telemetry names of the transaction lifecycle. They live in the contracts package
/// because they are part of the product's observable contract: a host subscribes its OpenTelemetry
/// provider to the activity source and the meter by these very names, and a third-party integrator
/// builds dashboards on these span and instrument names. The engine owns the instruments and
/// subscribes to nothing — exporting them is the host's duty.
/// </summary>
/// <remarks>
/// The two groups of tag names below are not separated cosmetically. A name from
/// <see cref="TransactionTypeTag"/>/<see cref="OutcomeTag"/> is admissible both on a metric and on a
/// span; a name from <see cref="TransactionIdTag"/>/<see cref="CorrelationTraceIdTag"/> is
/// admissible on a span only. A metric tag is a dimension: every distinct value is a new time
/// series, and a per-operation identifier there would grow the series count without bound.
/// The channel tag is deliberately NOT redeclared here: <c>ChannelTelemetry.ChannelTypeTag</c>
/// already carries <c>channel_type</c> and is in use — one fact, one home.
/// </remarks>
public static class TransactionTelemetry
{
    /// <summary>
    /// Name of the activity source owned by the transaction engine component.
    /// </summary>
    public const string ActivitySourceName = "Veriqa.Core.TransactionEngine";

    /// <summary>
    /// Name of the meter owned by the transaction engine component.
    /// </summary>
    public const string MeterName = "Veriqa.Core.TransactionEngine";

    /// <summary>
    /// Span: creation of an authentication transaction, the root of the sign-in trace.
    /// </summary>
    public const string CreateActivityName = "veriqa.transaction.create";

    /// <summary>
    /// Span: finalization of a transaction from channel data (confirm → resolve identity → complete).
    /// </summary>
    public const string CompleteActivityName = "veriqa.transaction.complete";

    /// <summary>
    /// Counter: transactions that entered the lifecycle. Tagged by <see cref="TransactionTypeTag"/>.
    /// </summary>
    public const string InitiatedCounterName = "veriqa.transaction.initiated";

    /// <summary>
    /// Counter: transactions that reached a terminal state. Tagged by <see cref="OutcomeTag"/> and
    /// by <c>ChannelTelemetry.ChannelTypeTag</c>. One counter with an outcome tag rather than a
    /// counter per outcome: that gives both the funnel and the refusal share from a single series.
    /// </summary>
    public const string TerminatedCounterName = "veriqa.transaction.terminated";

    /// <summary>
    /// Histogram: lifetime of a transaction from creation to its terminal state, in seconds.
    /// Tagged by <see cref="OutcomeTag"/>. The unit lives in the instrument's descriptor, not in
    /// the name (OpenTelemetry semantic conventions).
    /// </summary>
    public const string DurationHistogramName = "veriqa.transaction.duration";

    /// <summary>
    /// Unit of <see cref="DurationHistogramName"/> — seconds, as UCUM spells it.
    /// </summary>
    public const string DurationUnit = "s";

    /// <summary>
    /// Value of <c>ChannelTelemetry.ChannelTypeTag</c> for a transaction no channel ever answered —
    /// an expiry before any prompt, or a refusal on a transaction that named no channel. A declared
    /// value rather than an absent tag: the series is then explicitly "nobody answered" instead of
    /// silently merging with a differently-shaped one.
    /// </summary>
    public const string UnknownChannelType = "unknown";

    /// <summary>
    /// Tag: transaction type (a bounded set — see <c>TransactionTypes</c>). Admissible on metrics.
    /// </summary>
    public const string TransactionTypeTag = "veriqa.transaction.type";

    /// <summary>
    /// Tag: terminal outcome of the transaction (a closed set — see <see cref="TransactionOutcomes"/>).
    /// Admissible on metrics.
    /// </summary>
    public const string OutcomeTag = "veriqa.transaction.outcome";

    /// <summary>
    /// Span attribute: identifier of the transaction the span acts on. Never a metric tag.
    /// </summary>
    public const string TransactionIdTag = "veriqa.transaction.id";

    /// <summary>
    /// Span attribute: trace identifier of the trace that created the transaction. It is what
    /// stitches the inbound channel trace to the sign-in trace — the inbound webhook arrives
    /// arbitrarily later and carries no trace context of its own. Never a metric tag.
    /// </summary>
    public const string CorrelationTraceIdTag = "veriqa.transaction.trace_id";
}

/// <summary>
/// Values of <see cref="TransactionTelemetry.OutcomeTag"/> — a closed set, so the tag's cardinality
/// is known in advance.
/// </summary>
/// <remarks>
/// Deliberately not the channel's <c>TransactionOutcomeCodes</c>, despite three of the values
/// reading the same. That vocabulary answers "what is shown to the user in the messenger"
/// (SPEC-003 §6.2) and its first value is <c>confirmed</c>; this one answers "which terminal state
/// the engine recorded", and the state is <c>completed</c> — the sign-in is finished only once the
/// identity is resolved and stored, which is a step past the confirmation. The one value where they
/// differ is what makes them two facts rather than one.
/// </remarks>
public static class TransactionOutcomes
{
    /// <summary>
    /// The transaction was confirmed and completed: the sign-in went through.
    /// </summary>
    public const string Completed = "completed";

    /// <summary>
    /// The user refused the confirmation.
    /// </summary>
    public const string Declined = "declined";

    /// <summary>
    /// The transaction expired by TTL without an answer.
    /// </summary>
    public const string Expired = "expired";

    /// <summary>
    /// The transaction ended in a failure that is not the user's refusal.
    /// </summary>
    public const string Failed = "failed";

    /// <summary>
    /// The sign-in was closed because the same channel subject began a newer one. Kept apart from
    /// <see cref="Failed"/> so that repeated attempts do not raise the share of failures.
    /// </summary>
    public const string Superseded = "superseded";
}
