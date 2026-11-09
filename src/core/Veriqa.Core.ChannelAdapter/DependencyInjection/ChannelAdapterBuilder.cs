// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.DependencyInjection;

/// <summary>
/// Builder for configuring channel adapters.
/// Allows enabling the desired channels via a fluent API.
/// </summary>
public sealed class ChannelAdapterBuilder
{
    /// <summary>
    /// Service collection the builder registers into. Exposed for ecosystem packages that add their
    /// own registrations through an extension method on this builder; not part of the everyday
    /// configuration surface.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IServiceCollection Services { get; }

    /// <summary>
    /// Application configuration.
    /// </summary>
    internal IConfiguration Configuration { get; }

    /// <summary>
    /// Whether at least one adapter has been registered.
    /// </summary>
    internal bool HasAdapters { get; private set; }

    /// <summary>
    /// Whether the builder registers the configuration half of the channels only
    /// (<c>AddVeriqaChannelConfiguration</c>): their options, key catalogs, core-level registrars and
    /// declarations, and none of their working services.
    /// </summary>
    internal bool IsConfigurationOnly { get; }

    /// <summary>
    /// Registry of the registered channels (public SPI); created on the first
    /// <c>AddChannel</c> call and registered in DI once.
    /// </summary>
    private CustomChannelRegistry? _channels;

    /// <summary>
    /// Creates the channel adapter builder.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="isConfigurationOnly">
    /// <c>true</c> — register the configuration half of the channels only (see
    /// <see cref="IsConfigurationOnly"/>); <c>false</c> (default) — register the channels in full.
    /// </param>
    internal ChannelAdapterBuilder(
        IServiceCollection services,
        IConfiguration configuration,
        bool isConfigurationOnly = false)
    {
        Services = services;
        Configuration = configuration;
        IsConfigurationOnly = isConfigurationOnly;
    }

    /// <summary>
    /// Shared channel registration skeleton (CA-083): configuration section binding,
    /// validator, and ValidateOnStart. Deduplicates the copy-pasted
    /// <c>Configure + IValidateOptions + AddOptionsWithValidateOnStart</c> from the channel methods.
    /// Channel-specific registrations (client/provider/adapter/hosted) stay in the channel method.
    /// Idempotent per service collection: the same channel added again — through this builder or
    /// another one over the same collection — adds nothing, and the binding of the first call stays in
    /// effect.
    /// </summary>
    /// <typeparam name="TOptions">Channel Options type.</typeparam>
    /// <typeparam name="TValidator">Channel Options validator type.</typeparam>
    /// <param name="sectionName">Channel configuration section name.</param>
    internal void ConfigureChannelOptions<TOptions, TValidator>(string sectionName)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        // The mark of an earlier call is the validator this very skeleton registers — looked up in the
        // collection rather than in the builder, because the same channel may be added through two
        // builders. Matched by the type of the skeleton's own factory, so a validator or a Configure the
        // host registered itself for the same options — even the channel's own validator type — is not
        // taken for one.
        if (Services.Any(descriptor =>
                !descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(IValidateOptions<TOptions>)
                && descriptor.ImplementationFactory?.Target is ChannelOptionsValidatorFactory<TOptions, TValidator>))
        {
            return;
        }

        // Binding of the Veriqa:Channels:{Type}:* section — flat (invariant-1).
        Services.Configure<TOptions>(Configuration.GetSection(sectionName));

