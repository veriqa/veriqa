// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using OpenIddict.Client;

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Configuration;

/// <summary>
/// The part of the start-up checks of the web provider channels that only OpenIddict can make: the
/// options of the client are built here, at the start of the host, so a registration OpenIddict refuses
/// — an Apple client without its team, a setting of a provider outside what the provider admits —
/// stops the host before the first sign-in rather than at it. The refusal names the sections of the
/// channels the client was built from, with the message of OpenIddict that says what is missing.
/// <para>
/// Everything that can be checked from the configuration alone is checked earlier, at composition, by
/// <see cref="WebProviderRegistrationReader"/>. Like the other start-up passes of the channel contour
/// this is a hosted lifecycle service: <see cref="StartingAsync"/> runs before the web server binds.
/// </para>
/// </summary>
internal sealed class WebProviderStartupCheck : IHostedLifecycleService
{
    /// <summary>
    /// Options of the OpenIddict client, built on first read.
    /// </summary>
    private readonly IOptionsMonitor<OpenIddictClientOptions> _clientOptions;

    /// <summary>
    /// What the web provider channel sections declared at start.
    /// </summary>
    private readonly WebProviderRegistrations _registrations;

    /// <summary>
    /// Creates the start-up check.
    /// </summary>
    /// <param name="clientOptions">Options of the OpenIddict client.</param>
    /// <param name="registrations">What the web provider channel sections declared at start.</param>
    public WebProviderStartupCheck(
        IOptionsMonitor<OpenIddictClientOptions> clientOptions,
        WebProviderRegistrations registrations)
    {
        _clientOptions = clientOptions ?? throw new ArgumentNullException(nameof(clientOptions));
        _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
    }

    /// <summary>
    /// Builds the options of the OpenIddict client and refuses the start when OpenIddict refuses them.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    /// <exception cref="InvalidOperationException">OpenIddict refused the options of the client.</exception>
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = _clientOptions.CurrentValue;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or ArgumentException
                                              or FormatException
                                              or CryptographicException)
        {
            var channels = string.Join(
                ", ",
                _registrations.Enabled.Select(static registration =>
                    $"'{SectionOf(registration.ChannelType)}' ({registration.Provider.Name})"));

            throw new InvalidOperationException(
                $"Invalid web provider channel configuration: OpenIddict refused the options of its client, which "
                + $"carry the registrations of the web provider channels {channels}. {exception.Message} Check the "
                + $"settings of the provider in '{SettingsTemplate}' of the channel named by the message.",
                exception);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// No-op: the check runs in <see cref="StartingAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op: the check runs in <see cref="StartingAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op: the check holds no runtime state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op: the check holds no runtime state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// No-op: the check holds no runtime state.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Address of the settings of the provider of a channel, with the channel as a placeholder.
    /// </summary>
    private static string SettingsTemplate { get; } = ConfigurationPath.Combine(
        ChannelInboundVerificationConfigKeys.ChannelsCoreSectionName,
        "{" + ConfigDimensionNames.Channel + "}",
        WebProviderConfigKeys.WebProviderSectionName,
        WebProviderConfigKeys.SettingsSectionName);

    /// <summary>
    /// Path of the section of a channel, as the operator writes it.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <returns>The path.</returns>
    private static string SectionOf(string channelType) =>
        ConfigurationPath.Combine(
            ChannelInboundVerificationConfigKeys.ChannelsCoreSectionName,
            channelType,
            WebProviderConfigKeys.WebProviderSectionName);
}
