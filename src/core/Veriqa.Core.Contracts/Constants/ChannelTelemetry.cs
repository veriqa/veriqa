// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Constants;

/// <summary>
/// Observable telemetry names of the channel adapter component (SPEC-003 §6.5).
/// They live in the contracts package because they are part of the product's observable contract:
/// a host subscribes its OpenTelemetry provider to the meter by this very name, and a third-party
/// integrator builds dashboards on these instrument names.
/// </summary>
public static class ChannelTelemetry
{
    /// <summary>
    /// Name of the meter owned by the channel adapter component.
    /// </summary>
    public const string MeterName = "Veriqa.Core.ChannelAdapter";

    /// <summary>
    /// Name of the activity source owned by the channel adapter component. A second registry for the
    /// same component is deliberately not introduced: the channel spans belong here, next to the
    /// channel meter a host already subscribes to.
    /// </summary>
    public const string ActivitySourceName = "Veriqa.Core.ChannelAdapter";

    /// <summary>
    /// Span: the confirmation prompt leaving the core for the messenger.
    /// </summary>
    public const string PromptSendActivityName = "veriqa.channel.prompt.send";

    /// <summary>
    /// Span: an inbound channel event reaching the core. It opens a trace of its own — the user
    /// answers an arbitrary amount of time after the prompt, so one span covering both would stay
    /// open all that while. The two traces are stitched by
    /// <c>TransactionTelemetry.CorrelationTraceIdTag</c>.
    /// </summary>
    public const string InboundActivityName = "veriqa.channel.inbound";

    /// <summary>
    /// Counter of user notifications the channel failed to deliver.
    /// </summary>
    public const string FailedNoticesCounterName = "veriqa.channel.notice.failures";

    /// <summary>
    /// Counter of sign-in start events the core could not serve: the transaction they name is gone,
    /// its window has closed, or it no longer accepts the event. It counts the BRANCH and not the
    /// reply — an installation whose channel declares no reply text stays silent, and the occurrence
    /// is still the only thing that makes such a silence visible.
    /// </summary>
    public const string UnservedStartsCounterName = "veriqa.channel.start.unserved";

    /// <summary>
    /// Counter of channel events that name a transaction of a tenant other than the owner of the
    /// channel registration they arrived through. Such an event is answered as one naming an unknown
    /// transaction, so the answer shows nothing; the counter is what shows the operator a search
    /// through the identifiers of other tenants.
    /// </summary>
    public const string ForeignTransactionEventsCounterName = "veriqa.channel.event.foreign_transaction";

    /// <summary>
    /// Counter of channel message templates the core could not resolve for an adapter: the message
    /// was not sent. It is separate from <see cref="FailedNoticesCounterName"/>, which counts what
    /// the channel failed to deliver.
    /// </summary>
    public const string TemplateResolutionFailuresCounterName = "veriqa.channel.template.failures";

    /// <summary>
    /// Tag: channel type the failed notification belonged to.
    /// </summary>
    public const string ChannelTypeTag = "channel_type";

    /// <summary>
    /// Tag: error code of the failure, always taken from an existing code registry so the tag
    /// cardinality stays bounded.
    /// </summary>
    public const string ErrorCodeTag = "error_code";

    /// <summary>
    /// Tag: kind of the notification (see <see cref="ChannelNoticeKinds"/>).
    /// </summary>
    public const string NoticeKindTag = "notice_kind";

    /// <summary>
    /// Tag: kind of the message whose template could not be resolved (a <c>MessageKinds</c> value).
    /// </summary>
    public const string MessageKindTag = "message_kind";
}

/// <summary>
/// Kinds of user notification counted by <see cref="ChannelTelemetry.FailedNoticesCounterName"/>.
/// </summary>
public static class ChannelNoticeKinds
{
    /// <summary>
    /// A plain message sent to the user (including the confirmation prompt).
    /// </summary>
    public const string Message = "message";

    /// <summary>
    /// A terminal transaction outcome shown to the user.
    /// </summary>
    public const string Outcome = "outcome";
}
