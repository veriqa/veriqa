// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.Metrics;

using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Metrics of the channel adapter component (SPEC-003 §6.5), built on the BCL
/// <c>System.Diagnostics.Metrics</c>. The component owns the instruments but subscribes to nothing:
/// exporting them is the host's duty, which is why the names live in the contracts package and the
/// host adds the meter to its OpenTelemetry provider by the very same constant.
/// </summary>
/// <remarks>
/// Public, yet with nothing public on it: the type can be named and taken out of DI, and nothing more.
/// Every path that processes a channel event — the webhook route, the polling loops, the inbound
/// routes of the Email channel — gets the instrument from the container through
/// <see cref="ChannelInboundProcessor"/>, which hands it down to the outbound seam the same way it
/// hands down the logger. Recording is the component's own business: the constructor and the
/// recording method stay internal, so nobody outside writes into a series that integrators build
/// dashboards on.
/// <para>
/// Instance, not static, and created through <see cref="IMeterFactory"/>: a static meter cannot be
/// substituted in a test, while <c>MetricCollector</c> subscribes to the meter a factory produced.
/// </para>
/// </remarks>
public sealed class ChannelAdapterMetrics
{
    /// <summary>
    /// Counter of user notifications the channel failed to deliver.
    /// </summary>
    private readonly Counter<long> _failedNotices;

    /// <summary>
    /// Counter of sign-in start events that were not served.
    /// </summary>
    private readonly Counter<long> _unservedStarts;

    /// <summary>
    /// Counter of channel events that name a transaction of another tenant than the owner of the
    /// registration.
    /// </summary>
    private readonly Counter<long> _foreignTransactionEvents;

    /// <summary>
    /// Counter of channel message templates the core could not resolve for an adapter.
    /// </summary>
    private readonly Counter<long> _templateResolutionFailures;

    /// <summary>
    /// Creates the instruments on the component's own meter.
    /// </summary>
    /// <param name="meterFactory">Meter factory of the host.</param>
    internal ChannelAdapterMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(ChannelTelemetry.MeterName);

        _failedNotices = meter.CreateCounter<long>(ChannelTelemetry.FailedNoticesCounterName);
        _unservedStarts = meter.CreateCounter<long>(ChannelTelemetry.UnservedStartsCounterName);
        _foreignTransactionEvents = meter.CreateCounter<long>(ChannelTelemetry.ForeignTransactionEventsCounterName);
        _templateResolutionFailures = meter.CreateCounter<long>(ChannelTelemetry.TemplateResolutionFailuresCounterName);
    }

    /// <summary>
    /// Records one failed user notification.
    /// </summary>
    /// <remarks>
    /// Tags carry no PII (CA-121): neither the recipient, nor the message text, nor the inbound
    /// token contents. The error code always comes from an existing code registry, so the tag
    /// cardinality stays bounded.
    /// </remarks>
    /// <param name="channelType">Channel type the notification belonged to.</param>
    /// <param name="errorCode">Error code returned by the adapter.</param>
    /// <param name="noticeKind">Notification kind (see <see cref="ChannelNoticeKinds"/>).</param>
    internal void RecordFailedNotice(string channelType, string errorCode, string noticeKind)
    {
        _failedNotices.Add(
            1,
            new KeyValuePair<string, object?>(ChannelTelemetry.ChannelTypeTag, channelType),
            new KeyValuePair<string, object?>(ChannelTelemetry.ErrorCodeTag, errorCode),
            new KeyValuePair<string, object?>(ChannelTelemetry.NoticeKindTag, noticeKind));
    }

    /// <summary>
    /// Records one sign-in start event the core could not serve.
    /// </summary>
    /// <remarks>
    /// The branch is what is counted, not the reply: the event is recorded whether the registration
    /// declared a reply text, whether the user's budget allowed one, and whichever way the transaction
    /// refused. Only the channel type is tagged — the reason of the refusal belongs to the journal,
    /// which is keyed by the transaction, while an unbounded reason tag here would be written by
    /// whoever sends the events (CA-121: nothing of the sender is tagged either).
    /// </remarks>
    /// <param name="channelType">Channel type the event arrived through.</param>
    internal void RecordUnservedStart(string channelType)
    {
        _unservedStarts.Add(
            1,
            new KeyValuePair<string, object?>(ChannelTelemetry.ChannelTypeTag, channelType));
    }

    /// <summary>
    /// Records one channel event that names a transaction of a tenant other than the owner of the
    /// registration it arrived through (SPEC-042 R60).
    /// </summary>
    /// <remarks>
    /// Only the channel type is tagged, for the reason stated on <see cref="RecordUnservedStart"/>:
    /// the tenants and the identifier are written by whoever sends the events, so they stay in the
    /// log, where the identifier goes as a fingerprint.
    /// </remarks>
    /// <param name="channelType">Channel type the event arrived through.</param>
    internal void RecordForeignTransactionEvent(string channelType)
    {
        _foreignTransactionEvents.Add(
            1,
            new KeyValuePair<string, object?>(ChannelTelemetry.ChannelTypeTag, channelType));
    }

    /// <summary>
    /// Records one channel message template the core could not resolve for an adapter
    /// (<see cref="Abstractions.IChannelMessageTemplates"/>): the message it was asked for is not sent.
    /// </summary>
    /// <remarks>
    /// Tags carry no PII (CA-121): only the channel type and the message kind. The transaction stays
    /// in the log, next to the exception.
    /// </remarks>
    /// <param name="channelType">Channel type of the adapter that asked.</param>
    /// <param name="messageKind">Kind of the message whose template was asked for.</param>
    internal void RecordTemplateResolutionFailure(string channelType, string messageKind)
    {
        _templateResolutionFailures.Add(
            1,
            new KeyValuePair<string, object?>(ChannelTelemetry.ChannelTypeTag, channelType),
            new KeyValuePair<string, object?>(ChannelTelemetry.MessageKindTag, messageKind));
    }
}
