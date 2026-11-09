// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Http;
using Veriqa.Core.ChannelAdapter.Http.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Constants;
using Veriqa.Core.ChannelAdapter.Http.Outbound;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.DependencyInjection;

/// <summary>
/// Registration of the HTTP channels over <see cref="ChannelAdapterBuilder"/> — channels whose adapter
/// runs as a separate service and is reached over the wire contract of SPEC-042. The namespace is the
/// builder's own, so <c>adapters.AddHttpChannels()</c> reads like every other channel of the host.
/// </summary>
public static class ChannelAdapterBuilderHttpExtensions
{
    /// <summary>
    /// Registers every HTTP channel of the configuration — each section
    /// <c>Veriqa:Channels:{channel}</c> that carries an <c>Http</c> subsection (SPEC-042 R5, C30). A
    /// configuration without such sections registers no channel. The sections are read and checked
    /// once, here: a misconfigured HTTP channel stops the host with a message naming the key.
    /// Idempotent per service collection: a second call registers nothing.
    /// </summary>
    /// <param name="adapters">Channel adapter builder.</param>
    /// <returns>Builder for call chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// An HTTP channel section violates SPEC-042 E53/E55. Or the builder registers the configuration half of
    /// the channels only (<c>AddVeriqaChannelConfiguration</c>).
    /// </exception>
    public static ChannelAdapterBuilder AddHttpChannels(this ChannelAdapterBuilder adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        // Not a channel of the configuration-only composition: this method registers its clients and
        // its start-up check before any adapter, and its early return below would take the mark of a
        // later full call for its own.
        adapters.ThrowIfConfigurationOnly(nameof(AddHttpChannels));

        // The catalog of the keys is the mark of an earlier call: nothing but this method registers it,
        // so its presence means the channels of this configuration are registered already, and a second
        // pass would add every channel twice.
        if (adapters.Services.Any(descriptor =>
                !descriptor.IsKeyedService
                && ReferenceEquals(descriptor.ImplementationInstance, HttpChannelConfigKeys.Catalog)))
        {
            return adapters;
        }

        // The keys belong to the schema of every deployment that composes this call, whether or not its
        // configuration names an HTTP channel — declared before the sections are read.
        adapters.Services.AddVeriqaConfigKeyCatalog(adapters.Configuration, HttpChannelConfigKeys.Catalog);

        var registrations = HttpChannelRegistrationReader.Read(adapters.Configuration);

        if (registrations.ChannelTypes.Count is 0)
        {
            return adapters;
        }

        adapters.Services.AddSingleton(registrations);
        adapters.Services.AddHostedService<HttpChannelStartupCheck>();

        var configuration = adapters.Configuration;

        // Every registration declares its channel to the core, a disabled one included: the
        // declaration is what puts the channel into channels_enabled and under the start-up check of
        // the inbound-verification level (SPEC-042 R6, L41). The Enabled flag is read live, the way
        // the flag of a built-in channel is; the channel pulls no updates of its own (SPEC-042 R3).
        // The declaration also carries the fact the core checks outside this package: the owner of the
        // registration (SPEC-042 R60) — known for an enabled registration only, the one that gets an
        // adapter to serve anybody with.
        var owners = registrations.Enabled.ToDictionary(
            static registration => registration.ChannelType,
            static registration => registration.Tenant,
            StringComparer.Ordinal);

        foreach (var channelType in registrations.ChannelTypes)
        {
            adapters.Services.AddSingleton(new CoreChannelDeclaration(
                channelType,
                () => HttpChannelRegistrationReader.IsEnabled(configuration, channelType),
                usesPolling: null)
            {
                OwnerTenantId = owners.GetValueOrDefault(channelType)
            });
        }

        if (registrations.Enabled.Count is 0)
        {
            return adapters;
        }

        AddOutboundTransport(adapters.Services);

        // One wrapper class, one instance per enabled registration, each under the channel type of its
        // registration (SPEC-042 R1): the factory overload is the path that lets one class serve many
        // channel types, and the registry of the contour checks the name against every other channel.
        foreach (var registration in registrations.Enabled)
        {
            adapters.AddChannel(
                registration.ChannelType,
                services => CreateAdapter(registration, services),
                registration.RegistrationOptions);
        }

        return adapters;
    }

    /// <summary>
    /// Registers what the outbound calls of the HTTP channels stand on (SPEC-042 L44, L48, L43): the
    /// named client and the two ports with their shipped implementations.
    /// </summary>
    /// <param name="services">Service collection.</param>
    private static void AddOutboundTransport(IServiceCollection services)
    {
        // The shipped implementations of the ports give way to the host's own: a host that registered
        // an egress policy or an event-identifier source beforehand keeps it.
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IHttpChannelEgressPolicy, AllowAllHttpChannelEgressPolicy>();
        services.TryAddSingleton<IHttpChannelInboundEventIdSource, HttpContextInboundEventIdSource>();

        // The client follows no redirect (an answer outside the contract, SPEC-042 L44), keeps no
        // cookies across the tenants its calls are made for, and has no timeout of its own: the
        // attempt timeouts and the budget are those of the registration, applied by the outbound client.
        var client = services
            .AddHttpClient(
                HttpChannelConstants.HttpClientName,
                static httpClient => httpClient.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            });

        // A host may put a resilience handler on every client (the standard host does, through
        // ConfigureHttpClientDefaults). This client retries by the rules of the wire alone — a handler
        // retrying beneath it would re-send without the egress question and with a stale signature, and
        // would retry deep-link and health — so any inherited handler is removed. The package offers the
        // removal as an experimental API; its diagnostic is suppressed around this call only.
#pragma warning disable EXTEXP0001
        client.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }

    /// <summary>
    /// Builds the wrapper of one registration over its own outbound client.
    /// </summary>
    /// <param name="registration">The registration.</param>
    /// <param name="services">Service provider.</param>
    /// <returns>The adapter.</returns>
    private static HttpChannelAdapter CreateAdapter(HttpChannelRegistration registration, IServiceProvider services)
    {
        var timeProvider = services.GetRequiredService<TimeProvider>();

        var outbound = new HttpChannelOutboundClient(
            registration,
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<IHttpChannelEgressPolicy>(),
            services.GetRequiredService<IHttpChannelInboundEventIdSource>(),
            services.GetRequiredService<IConfigurationResolver>(),
            timeProvider,
            services.GetRequiredService<ILogger<HttpChannelOutboundClient>>());

        return new HttpChannelAdapter(
            registration,
            outbound,
            services.GetRequiredService<IChannelMessageTemplates>(),
            services.GetRequiredService<IConfirmationPromptLocalizer>(),
            timeProvider,
            services.GetRequiredService<ILogger<HttpChannelAdapter>>());
    }
}
