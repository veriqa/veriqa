// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Veriqa.Core.Configuration;

/// <summary>
/// Registration of the canonical configuration resolver and of the application-configuration source
/// (SPEC-012 §10). The mechanism knows no contours: which levels the contour has, and which
/// source serves each of them, is stated by these registrations, and the cloud contour adds its own
/// source on top without a line of branching in the core.
/// </summary>
public static class ConfigurationResolverServiceCollectionExtensions
{
    /// <summary>
    /// Registers the configuration resolver together with the application-configuration source serving
    /// the core level. Idempotent: a repeated call does not create a second resolver or a second source.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaConfigurationResolver(this IServiceCollection services) =>
        services.AddVeriqaConfigurationResolver(static _ => { });

    /// <summary>
    /// Registers the configuration resolver and lets the deployment state, through the builder, what it
    /// puts in place of a replaceable part of the mechanism (SPEC-012 §10.6, CFG-235). The builder is
    /// the ONLY such point: what it does not offer is not replaceable, and a registration made anywhere
    /// else does not stand in for it.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">States the replacements of the mechanism's replaceable parts.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaConfigurationResolver(
        this IServiceCollection services,
        Action<ConfigurationResolverBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // The core level is always served from the application configuration: it is the degenerate N=1
        // case of the general model (self-hosted ≡ core, SPEC-012 §10.1).
        services.AddVeriqaOptionsConfigLevels(ConfigLevel.Core);

        // Caching and degradation layer owned by the resolver: it is what makes a broken source of one
        // owner stop short of the resolution of the others (SPEC-012 §10.6). Its dependencies are registered
        // here rather than assumed: AddLogging and AddOptions are additive and idempotent (TryAdd
        // inside), so a host that configured its own logging keeps it.
        services.AddLogging();
        services.AddOptions<ConfigResolutionOptions>();
        services.TryAddSingleton(TimeProvider.System);

        // The cache primitive behind the port. AddMemoryCache is additive and idempotent, so a host
        // that configured its own memory cache keeps it. This is the DEFAULT implementation of the
        // port: putting a distributed one in its place is stated through the builder below, which is
        // applied last and therefore wins over this line.
        services.AddMemoryCache();
        services.TryAddSingleton<IConfigResolutionCache, ConfigResolutionCache>();

        // The registry of extraction bindings and the map of the contour are filled once at startup and
        // only read afterwards; both refuse a registration that would make a level silently stop working.
        services.TryAddSingleton<ConfigBindingRegistry>();
        services.TryAddSingleton<ConfigSourceMap>();

        // What the walk of the configuration snapshot leaves for the resolution path (SPEC-012 §4.1
        // CFG-246): the records it threw out of the effective configuration, and how far the walk
        // reaches. It is registered next to the registry rather than inside the report, because the two
        // ends of it are the report and the resolver, and both are singletons of this container. A
        // deployment that registers no catalog gets an instance that says "nothing is covered, nothing
        // is discarded" — which is the behaviour the mechanism had before the walk could decide either.
        services.TryAddSingleton<ConfigSnapshotEffect>();

        // The ONE refusal of a deployment that states no value for a setting whose owner declared one
        // obligatory (SPEC-012 §10.6). It is registered here and not by an owner, because the point of
        // it is that there is exactly one: a second place building the same message is the very thing
        // the declared requirement replaces.
        services.TryAddSingleton<RequiredConfigValues>();

        // The read-only slice of the contour map, for the contour to assert its own expectation about
        // levels (SPEC-012 §10.6). The mechanism knows no contours and states no such expectation
        // itself; publishing the slice is what lets the owner of the deployment state it in one line.
        services.TryAddSingleton(provider => provider.GetRequiredService<ConfigSourceMap>().CreateView());

        // The read-only slice of the SCHEMA of declared keys (SPEC-012 §10.6) — the other half of what
        // a deployment can be told about its own configuration: the contour map above answers "who
        // serves which level", this one answers "what does a key declare". It is what makes a check
        // over the schema as a whole possible outside the mechanism, which cannot state such an
        // expectation itself.
        services.TryAddSingleton(provider => provider.GetRequiredService<ConfigBindingRegistry>().CreateSchemaView());

