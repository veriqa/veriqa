// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The OPTIONAL dependency of the webhook routes themselves — what the tenant demux of a route
/// authorizes the tenant segment against. It is absent in a legitimate composition (a host mapping the
/// routes without the channel registration), so it is nullable and the demux degrades on its own.
/// <para>
/// It arrives as a handler argument rather than being fetched out of <c>RequestServices</c> inside the
/// route: resolving a "maybe registered" service is the composition root's business, and the object
/// is built by a DI factory, which is the one place where asking the container what it happens to hold
/// is the correct thing to do. The processing of the event past the route has its dependencies of its
/// own (<see cref="ChannelInboundProcessor"/>).
/// </para>
/// </summary>
/// <param name="ConfigurationResolver">
/// Canonical layer resolver — what the tenant demux authorizes the tenant segment against; null —
/// the channel registration is absent altogether, which makes the demux fail closed (CA-032).
/// </param>
public sealed record ChannelWebhookSupport(IConfigurationResolver? ConfigurationResolver)
{
    /// <summary>
    /// The support of a composition that registered no resolver. It is what a host mapping the webhook
    /// endpoints without the channel DI gets, and it degrades the way the demux already degrades on a
    /// missing resolver: it fails closed.
    /// </summary>
    public static ChannelWebhookSupport None { get; } = new((IConfigurationResolver?)null);
}
