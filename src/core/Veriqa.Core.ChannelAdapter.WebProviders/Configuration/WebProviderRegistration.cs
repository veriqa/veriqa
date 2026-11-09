// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.IdentityModel.Tokens;

using Veriqa.Core.ChannelAdapter.WebProviders.Providers;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Configuration;

/// <summary>
/// One enabled web provider channel as its section states it, read and checked once at start by
/// <see cref="WebProviderRegistrationReader"/>. One channel is one client registration of OpenIddict:
/// its registration identifier is the channel type.
/// </summary>
internal sealed class WebProviderRegistration
{
    /// <summary>
    /// Channel type — the name of the section, checked against the channel-type format.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// The provider of <c>OpenIddict.Client.WebIntegration</c> the channel signs in with.
    /// </summary>
    public required WebProviderDescriptor Provider { get; init; }

    /// <summary>
    /// Identifier of the client registered with the provider.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Secret of the client registered with the provider; <c>null</c> — none stated.
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Scopes requested beyond those the provider registration asks for by default.
    /// </summary>
    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>
    /// Settings of the provider's own, in the order the configuration states them.
    /// </summary>
    public required IReadOnlyList<WebProviderSetting> Settings { get; init; }

    /// <summary>
    /// Key the client signs its assertion with, with its key identifier, at a provider that
    /// authenticates the client this way; <c>null</c> — the provider takes no such key.
    /// </summary>
    public ECDsaSecurityKey? SigningKey { get; init; }

    /// <summary>
    /// Absolute <c>https</c> URL the routes of the channel are published under.
    /// </summary>
    public required Uri PublicBaseUrl { get; init; }

    /// <summary>
    /// Absolute address of return the provider sends the user back to — the one the integrator
    /// registers with the provider.
    /// </summary>
    public required Uri RedirectUri { get; init; }

    /// <summary>
    /// Name of the channel on the sign-in page; <c>null</c> — the display name of the provider in
    /// OpenIddict.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Inner markup of the channel glyph; <c>null</c> — none stated.
    /// </summary>
    public string? IconSvgPath { get; init; }

    /// <summary>
    /// Where the page of the channel takes "Yes" to its question by a slider instead of a button.
    /// </summary>
    public WebProviderConfirmSlider ConfirmSlider { get; init; } = WebProviderConfigKeys.DefaultConfirmSlider;

    /// <summary>
    /// Where the phone number of the account comes from; <c>null</c> — the channel gives no number.
    /// </summary>
    public WebProviderPhoneNumber? PhoneNumber { get; init; }

    /// <summary>
    /// Claims taken from the answer of the provider, in the order the configuration states them.
    /// </summary>
    public required IReadOnlyList<WebProviderClaimMapping> Claims { get; init; }
}
