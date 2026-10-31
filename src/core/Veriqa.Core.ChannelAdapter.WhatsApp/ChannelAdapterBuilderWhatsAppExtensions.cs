// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.WhatsApp;
using Veriqa.Core.ChannelAdapter.WhatsApp.Abstractions;
using Veriqa.Core.ChannelAdapter.WhatsApp.Configuration;
using Veriqa.Core.ChannelAdapter.WhatsApp.Constants;
using Veriqa.Core.ChannelAdapter.WhatsApp.Providers.MetaCloudApi;
using Veriqa.Core.ChannelAdapter.WhatsApp.Providers.Twilio;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.DependencyInjection;

/// <summary>
/// Registration of the WhatsApp channel over <see cref="ChannelAdapterBuilder"/>.
/// The namespace is the builder's own, so <c>adapters.AddWhatsApp()</c> stays the same line of code it
/// was while the channel lived in the base assembly — only the package it arrives from changed.
/// </summary>
public static class ChannelAdapterBuilderWhatsAppExtensions
{
    /// <summary>
    /// Registers the WhatsApp adapter with support for multiple providers.
    /// The channel logic is shared; the provider determines how messages are delivered.
    /// </summary>
    /// <param name="adapters">Channel adapter builder.</param>
    /// <returns>Builder for call chaining.</returns>
    public static ChannelAdapterBuilder AddWhatsApp(this ChannelAdapterBuilder adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        // Registration skeleton (shared helper).
        adapters.ConfigureChannelOptions<WhatsAppOptions, WhatsAppOptionsValidator>(WhatsAppOptions.SectionName);

        // What the channel contour has to know about WhatsApp, said by WhatsApp itself: its keys and
        // the core-level facts read off its options — both before the enabled check, as always.
        // WhatsApp is webhook-only — it declares no polling transport.
        adapters.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRegisterConfigKeys, WhatsAppConfigKeyRegistrar>());

        // The catalog of this channel — the declaration half of the same pair. It is registered HERE,
        // at the composition of the channel, so that the key reaches the schema of a deployment that
        // added this channel and no other: the call guards the catalog on its own, so adding the
        // channel twice still leaves one registration.
        adapters.Services.AddVeriqaConfigKeyCatalog(adapters.Configuration, WhatsAppConfigKeys.Catalog);
        adapters.DeclareCoreChannel<WhatsAppOptions>(ChannelTypes.WhatsApp, static options => options.Enabled);

        // If WhatsApp is disabled — exit after registering the options and validator.
        if (!adapters.IsChannelEnabled<WhatsAppOptions>(WhatsAppOptions.SectionName, static o => o.Enabled))
        {
            return adapters;
        }

        // Register the shipped provider the configured code names — Twilio for its code, Meta Cloud API
        // otherwise (the default, and the fallback for a code naming a provider supplied by the host).
        // Either is only a default: a provider supplied by the host arrives through UseWhatsAppProvider,
        // which may be called after AddWhatsApp, so the declared and the actual provider are matched at
        // startup. The code is read off the section the same way the enabled flag above is.
        var configuredProvider = adapters.Configuration
            .GetSection(WhatsAppOptions.SectionName)[nameof(WhatsAppOptions.Provider)];
        if (string.Equals(configuredProvider, WhatsAppProviderNames.Twilio, StringComparison.Ordinal))
        {
            RegisterTwilioProvider(adapters.Services);
        }
        else
        {
            RegisterMetaCloudApiProvider(adapters.Services);
        }

        // Startup check "the configured Provider code == the code of the registered provider".
        adapters.Services.AddHostedService<WhatsAppProviderStartupValidator>();

        // Register the WhatsApp adapter through the one registration path every channel takes. Meta
        // verifies the webhook with a GET handshake, so the channel declares the query parameter
        // whose value the core has to echo back — the verification route is mapped from that
        // declaration, not from the channel's name.
        return adapters.AddChannel<WhatsAppChannelAdapter>(
            ChannelTypes.WhatsApp,
            new ChannelRegistrationOptions
            {
                ErrorMessageText = MessageTemplateNaturalKeys.OutcomeErrorInChannel,

                // No StaleLinkReplyText, deliberately: on this platform a message sent outside an
                // approved template is a chargeable service message, so answering every stale link
                // would bill the installation for events it did not ask for. An installation that
                // accepts that cost states the text in its own registration.
                WebhookVerificationQueryKey = MetaCloudApiConstants.HubChallengeQueryKey
            });
    }

    /// <summary>
    /// Uses a custom WhatsApp delivery provider instead of the shipped one (Meta Cloud API or Twilio).
    /// The result does not depend on when the method is called — before or after <see cref="AddWhatsApp"/> —
    /// because the shipped provider is registered with <c>TryAddSingleton</c>. Calling it more than once is
    /// allowed: the last call wins. The provider's <c>ProviderType</c> must match the configured
    /// <c>Veriqa:Channels:WhatsApp:Provider</c>, otherwise the host stops at startup.
    /// </summary>
    /// <typeparam name="TProvider">Type of the custom delivery provider.</typeparam>
    /// <param name="adapters">Channel adapter builder.</param>
    /// <returns>Builder for call chaining.</returns>
    public static ChannelAdapterBuilder UseWhatsAppProvider<TProvider>(this ChannelAdapterBuilder adapters)
        where TProvider : class, IWhatsAppProvider
    {
        ArgumentNullException.ThrowIfNull(adapters);

        // AddSingleton, not TryAddSingleton: this is the substitution itself, not a default. The last
        // registration of a service is the one resolved, which is what makes the last Use* call win.
        adapters.Services.AddSingleton<IWhatsAppProvider, TProvider>();
        return adapters;
    }

    /// <summary>
    /// Registers the Meta Cloud API provider.
    /// </summary>
    /// <param name="services">Service collection.</param>
    private static void RegisterMetaCloudApiProvider(IServiceCollection services)
    {
        // Register the HTTP client for the Graph API.
        services
            .AddHttpClient(
                MetaCloudApiConstants.HttpClientName,
                client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(MetaCloudApiConstants.GraphApiTimeoutSeconds);
                });

        // Register the Meta Cloud API provider. TryAddSingleton, like every shipped default of a
        // one-implementation port here: a host that registered its own provider beforehand keeps it.
        services.TryAddSingleton<IWhatsAppProvider, MetaCloudApiWhatsAppProvider>();
    }

    /// <summary>
    /// Registers the Twilio provider.
    /// </summary>
    /// <param name="services">Service collection.</param>
    private static void RegisterTwilioProvider(IServiceCollection services)
    {
        // Register the HTTP client for the Twilio REST API.
        services
            .AddHttpClient(
                TwilioConstants.HttpClientName,
                client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(TwilioConstants.ApiTimeoutSeconds);
                });

        // TryAddSingleton, like the Meta default: a host that registered its own provider beforehand keeps it.
        services.TryAddSingleton<IWhatsAppProvider, TwilioWhatsAppProvider>();
    }
}
