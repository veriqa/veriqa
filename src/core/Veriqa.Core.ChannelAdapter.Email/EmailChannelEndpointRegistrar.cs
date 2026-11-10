// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Routing;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Pipeline;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// Contributes the Email channel's own endpoints (SPEC-016 §4, §5) wherever the host maps the Veriqa
/// channel endpoints. The channel needs more than the generic webhook route — pages a person opens
/// in a browser and an inbound route outside the path convention — so it maps them itself, through
/// the seam open to any channel, instead of the auth server naming Email to do it.
/// <para>
/// Its pages are of two kinds the host offers a policy for: the form, the send and the code entry are
/// routes that make the channel send a message; the pages the links of the mails open are pages of a
/// message link.
/// </para>
/// </summary>
/// <remarks>
/// Registered by <c>AddEmail()</c> only when the channel is enabled, which is what keeps the routes
/// off a host that has no Email channel. The auth server does not read <c>EmailOptions</c> to decide
/// it: the condition is the channel's own.
/// </remarks>
internal sealed class EmailChannelEndpointRegistrar : IChannelEndpointRegistrar
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, ChannelEndpointPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(policies);

        endpoints.MapEmailAuthEndpoints(policies.MessageSendPolicyName, policies.MessageLinkPolicyName);
    }
}
