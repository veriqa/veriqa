// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.TransactionEngine.DependencyInjection;
using Veriqa.Core.TransactionEngine.Events;

namespace Veriqa.Core.AuthServer.DependencyInjection;

/// <summary>
/// Builder for configuring the Veriqa AuthServer library in embedded mode.
/// Provides replaceable extension points and a high-level fluent API
/// on top of the low-level extension methods.
/// </summary>
public sealed class VeriqaAuthServerBuilder
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
    /// Host environment.
    /// </summary>
    internal IWebHostEnvironment Environment { get; }

    /// <summary>
    /// Indicates that the Transaction Engine has been configured explicitly.
    /// </summary>
    private bool _transactionEngineConfigured;

    /// <summary>
    /// Indicates that the channel adapters have been configured explicitly.
    /// </summary>
    private bool _channelAdaptersConfigured;

    /// <summary>
    /// Action configuring the EF Core provider of the OpenIddict store (host-owned).
    /// </summary>
    private Action<DbContextOptionsBuilder>? _openIddictDatabaseConfigurator;

    /// <summary>
    /// Creates a builder instance.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="environment">Host environment.</param>
    internal VeriqaAuthServerBuilder(
        IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        Services = services;
        Configuration = configuration;
        Environment = environment;
    }

    /// <summary>
    /// Indicates that the Transaction Engine has been configured (public, for internal checks).
    /// </summary>
    internal bool TransactionEngineConfigured => _transactionEngineConfigured;

    /// <summary>
    /// Indicates that the channel adapters have been configured (public, for internal checks).
    /// </summary>
    internal bool ChannelAdaptersConfigured => _channelAdaptersConfigured;

    /// <summary>
    /// Action configuring the EF Core provider of the OpenIddict store, or <see langword="null"/>
    /// when the caller did not configure one (the volatile InMemory store is then used, and whether
    /// that was spoken decides the startup check — SPEC-012 CFG-119).
    /// </summary>
    internal Action<DbContextOptionsBuilder>? OpenIddictDatabaseConfigurator => _openIddictDatabaseConfigurator;

    /// <summary>
    /// Configures the EF Core provider of the OpenIddict store.
    /// The provider choice, connection string and migrations assembly belong to the caller
    /// (host-owned, SPEC-001 §10.2):
    /// <code>
    /// authServer.UseOpenIddictDatabase(ef => ef.UseNpgsql(connectionString,
    ///     npgsql => npgsql.MigrationsAssembly("Veriqa.Core.AuthServer.Migrations.PostgreSql")));
    /// </code>
    /// Without this call and without <see cref="UseInMemoryOpenIddictStore"/> the host fails to start
    /// outside the Development environment (SPEC-012 CFG-118, CFG-119). Calling it more than once is
    /// allowed — the last call wins. A relational provider configured without a migrations assembly
    /// leaves MigrateAsync without migrations to apply (standard EF Core behaviour).
    /// </summary>
    /// <param name="configureDatabase">Action configuring EF Core (UseNpgsql / UseMySQL etc.).</param>
    /// <returns>The current builder for call chaining.</returns>
    public VeriqaAuthServerBuilder UseOpenIddictDatabase(
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        ArgumentNullException.ThrowIfNull(configureDatabase);

        // Store the delegate; AddVeriqaAuthServer passes it on to AddVeriqaOpenIddict
        _openIddictDatabaseConfigurator = configureDatabase;
        return this;
    }

    /// <summary>
    /// Explicitly selects the volatile EF Core InMemory store for the OpenIddict data
    /// (clients, tokens, authorizations). Intended for development, samples and tests only:
    /// the data does not survive a restart and is not shared between replicas.
    /// Without this call and without <see cref="UseOpenIddictDatabase"/> the host fails to
    /// start outside the Development environment (SPEC-012 CFG-118, CFG-119).
    /// The two methods are mutually exclusive and the last call wins — the same rule
    /// <see cref="UseOpenIddictDatabase"/> already declares.
    /// </summary>
    /// <returns>The current builder for call chaining.</returns>
    public VeriqaAuthServerBuilder UseInMemoryOpenIddictStore()
    {
        // Dropping the delegate is what makes "the last call wins" true in this direction as well:
        // without it a UseOpenIddictDatabase stated earlier would keep the relational provider while
        // the caller has just asked for the in-memory one.
        _openIddictDatabaseConfigurator = null;

        // The marker of the spoken choice has a single home — the container — so the startup check
        // reads it the same way for the builder and for the bare AddVeriqaOpenIddict entry point.
        Services.UseInMemoryOpenIddictStore();
        return this;
    }

    /// <summary>
    /// Configures the Transaction Engine via the fluent API.
    /// Allows choosing the storage (InMemory, EF Core, Redis) and the event publisher.
    /// InMemory storage is used by default.
    /// </summary>
    /// <param name="configure">Action for configuring the Transaction Engine.</param>
    /// <returns>The current builder for call chaining.</returns>
    public VeriqaAuthServerBuilder ConfigureTransactionEngine(
        Action<TransactionEngineBuilder> configure)
    {
        // Configure the Transaction Engine with custom settings
        Services.AddVeriqaTransactionEngine(Configuration, configure);
        _transactionEngineConfigured = true;
        return this;
    }

    /// <summary>
    /// Configures the channel adapters via the fluent API.
    /// Allows enabling/disabling specific channels (Telegram, MAX, etc.).
    /// By default no channels are connected — the application starts with a warning.
    /// </summary>
    /// <param name="configure">Action for configuring the adapters.</param>
    /// <returns>The current builder for call chaining.</returns>
    public VeriqaAuthServerBuilder AddChannelAdapters(
        Action<ChannelAdapterBuilder> configure)
    {
        // Register the channel adapters
        Services.AddVeriqaChannelAdapters(Configuration, configure);
        _channelAdaptersConfigured = true;
        return this;
    }

    /// <summary>
    /// Replaces the built-in authentication page renderer with a custom one.
    /// Extension point for fully overriding the HTML generation of the authentication page (UI-050).
    /// The result does not depend on when the method is called: inside the
    /// <c>AddVeriqaAuthServer</c> configure delegate or after it through the captured builder —
    /// either way the container resolves <typeparamref name="TRenderer"/>, because the shipped
    /// default is registered with <c>TryAddSingleton</c>. Calling it more than once is allowed —
    /// the last call wins.
    /// <para>
    /// The custom renderer receives the sign-in window and the entry page of a confirmation. The scanner
    /// page of hop mode — the page a phone opens by a hop QR code — stays with the built-in renderer: its
    /// page script sends the hop signals, so the design and the CSS variables apply there, the custom
    /// markup does not.
    /// </para>
    /// </summary>
    /// <typeparam name="TRenderer">Type of the custom renderer.</typeparam>
    /// <returns>The current builder for call chaining.</returns>
    public VeriqaAuthServerBuilder UseAuthPageRenderer<TRenderer>()
        where TRenderer : class, IAuthPageRenderer
    {
        // AddSingleton, not TryAddSingleton: this is the substitution itself, not a default. The last
        // registration of a service is the one resolved, which is what makes the last Use* call win.
        Services.AddSingleton<IAuthPageRenderer, TRenderer>();
        return this;
    }

    /// <summary>
    /// Replaces the built-in claims mapper with a custom one.
    /// Extension point for customizing the set of OIDC claims from the resolved identity.
    /// The result does not depend on when the method is called: inside the
    /// <c>AddVeriqaAuthServer</c> configure delegate or after it through the captured builder —
    /// either way the container resolves <typeparamref name="TMapper"/>, because the shipped
    /// default is registered with <c>TryAddSingleton</c>. Calling it more than once is allowed —
    /// the last call wins.
    /// <para>
    /// The custom mapper is registered as <b>scoped</b>: the seam is invoked once per authorization
    /// request, and the scenario it exists for — enriching the claims from the integrator's own
    /// database or directory — depends on scoped services such as a <c>DbContext</c>. A singleton
    /// mapper would turn such a dependency into a captive one. The shipped default stays a singleton
    /// (it is stateless and has no dependencies), and the substitution still wins at every call
    /// order: it is either the only registration of the service or the later one.
    /// </para>
    /// </summary>
    /// <typeparam name="TMapper">Type of the custom claims mapper.</typeparam>
    /// <returns>The current builder for call chaining.</returns>
    public VeriqaAuthServerBuilder UseClaimsMapper<TMapper>()
        where TMapper : class, IClaimsMapper
    {
        // Add, not TryAdd — see UseAuthPageRenderer above. Scoped rather than singleton: the mapper
        // is resolved per request and the documented enrichment path injects scoped services.
        Services.AddScoped<IClaimsMapper, TMapper>();
        return this;
    }

    /// <summary>
    /// Adds a transaction event handler alongside the built-in ones. The port is consumed as
    /// IEnumerable&lt;ITransactionEventHandler&gt;, so every registered handler receives every event;
    /// this method does not replace the shipped handlers.
    /// </summary>
    /// <typeparam name="THandler">Handler type.</typeparam>
    /// <returns>The current builder for call chaining.</returns>
    public VeriqaAuthServerBuilder AddTransactionEventHandler<THandler>()
        where THandler : class, ITransactionEventHandler
    {
        // TryAddEnumerable, like every neighbour on this port (the audit receiver, the in-channel
        // prompt expiry handler): it compares the (service, implementation) pair, so a repeated call
        // with the same handler adds no second registration — and therefore delivers no event twice.
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITransactionEventHandler, THandler>());
        return this;
    }
}
