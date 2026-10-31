// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Routing;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.WebProviders.Configuration;
using Veriqa.Core.ChannelAdapter.WebProviders.Pages;

namespace Veriqa.Core.ChannelAdapter.WebProviders;

/// <summary>
/// Contributes the routes of the web provider channels wherever the host maps the Veriqa channel
/// endpoints: the channels have no webhook, but a link, an address of return and a page, which they map
/// through the seam open to any channel. Only the enabled channels are mapped — a disabled channel has
/// no route.
/// </summary>
internal sealed class WebProviderChannelEndpointRegistrar : IChannelEndpointRegistrar
{
    /// <summary>
    /// The channels read at start.
    /// </summary>
    private readonly WebProviderRegistrations _registrations;

    /// <summary>
    /// Creates the registrar over the channels read at start.
    /// </summary>
    /// <param name="registrations">The channels.</param>
    public WebProviderChannelEndpointRegistrar(WebProviderRegistrations registrations)
    {
        _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, ChannelEndpointPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(policies);

        foreach (var registration in _registrations.Enabled)
        {
            WebProviderEndpoints.Map(endpoints, registration.ChannelType, policies.PagePolicyName);
        }
    }
}