        // Validator + ValidateOnStart — a single skeleton for all channels.
        var validatorFactory = new ChannelOptionsValidatorFactory<TOptions, TValidator>();
        Services.Add(ServiceDescriptor.Singleton(
            typeof(IValidateOptions<TOptions>),
            new Func<IServiceProvider, object>(validatorFactory.Create)));
        Services.AddOptionsWithValidateOnStart<TOptions>();
    }

    /// <summary>
    /// Shared channel-enabled check: reads <c>Enabled</c> from the section.
    /// A single helper instead of duplicated private <c>IsXxxEnabled()</c> per channel.
    /// In the configuration-only mode it answers <c>false</c> whatever the section says: every channel
    /// method stops right after its configuration half, which is all that mode registers. The core-level
    /// availability of the channel does not follow from this answer — it is read off the options by the
    /// declaration (<see cref="DeclareCoreChannel{TOptions}"/>).
    /// </summary>
    /// <typeparam name="TOptions">Channel Options type.</typeparam>
    /// <param name="sectionName">Channel configuration section name.</param>
    /// <param name="isEnabled">Enabled-flag selector (the channel's semantics are preserved).</param>
    /// <returns>
    /// <c>true</c> if the channel is enabled in the configuration and the builder registers working
    /// channels.
    /// </returns>
    internal bool IsChannelEnabled<TOptions>(string sectionName, Func<TOptions, bool> isEnabled)
        where TOptions : class
    {
        if (IsConfigurationOnly)
        {
            return false;
        }

        var options = Configuration.GetSection(sectionName).Get<TOptions>();
        return options is not null && isEnabled(options);
    }

    /// <summary>
    /// Declares the core-level facts of a channel (<see cref="CoreChannelDeclaration"/>) off its own
    /// options: the channel contour reads them from this declaration rather than from each channel's
    /// named option type.
    /// Declared before the enabled check on purpose — a disabled channel still belongs to the schema
    /// of the deployment, it is simply not in the available set.
    /// Idempotent per service collection: a second declaration for the same options type adds nothing.
    /// </summary>
    /// <typeparam name="TOptions">Channel Options type.</typeparam>
    /// <param name="channelType">Channel type the declaration speaks for.</param>
    /// <param name="isEnabled">Reads the channel's <c>Enabled</c> flag off its options.</param>
    /// <param name="usesPolling">
    /// Reads whether the channel pulls updates itself off its options; <c>null</c> — the channel has
    /// no polling transport at all.
    /// </param>
    internal void DeclareCoreChannel<TOptions>(
        string channelType,
        Func<TOptions, bool> isEnabled,
        Func<TOptions, bool>? usesPolling = null)
        where TOptions : class
    {
        // The declaration is built by a factory, so the descriptor alone does not tell whose it is; the
        // factory's own type does. The look-up goes over the collection rather than the builder, because
        // the same channel may be declared through two builders.
        if (Services.Any(descriptor =>
                !descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(CoreChannelDeclaration)
                && descriptor.ImplementationFactory?.Target is CoreChannelDeclarationFactory<TOptions>))
        {
            return;
        }

        var factory = new CoreChannelDeclarationFactory<TOptions>(channelType, isEnabled, usesPolling);
        Services.Add(ServiceDescriptor.Singleton(
            typeof(CoreChannelDeclaration),
            new Func<IServiceProvider, object>(factory.Create)));
    }

    /// <summary>
    /// Refuses a registration of a working channel in the configuration-only mode, before it registers
    /// anything.
    /// </summary>
    /// <param name="method">Name of the registration method that was called.</param>
    /// <exception cref="InvalidOperationException">The builder registers the configuration half only.</exception>
    internal void ThrowIfConfigurationOnly(string method)
    {
        if (IsConfigurationOnly)
        {
            throw new InvalidOperationException(
                $"{method} registers a working channel, which "
                + $"{nameof(ChannelAdapterServiceCollectionExtensions.AddVeriqaChannelConfiguration)} does not do. "
                + "Register a working channel through "
                + $"{nameof(ChannelAdapterServiceCollectionExtensions.AddVeriqaChannelAdapters)}.");
        }
    }

    /// <summary>
    /// Registers a channel adapter (public SPI) — the single registration path, taken by the channels
    /// shipped with the product and by a third-party one alike.
    /// The adapter is registered as a singleton <see cref="IChannelAdapter"/> implementation, so it
    /// shows up in every core consumer of <c>IEnumerable&lt;IChannelAdapter&gt;</c> (sign-in window,
    /// webhook pipeline).
    /// </summary>
    /// <typeparam name="TAdapter">Adapter type implementing <see cref="IChannelAdapter"/>.</typeparam>
    /// <param name="channelType">
    /// Channel type of the adapter. Must match <see cref="CustomChannelConstants.ChannelTypePattern"/>
    /// (<c>\A[a-z][a-z0-9-]{0,63}\z</c>) and equal the value returned by the adapter's
    /// <see cref="IChannelAdapter.ChannelType"/>. A violation or a duplicate registration fails fast
    /// at startup.
    /// </param>
    /// <param name="mapWebhook">
    /// <c>true</c> (default) — map the generic webhook endpoint
    /// <c>POST /api/channels/{channelType}/webhook</c> (plus the optional tenant-segment variant)
    /// for this channel; <c>false</c> — register an outbound/polling-only channel without a webhook.
    /// </param>
    /// <returns>Builder for call chaining.</returns>
    public ChannelAdapterBuilder AddChannel<TAdapter>(string channelType, bool mapWebhook = true)
        where TAdapter : class, IChannelAdapter
        => AddChannel<TAdapter>(channelType, new ChannelRegistrationOptions { MapWebhook = mapWebhook });

    /// <summary>
    /// Registers a channel adapter with the full set of registration settings (public SPI) — status
    /// texts of its own and the platform's verification handshake.
    /// </summary>
    /// <typeparam name="TAdapter">Adapter type implementing <see cref="IChannelAdapter"/>.</typeparam>
    /// <param name="channelType">Channel type of the adapter (see the overload above).</param>
    /// <param name="options">Registration settings of the channel.</param>
    /// <returns>Builder for call chaining.</returns>
    public ChannelAdapterBuilder AddChannel<TAdapter>(string channelType, ChannelRegistrationOptions options)
        where TAdapter : class, IChannelAdapter
    {
        ThrowIfConfigurationOnly(nameof(AddChannel));

        RegisterChannel(channelType, typeof(TAdapter), options);
        Services.AddSingleton<IChannelAdapter, TAdapter>();

        HasAdapters = true;
        return this;
    }

    /// <summary>
    /// Registers a channel adapter created by a factory (public SPI) — for adapters that
    /// need explicit construction (own options, own clients). Semantics are identical to
    /// <see cref="AddChannel{TAdapter}(string, bool)"/>.
    /// </summary>
    /// <typeparam name="TAdapter">Adapter type implementing <see cref="IChannelAdapter"/>.</typeparam>
    /// <param name="channelType">Channel type of the adapter (see the overload above).</param>
    /// <param name="adapterFactory">Adapter factory resolved from the service provider.</param>
    /// <param name="mapWebhook">Whether to map the generic webhook endpoint (see the overload above).</param>
    /// <returns>Builder for call chaining.</returns>
    public ChannelAdapterBuilder AddChannel<TAdapter>(
        string channelType,
        Func<IServiceProvider, TAdapter> adapterFactory,
        bool mapWebhook = true)
        where TAdapter : class, IChannelAdapter
        => AddChannel(channelType, adapterFactory, new ChannelRegistrationOptions { MapWebhook = mapWebhook });

    /// <summary>
    /// Registers a channel adapter created by a factory, with the full set of registration settings
    /// (public SPI).
    /// </summary>
    /// <typeparam name="TAdapter">Adapter type implementing <see cref="IChannelAdapter"/>.</typeparam>
    /// <param name="channelType">Channel type of the adapter (see the overload above).</param>
    /// <param name="adapterFactory">Adapter factory resolved from the service provider.</param>
    /// <param name="options">Registration settings of the channel.</param>
    /// <returns>Builder for call chaining.</returns>
    public ChannelAdapterBuilder AddChannel<TAdapter>(
        string channelType,
        Func<IServiceProvider, TAdapter> adapterFactory,
        ChannelRegistrationOptions options)
        where TAdapter : class, IChannelAdapter
    {
        ThrowIfConfigurationOnly(nameof(AddChannel));
        ArgumentNullException.ThrowIfNull(adapterFactory);

        RegisterChannel(channelType, typeof(TAdapter), options);
        Services.AddSingleton<IChannelAdapter>(adapterFactory);

        HasAdapters = true;
        return this;
    }

    /// <summary>
    /// Validates the channel type and records it in the registry shared with the webhook
    /// pipeline. The registry is registered in DI once, on the first registered channel.
    /// </summary>
    /// <param name="channelType">Channel type of the adapter.</param>
    /// <param name="adapterType">Adapter type being registered for this channel type.</param>
    /// <param name="options">Registration settings of the channel.</param>
    private void RegisterChannel(string channelType, Type adapterType, ChannelRegistrationOptions options)
    {
        if (_channels is null)
        {
            // The registry is one per IServiceCollection, not one per builder: AddChannelAdapters /
            // AddVeriqaChannelAdapters may be called more than once (host + extension library), and a
            // second registry would shadow the first in DI — the routes of the first builder would
            // silently never be mapped, and its channel types would escape the collision checks.
            // So an already registered instance is reused.
            _channels = FindRegisteredRegistry();

            if (_channels is null)
            {
                _channels = new CustomChannelRegistry();
                Services.AddSingleton(_channels);

                // The registration ↔ adapter cross-check is bound to the host start, not to the
                // webhook mapping: an outbound/polling-only channel never maps a webhook and would
                // otherwise never be checked. A hosted lifecycle service (not an IStartupFilter) so it
                // also runs in a non-web host, still failing fast before the first request.
                Services.AddHostedService<CustomChannelStartupValidator>();
            }
        }

        // Validation and collision detection live in the registry — it fails fast at startup.
        _channels.Register(channelType, adapterType, options);
    }

    /// <summary>
    /// Returns the channel registry already registered in the service collection
    /// by an earlier <c>AddChannel</c> call (possibly from another builder), or null.
    /// </summary>
    /// <returns>The registry instance registered in DI, or null.</returns>
    private CustomChannelRegistry? FindRegisteredRegistry()
    {
        foreach (var descriptor in Services)
        {
            if (descriptor.ServiceType == typeof(CustomChannelRegistry)
                && descriptor.ImplementationInstance is CustomChannelRegistry registry)
            {
                return registry;
            }
        }

        return null;
    }

    /// <summary>
    /// Factory of the options validator the channel skeleton registers. A type of its own, so the
    /// registered descriptor tells the skeleton's validator from one the host registered itself (see
    /// <see cref="ConfigureChannelOptions{TOptions, TValidator}"/>). <see cref="Create"/> stays an
    /// instance method on purpose: the factory instance, as the target of the registered delegate, is
    /// that mark.
    /// </summary>
    /// <typeparam name="TOptions">Channel Options type.</typeparam>
    /// <typeparam name="TValidator">Channel Options validator type.</typeparam>
    private sealed class ChannelOptionsValidatorFactory<TOptions, TValidator>
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        /// <summary>
        /// Creates the validator, resolving its constructor dependencies from the container.
        /// </summary>
        /// <param name="provider">Service provider the dependencies are taken from.</param>
        /// <returns>The options validator of the channel.</returns>
        public IValidateOptions<TOptions> Create(IServiceProvider provider)
            => ActivatorUtilities.CreateInstance<TValidator>(provider);
    }

    /// <summary>
    /// Factory of the core-level declaration of a channel. A type of its own rather than a lambda, so the
    /// registered descriptor names the options type it speaks for (see
    /// <see cref="DeclareCoreChannel{TOptions}"/>).
    /// </summary>
    /// <typeparam name="TOptions">Channel Options type.</typeparam>
    /// <param name="channelType">Channel type the declaration speaks for.</param>
    /// <param name="isEnabled">Reads the channel's <c>Enabled</c> flag off its options.</param>
    /// <param name="usesPolling">Reads whether the channel pulls updates itself; <c>null</c> — no polling.</param>
    private sealed class CoreChannelDeclarationFactory<TOptions>(
        string channelType,
        Func<TOptions, bool> isEnabled,
        Func<TOptions, bool>? usesPolling)
        where TOptions : class
    {
        /// <summary>
        /// Creates the declaration over the options of the channel.
        /// </summary>
        /// <param name="provider">Service provider the options are taken from.</param>
        /// <returns>The core-level declaration of the channel.</returns>
        public CoreChannelDeclaration Create(IServiceProvider provider)
        {
            // The flags are read through two different accessors on purpose:
            // IOptionsMonitor for the availability set, which follows a live reload of the section, and
            // IOptions for the transport mode, which is a start-up decision.
            return new CoreChannelDeclaration(
                channelType,
                () => isEnabled(provider.GetRequiredService<IOptionsMonitor<TOptions>>().CurrentValue),
                usesPolling is null
                    ? null
                    : () => usesPolling(provider.GetRequiredService<IOptions<TOptions>>().Value));
        }
    }
}