        // The read-only slice of the BINDINGS (SPEC-012 §10.6) — the third fact a deployment can be told:
        // which levels actually answer a key in this contour. Neither slice above says it — a served level
        // may hold no binding of a key, and a declared level is only where a key MAY be bound — and a
        // composition step deciding once at startup what the process exposes needs exactly that.
        services.TryAddSingleton(provider => provider.GetRequiredService<ConfigBindingRegistry>().CreateBindingView());

        // The single precedence resolver in the core (anti-fork, SPEC-012 §10.6) — registered
        // UNCONDITIONALLY. TryAdd would give the opposite guarantee of the one the anti-fork needs: a
        // registration made before this call would win, and the invariant would be lost to an ordering
        // nobody looks at. Replace is what gives both halves at once — a foreign registration made
        // earlier does not stand, and a repeated call of this extension leaves exactly one descriptor
        // (plain AddSingleton would leave two, and RemoveAll + Add is the very move this makes
        // impossible for anyone else). Replacing it deliberately AFTER this call is still possible and
        // is deliberately not fought: the guarantee is against an accidental override, and an
        // intentional fork is a matter for review rather than for code.
        services.Replace(ServiceDescriptor.Singleton<IConfigurationResolver, ConfigurationResolver>());

        // The startup report of the contour map and of the levels declared but left unbound. It is told
        // which levels the contour reads through a reader of documents — the same readers the registrar
        // of the catalogs binds over — so that a level a key addresses by identity is not reported as
        // forgotten where no path binding of it can exist.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ConfigResolutionStartupDiagnosticsService>(
                provider => new ConfigResolutionStartupDiagnosticsService(
                    provider.GetRequiredService<ConfigSourceMap>(),
                    provider.GetRequiredService<ConfigBindingRegistry>(),
                    provider.GetRequiredService<ConfigKeySchemaView>(),
                    provider.GetServices<ConfigKeyCatalog>(),
                    CollectLevelNodeReaders(provider).Keys.ToHashSet(),
                    provider.GetRequiredService<ILogger<ConfigResolutionStartupDiagnosticsService>>())));

        // The report on what a configuration SNAPSHOT holds that the mechanism cannot use. It is
        // registered unconditionally and walks whatever catalogs the contour registered: a deployment
        // that registers none gets a report that finds nothing to walk, not a missing report.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ConfigSnapshotDiagnosticsService>());

        // Applied LAST, so a replacement stated here wins over the defaults above unconditionally: the
        // builder is where the deployment states its answer, not where it adds a candidate.
        configure(new ConfigurationResolverBuilder(services));

        return services;
    }

    /// <summary>
    /// Registers the catalogs an owner STATES here, together with the snapshot catalogs their
    /// declarations imply (SPEC-012 §10.6, CFG-244), and with the one catalog this mechanism owns
    /// itself.
    /// <para>
    /// EVERY owner of declared keys calls this from its OWN composition, naming its own catalogs —
    /// the auth server when it composes the auth server, the contour of the channels when the
    /// channels are composed, a channel satellite when its channel is. Registering there is the only
    /// moment that works for an owner no composition root can name: its assembly is not loaded until
    /// its composition is reached. And it is what makes the ORDER of the compositions irrelevant —
    /// each call registers what it names and skips what the container already holds, so no owner can
    /// suppress the catalogs of another and none of them is registered twice.
    /// </para>
    /// <para>
    /// The catalogs are PASSED IN rather than collected from somewhere: the answer to "what does this
    /// deployment declare" is the composition itself, and the container is where it ends up. A
    /// registry alongside it would be a second answer, and the two would drift the moment an owner
    /// migrated — silently, because a key missing from a table looks exactly like a key that does not
    /// exist.
    /// </para>
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">
    /// The application configuration the core level is read from — the instance the composition root
    /// holds rather than whatever the container resolves, for the reason
    /// <see cref="PathConfigKeyRegistrar"/> takes it too.
    /// </param>
    /// <param name="catalogs">
    /// Catalogs the calling owner ships. An owner that ships none still gets the catalog of the
    /// mechanism, which is what the call is for on its own.
    /// </param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaConfigKeyCatalogs(
        this IServiceCollection services,
        IConfiguration configuration,
        params ConfigKeyCatalog[] catalogs)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(catalogs);

        // The catalog the MECHANISM itself owns — the settings of its own diagnostics. It travels with
        // this call because this is the composition of the mechanism as far as declared keys go: the
        // registration of the resolver takes no application configuration, and a snapshot catalog
        // cannot be built without one.
        services.AddVeriqaConfigKeyCatalog(configuration, LoggingConfigKeys.Catalog);

        foreach (var catalog in catalogs)
        {
            // The guard is PER CATALOG rather than over the whole block, and it lives in the call
            // below: a catalog that is already registered is skipped ALONE, so a second call does not
            // claim its keys twice, and a catalog registered by an earlier composition does not stop
            // the rest from being registered at all.
            services.AddVeriqaConfigKeyCatalog(configuration, catalog);
        }

        return services;
    }

    /// <summary>
    /// Registers ONE catalog of declared keys together with the snapshot catalogs its declarations
    /// imply (SPEC-012 §10.6, CFG-244). An owner calls this for every catalog it owns, at its OWN
    /// composition — which is what carries the declarations of an owner into a host composed without
    /// the auth server, where nothing else names them.
    /// <para>
    /// The registration is guarded PER CATALOG and is therefore idempotent: a catalog the collection
    /// already holds is skipped alone, so a composition called twice, and two compositions naming one
    /// catalog, both leave exactly one registration. That matters beyond tidiness —
    /// <see cref="PathConfigKeyRegistrar"/> walks EVERY registered instance, and a catalog reached
    /// twice declares each of its keys twice, which is refused at startup. Identity of a catalog is
    /// the instance itself: an owner declares into the single object it exposes.
    /// </para>
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">
    /// The application configuration the core level is read from — the instance the composition root
    /// holds rather than whatever the container resolves, for the reason
    /// <see cref="PathConfigKeyRegistrar"/> takes it too.
    /// </param>
    /// <param name="catalog">Catalog of the owner's declarations.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaConfigKeyCatalog(
        this IServiceCollection services,
        IConfiguration configuration,
        ConfigKeyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(catalog);

        // The one registrar that READS catalogs. It travels with the registration rather than being
        // expected of the composition, because a catalog nobody reads declares nothing: its keys are
        // absent from the schema, and an owner that also BINDS a level of such a key is refused at
        // startup for binding what nobody declared. The registration is idempotent and takes the same
        // application configuration this call already has, so the owner states its catalog and gets a
        // deployment that reads it — in whatever order the compositions ran.
        services.AddVeriqaPathConfigKeyRegistrar(configuration);

        if (services.Any(descriptor => ReferenceEquals(descriptor.ImplementationInstance, catalog)))
        {
            return services;
        }

        services.AddSingleton(catalog);

        // …and the snapshot catalogs those declarations imply, under the same guard: a second pass
        // would claim every "setting + level" pair twice.
        services.AddVeriqaDeclaredConfigSnapshotCatalogs(configuration, catalog);

        return services;
    }

    /// <summary>
    /// Registers the snapshot catalogs the DECLARATIONS of one owner imply: one per pair
    /// "setting + level" the owner declares by catalog (SPEC-012 §10.6, CFG-244). Everything such a
    /// catalog needs is already in the declaration — the key, its domain, the level and the address
    /// inside the record of that level — so a key with a domain costs no class per level of it.
    /// <para>
    /// The records of a level above the core are enumerated by their OWNER, through
    /// <see cref="IConfigNodeWalker"/>: a level whose walker the container does not hold is registered
    /// all the same and answers <see cref="IConfigSnapshotCatalog.CanWalk"/> with false, so the report
    /// NAMES it as standing outside the walk instead of counting it as walked and found clean.
    /// </para>
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">
    /// The application configuration the core level is read from — the instance the composition root
    /// holds rather than whatever the container resolves, for the reason
    /// <see cref="PathConfigKeyRegistrar"/> takes it too.
    /// </param>
    /// <param name="catalog">Catalog of the owner's declarations.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaDeclaredConfigSnapshotCatalogs(
        this IServiceCollection services,
        IConfiguration configuration,
        ConfigKeyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(catalog);

        var registrar = new DeclaredSnapshotCatalogRegistrar(services, configuration);

        foreach (var declared in catalog.Keys)
        {
            declared.Accept(registrar);
        }

        return services;
    }

    /// <summary>
    /// Declares that this contour serves the listed levels from the application configuration
    /// (files, environment variables, User Secrets, Key Vault). The call is idempotent and UNIONS the
    /// level sets of the single Options source, so a host without the auth server ends up with
    /// <c>{ Core }</c>, a host with it with <c>{ Core, Application, UiConfig }</c>, and the order of
    /// the DI extension calls does not change the result.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="levels">Levels served from the application configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaOptionsConfigLevels(
        this IServiceCollection services,
        params ConfigLevel[] levels)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(levels);

        // The source is a single instance shared by every registration: a second Options source would
        // be a second source on the same level, which is a startup error by design.
        var registered = services
            .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(OptionsConfigSource))
            ?.ImplementationInstance as OptionsConfigSource;

        if (registered is null)
        {
            registered = new OptionsConfigSource();
            services.AddSingleton(registered);
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IConfigSource, OptionsConfigSource>(
                    provider => provider.GetRequiredService<OptionsConfigSource>()));
        }

        registered.DeclareLevels(levels);

        return services;
    }

    /// <summary>
    /// Registers a registrar of key bindings. Registrars are applied once at startup, before the first
    /// resolution.
    /// </summary>
    /// <typeparam name="TRegistrar">Type of the registrar.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaConfigKeys<TRegistrar>(this IServiceCollection services)
        where TRegistrar : class, IRegisterConfigKeys
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRegisterConfigKeys, TRegistrar>());

        return services;
    }

    /// <summary>
    /// States the NODE reader of one level that has a record — the reader every key declared by catalog
    /// with an address at that level is bound over (<see cref="PathConfigKeyRegistrar"/>, SPEC-012
    /// §10.6, CFG-235). The reader is keyed by its level, and the FIRST one stated for a level wins:
    /// a contour that keeps the record of a level in a store of its own states its reader BEFORE the
    /// composition that ships the default (the auth server states the readers of the application and
    /// <c>ui_config</c> levels, guarded the same way), and a contour serving a level nobody else does
    /// states the only reader there is. Which levels a contour has is a property of the deployment
    /// (CFG-202): a level with no reader stated is simply not bound by path, and the startup report
    /// names a declared level nobody bound.
    /// <para>
    /// The core level has no record — it is read from the application configuration of the host — so a
    /// reader stated for it is refused rather than silently ignored.
    /// </para>
    /// </summary>
    /// <typeparam name="TReader">Type of the node reader; its dependencies come from the container.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="level">Level whose record the reader stands over.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaConfigLevelNodeReader<TReader>(
        this IServiceCollection services,
        ConfigLevel level)
        where TReader : class, IConfigRecordReader<ConfigNode>
    {
        ArgumentNullException.ThrowIfNull(services);
        RefuseTheCoreLevel(level);

        services.TryAddKeyedSingleton<IConfigRecordReader<ConfigNode>, TReader>(level);

        return services;
    }

    /// <summary>
    /// States the NODE reader of one level that has a record, built by a factory — for a reader that
    /// takes something the container does not hold, such as the configuration instance the composition
    /// root was handed. Same contract as <see cref="AddVeriqaConfigLevelNodeReader{TReader}"/>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="level">Level whose record the reader stands over.</param>
    /// <param name="factory">Builds the reader.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaConfigLevelNodeReader(
        this IServiceCollection services,
        ConfigLevel level,
        Func<IServiceProvider, IConfigRecordReader<ConfigNode>> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);
        RefuseTheCoreLevel(level);

        services.TryAddKeyedSingleton<IConfigRecordReader<ConfigNode>>(level, (provider, _) => factory(provider));

        return services;
    }

    /// <summary>
    /// Registers the ONE registrar of the keys declared by catalog (<see cref="PathConfigKeyRegistrar"/>)
    /// over the node readers stated for the levels (<see cref="AddVeriqaConfigLevelNodeReader{TReader}"/>).
    /// The readers are collected when the registrar is built, not when it is registered, so the order
    /// of the compositions does not matter: a reader stated after this call is still found. Idempotent —
    /// two compositions calling it leave one registrar, and one registrar walking every catalog is what
    /// keeps a key from being bound twice.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">
    /// The application configuration the core level is read from — the instance the composition root
    /// holds rather than whatever the container resolves, for the reason
    /// <see cref="PathConfigKeyRegistrar"/> takes it too.
    /// </param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaPathConfigKeyRegistrar(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRegisterConfigKeys, PathConfigKeyRegistrar>(
                provider => new PathConfigKeyRegistrar(
                    provider.GetServices<ConfigKeyCatalog>(),
                    CollectLevelNodeReaders(provider),
                    configuration,
                    provider.GetRequiredService<ILogger<PathConfigKeyRegistrar>>())));

        return services;
    }

    /// <summary>
    /// Refuses a node reader stated for the core level: that level is read from the application
    /// configuration and has no record for a reader to stand over.
    /// </summary>
    /// <param name="level">Level a reader is being stated for.</param>
    private static void RefuseTheCoreLevel(ConfigLevel level)
    {
        if (level is ConfigLevel.Core)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "The core level is read from the application configuration of the host and has no record reader.");
        }
    }

    /// <summary>
    /// Gathers the node reader of every level one was stated for — the map the registrar binds the
    /// levels of a declared key over. A level without a reader is absent from the map, which is what
    /// leaves it unbound.
    /// </summary>
    /// <param name="provider">Service provider the readers were stated in.</param>
    /// <returns>Node reader by level.</returns>
    private static IReadOnlyDictionary<ConfigLevel, IConfigRecordReader<ConfigNode>> CollectLevelNodeReaders(
        IServiceProvider provider)
    {
        var readers = new Dictionary<ConfigLevel, IConfigRecordReader<ConfigNode>>();

        foreach (var level in Enum.GetValues<ConfigLevel>())
        {
            if (level is ConfigLevel.Core)
            {
                continue;
            }

            if (provider.GetKeyedService<IConfigRecordReader<ConfigNode>>(level) is { } reader)
            {
                readers[level] = reader;
            }
        }

        return readers;
    }

    /// <summary>
    /// Turns the declarations of one owner into snapshot catalog registrations. It is a visitor because
    /// that is the one seam where the type erasure of a catalog of declarations is undone
    /// (<see cref="IConfigKeyDeclarationVisitor"/>): the catalog it builds is typed by the value of the
    /// key, and a cast made here would be a second place deciding what that type is.
    /// </summary>
    /// <param name="services">Service collection the registrations go into.</param>
    /// <param name="configuration">Application configuration the core level is read from.</param>
    private sealed class DeclaredSnapshotCatalogRegistrar(
        IServiceCollection services,
        IConfiguration configuration) : IConfigKeyDeclarationVisitor
    {
        /// <inheritdoc />
        /// <remarks>
        /// The two limits of <see cref="ConfigValueCatalog{T}"/> are applied HERE rather than met as a
        /// failure inside it: a key that declares no domain has nothing for the walk to check its
        /// stated values against, and a key whose value is a secret is never named by diagnostics
        /// whatever the outcome. Both simply cost no registration.
        /// </remarks>
        public void Visit<T>(DeclaredConfigKey<T> declared)
        {
            if (declared.Key.Domain is null || declared.Key.IsSecret)
            {
                return;
            }

            foreach (var address in declared.Levels)
            {
                // A level addressed by IDENTITY has no path and no document to walk into: what states
                // its values is a store keeping rows, and enumerating those is that store's own answer.
                if (address.Template is null)
                {
                    continue;
                }

                // Added rather than TryAdded: the deduplication of an enumerable registration goes by
                // implementation TYPE, and every catalog here is the same generic type over a different
                // pair — the pair itself is what must not repeat, and the report refuses a second
                // catalog on one pair whatever registered it.
                services.Add(ServiceDescriptor.Singleton<IConfigSnapshotCatalog>(
                    provider => DeclaredConfigValueCatalog<T>.Over(declared, address, configuration, provider)));
            }
        }
    }
}
