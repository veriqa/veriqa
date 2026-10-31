// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http;

/// <summary>
/// Source of the <c>webhook-id</c> of the inbound event that triggered the current outbound call of an
/// HTTP channel (SPEC-042 L43): the idempotency key of a <c>send-prompt</c> or <c>report-outcome</c> the
/// event caused is derived from it, so a repeated delivery of the same event yields the same key.
/// <para>
/// The shipped implementation reads the <c>webhook-id</c> header of the current HTTP request when that
/// request is the inbound route of the channel (with or without the tenant segment). It is registered
/// only when no implementation is registered yet, so a host replaces it by registering its own before
/// the channels are added.
/// </para>
/// </summary>
public interface IHttpChannelInboundEventIdSource
{
    /// <summary>
    /// Returns the identifier of the inbound event of the channel the current call runs in.
    /// </summary>
    /// <param name="channelType">Channel type of the HTTP registration making the call.</param>
    /// <returns>The <c>webhook-id</c> of the event; <c>null</c> — the call runs outside an inbound event
    /// of this channel (a background path).</returns>
    string? GetCurrentEventId(string channelType);
}
