// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenIddict.Client;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.ChannelAdapter.WebProviders;
using Veriqa.Core.ChannelAdapter.WebProviders.Configuration;
using Veriqa.Core.ChannelAdapter.WebProviders.Constants;
using Veriqa.Core.ChannelAdapter.WebProviders.Pages;
using Veriqa.Core.ChannelAdapter.WebProviders.Providers;
using Veriqa.Core.ChannelAdapter.WebProviders.State;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.ChannelAdapter.DependencyInjection;

/// <summary>
/// Registration of the web provider channels over <see cref="ChannelAdapterBuilder"/> — channels the
/// user signs in on with an account of a provider of <c>OpenIddict.Client.WebIntegration</c> (Google,
/// Apple, Huawei, Yandex, VK ID…). The namespace is the builder's own, so
/// <c>adapters.AddWebProviders()</c> reads like every other channel of the host.
/// </summary>
public static class ChannelAdapterBuilderWebProvidersExtensions
{
    /// <summary>
    /// Registers every web provider channel of the configuration — each section
    /// <c>Veriqa:Channels:{channel}</c> that carries a <c>WebProvider</c> subsection. A configuration
    /// without such sections registers no channel, and one where no such channel is enabled registers no
    /// OpenIddict client either. The sections are read and checked once, here: a misconfigured channel
    /// stops the host with a message naming the key. Idempotent per service collection: a second call
    /// registers nothing.
    /// </summary>
    /// <param name="adapters">Channel adapter builder.</param>
    /// <returns>Builder for call chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// A web provider channel section is misconfigured. Or the builder registers the configuration half of
    /// the channels only (<c>AddVeriqaChannelConfiguration</c>).
    /// </exception>
    public static ChannelAdapterBuilder AddWebProviders(this ChannelAdapterBuilder adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        // Not a channel of the configuration-only composition: this method registers its clients and
        // its start-up check before any adapter, and its early return below would take the mark of a
        // later full call for its own.
        adapters.ThrowIfConfigurationOnly(nameof(AddWebProviders));

        // The catalog of the keys is the mark of an earlier call: nothing but this method registers it,
        // so its presence means the channels of this configuration are registered already, and a second
        // pass would add every channel and every client registration twice.
        if (adapters.Services.Any(descriptor =>
                !descriptor.IsKeyedService
                && ReferenceEquals(descriptor.ImplementationInstance, WebProviderConfigKeys.Catalog)))
        {
            return adapters;
        }

        // The keys belong to the schema of every deployment that composes this call, whether or not its
        // configuration names a web provider channel — declared before the sections are read.
        adapters.Services.AddVeriqaConfigKeyCatalog(adapters.Configuration, WebProviderConfigKeys.Catalog);

        var registrations = WebProviderRegistrationReader.Read(adapters.Configuration);

        if (registrations.ChannelTypes.Count is 0)
        {
            return adapters;
        }

        var configuration = adapters.Configuration;

        // Every channel declares itself to the core, a disabled one included: the declaration is what
        // puts the channel into channels_enabled and under the start-up check of the inbound-verification
        // level. The Enabled flag is read live, the way the flag of a built-in channel is.
        foreach (var channelType in registrations.ChannelTypes)
        {
            adapters.Services.AddSingleton(new CoreChannelDeclaration(
                channelType,
                () => WebProviderRegistrationReader.IsEnabled(configuration, channelType),
                usesPolling: null));
        }

        // A disabled channel has no client registration, no route and no adapter.
        if (registrations.Enabled.Count is 0)
        {
            return adapters;
        }

        adapters.Services.AddSingleton(registrations);
        adapters.Services.AddHostedService<WebProviderStartupCheck>();

        WebProviderClientComposition.AddClient(adapters.Services, registrations.Enabled);

        // The state of the pages lives in the process unless a store of the host's own — or the Redis
        // channel stores, which register theirs unconditionally — takes its place.
        adapters.Services.TryAddSingleton<IWebProviderStateStore, InMemoryWebProviderStateStore>();

        // The routes of the enabled channels — the link, the address of return and the page — are
        // mapped by the channels themselves wherever the host maps the Veriqa channel endpoints. The
        // forms of the page carry an anti-forgery token, and the browser holds the mark of its session.
        adapters.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IChannelEndpointRegistrar, WebProviderChannelEndpointRegistrar>());
        adapters.Services.AddAntiforgery();
        adapters.Services.TryAddSingleton<WebProviderPageMark>();

        // The texts of the page an application may reword for itself (AuthPage.Texts) — the channels'
        // part of the set, contributed beside the pages they map.
        adapters.Services.AddCorePageOverridableTexts(WebProviderPageStrings.OverridableTexts);

        // One adapter class, one instance per enabled channel, each under the channel type of its
        // section: the factory overload is the path that lets one class serve many channel types. The
        // channel has no webhook — its events come from its own routes.
        var registrationOptions = new ChannelRegistrationOptions { MapWebhook = false };

        foreach (var registration in registrations.Enabled)
        {
            adapters.AddChannel(
                registration.ChannelType,
                services => CreateAdapter(registration, services),
                registrationOptions);
        }

        return adapters;
    }

    /// <summary>
    /// Builds the adapter of one channel.
    /// </summary>
    /// <param name="registration">The channel.</param>
    /// <param name="services">Service provider.</param>
    /// <returns>The adapter.</returns>
    private static WebProviderChannelAdapter CreateAdapter(WebProviderRegistration registration, IServiceProvider services) =>
        new(
            registration,
            services.GetRequiredService<IChannelMessageTemplates>(),
            services.GetRequiredService<IConfirmationPromptLocalizer>(),
            services.GetRequiredService<IWebProviderStateStore>(),
            services.GetRequiredService<ITransactionStore>(),
            services.GetRequiredService<IOptionsMonitor<TransactionEngineOptions>>(),
            services.GetRequiredService<IOptionsMonitor<OpenIddictClientOptions>>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<WebProviderChannelAdapter>>());
}
