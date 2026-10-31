// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http;

/// <summary>
/// Egress policy of the HTTP channels (SPEC-042 L48, R20): asked once before EVERY outbound connection
/// attempt of the wrapper, each retry included, and before the attempt is signed. A refusal opens no
/// connection; the call then fails as the channel being unavailable (<c>channel_not_available</c>), and
/// a health probe reports the channel as unhealthy.
/// <para>
/// The shipped implementation allows every call. It is registered only when no implementation is
/// registered yet, so a host replaces it by registering its own before the channels are added.
/// </para>
/// </summary>
public interface IHttpChannelEgressPolicy
{
    /// <summary>
    /// Decides whether the wrapper may open a connection for one attempt of an outbound call.
    /// </summary>
    /// <param name="request">What the attempt is: the tenant, the channel, the operation and the target.</param>
    /// <param name="cancellationToken">Cancellation token of the call.</param>
    /// <returns><c>true</c> — the connection may be opened; <c>false</c> — no connection is opened.</returns>
    ValueTask<bool> IsAllowedAsync(HttpChannelEgressRequest request, CancellationToken cancellationToken);
}
