// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.Metrics;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.TransactionEngine.Constants;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Diagnostics;

/// <summary>
/// Lifecycle metrics of a transaction: how many entered, how many left and by which outcome, and
/// how long they lived. The component owns the instruments and subscribes to nothing — exporting
/// them is the host's duty, which is why the names live in the contracts package and the host adds
/// the meter to its OpenTelemetry provider by the very same constant.
/// </summary>
/// <remarks>
/// Instance, not static, and created through <see cref="IMeterFactory"/>: a static meter cannot be
/// substituted in a test, while <c>MetricCollector</c> subscribes to the meter a factory produced.
/// <para>
/// Tags carry no identifier of a transaction, a session or a channel user (CA-121), and not only
/// because of privacy: a metric tag is a dimension, and every distinct value of it is another time
/// series. The tags used here are closed sets — the transaction type, the outcome, the channel type.
/// </para>
/// </remarks>
internal sealed class TransactionMetrics
{
    /// <summary>
    /// Counter of transactions that entered the lifecycle.
    /// </summary>
    private readonly Counter<long> _initiated;

    /// <summary>
    /// Counter of transactions that reached a terminal state.
    /// </summary>
    private readonly Counter<long> _terminated;

    /// <summary>
    /// Histogram of transaction lifetimes, in seconds.
    /// </summary>
    private readonly Histogram<double> _duration;

    /// <summary>
    /// Creates the instruments on the component's own meter.
    /// </summary>
    /// <param name="meterFactory">Meter factory of the host.</param>
    public TransactionMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(TransactionTelemetry.MeterName);

        _initiated = meter.CreateCounter<long>(TransactionTelemetry.InitiatedCounterName);
        _terminated = meter.CreateCounter<long>(TransactionTelemetry.TerminatedCounterName);
        _duration = meter.CreateHistogram<double>(
            TransactionTelemetry.DurationHistogramName,
            unit: TransactionTelemetry.DurationUnit);
    }

    /// <summary>
    /// Records one transaction that entered the lifecycle.
    /// </summary>
    /// <param name="transactionType">Transaction type (a bounded set — see <c>TransactionTypes</c>).</param>
    public void RecordInitiated(string transactionType)
    {
        _initiated.Add(
            1,
            new KeyValuePair<string, object?>(TransactionTelemetry.TransactionTypeTag, transactionType));
    }

    /// <summary>
    /// Records one transaction that reached a terminal state, together with how long it lived.
    /// </summary>
    /// <remarks>
    /// Called at the point of the actual state transition, never on entry to the method that drives
    /// it: an idempotent repeat of a terminal transition must not count twice.
    /// A lifetime measured as negative means the clocks of two replicas disagree — the transaction
    /// looks created after it ended. Zero is recorded instead: a negative duration is not a fact
    /// about the transaction, and dropping the observation entirely would silently thin the
    /// histogram exactly when the deployment has a clock problem worth seeing.
    /// </remarks>
    /// <param name="outcome">Terminal outcome (see <see cref="TransactionOutcomes"/>).</param>
    /// <param name="channelType">Channel that answered, or <c>TransactionTelemetry.UnknownChannelType</c>.</param>
    /// <param name="lifetime">Time from creation to the terminal state.</param>
    public void RecordTerminated(string outcome, string channelType, TimeSpan lifetime)
    {
        _terminated.Add(
            1,
            new KeyValuePair<string, object?>(TransactionTelemetry.OutcomeTag, outcome),
            new KeyValuePair<string, object?>(ChannelTelemetry.ChannelTypeTag, channelType));

        _duration.Record(
            lifetime > TimeSpan.Zero ? lifetime.TotalSeconds : 0,
            new KeyValuePair<string, object?>(TransactionTelemetry.OutcomeTag, outcome));
    }

    /// <summary>
    /// Records a terminal transition of the transaction: the outcome counter and the lifetime
    /// histogram, measured from <c>CreatedAt</c> to <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// The lifetime is a difference of two stored moments rather than an elapsed-time reading:
    /// the transaction may well have been created on another replica, so there is no local start
    /// timestamp to subtract from.
    /// The channel tag falls back from the channel that actually answered to the channel that was
    /// asked, and then to the declared "nobody answered" value — which is the honest state of a
    /// transaction that expired before any channel replied.
    /// Every writer of a terminal state records through this one overload — the transaction service,
    /// the cleanup pass and the expiry step they share — so the shape of the record has one home.
    /// </remarks>
    /// <param name="transaction">Transaction that has just reached its terminal state.</param>
    /// <param name="outcome">Terminal outcome (see <see cref="TransactionOutcomes"/>).</param>
    /// <param name="now">Moment of the transition, read off the writer's clock.</param>
    public void RecordTerminated(Transaction transaction, string outcome, DateTimeOffset now)
    {
        var channelType = transaction.ChannelIdentitySnapshot?.ChannelType
            ?? transaction.RequestedChannelType
            ?? TransactionTelemetry.UnknownChannelType;

        RecordTerminated(outcome, channelType, now - transaction.CreatedAt);
    }
}
