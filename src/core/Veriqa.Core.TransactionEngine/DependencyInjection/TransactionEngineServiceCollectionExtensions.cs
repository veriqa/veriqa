// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Diagnostics;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Events.Channels;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.RestrictionPolicies;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.TransactionEngine.DependencyInjection;

/// <summary>
/// Extension methods for registering the Transaction Engine in the DI container.
/// </summary>
public static class TransactionEngineServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Transaction Engine with the default configuration.
    /// Uses the in-memory store.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaTransactionEngine(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.AddVeriqaTransactionEngine(configuration, _ => { });
    }

    /// <summary>
    /// Registers the Transaction Engine with a configurable store.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="configure">Action configuring the Transaction Engine.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaTransactionEngine(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TransactionEngineBuilder> configure)
    {
        // Register the configuration from the Veriqa:TransactionEngine section
        services.Configure<TransactionEngineOptions>(
            configuration.GetSection(TransactionEngineOptions.SectionName));

        // Register the initiator context check configuration (SPEC-017 §10, SPEC-012 §6.1).
        // Used by the collection point (Authorization Endpoint) and the channel processing pipeline.
        services.Configure<InitiatorContextOptions>(
            configuration.GetSection(InitiatorContextOptions.SectionName));

        // Both sets of names the section carries are closed sets of the product, and a name outside
        // them is matched by nobody: the confirmation shows the field nowhere while the operator
        // believes it is shown. Judged at start rather than found by its absence on a live prompt.
        services.AddSingleton<IValidateOptions<InitiatorContextOptions>, InitiatorContextOptionsValidator>();
        services.AddOptionsWithValidateOnStart<InitiatorContextOptions>();

        // Comparable identity types (SPEC-012 §4.12, SPEC-039 C22): the global section is the core
        // level of the axis, and it is validated at startup — a repeated type name, an empty claim
        // name or an unknown normalization rule refuses the start (CFG-160), because a declaration
        // silently ignored leaves the operator believing a comparison is in effect that is not.
        services.Configure<IdentityMatchOptions>(
            configuration.GetSection(IdentityMatchOptions.SectionName));

        // Handed the configuration instance the options are bound from — the one the integrator
        // supplied — rather than whatever the container resolves: a host serving a subsection would
        // otherwise be judged over a root where the section is not there at all.
        services.AddSingleton<IValidateOptions<IdentityMatchOptions>>(
            _ => new IdentityMatchOptionsValidator(configuration));
        services.AddOptionsWithValidateOnStart<IdentityMatchOptions>();

        // Restriction policies (SPEC-018 §9.2): the core level of both policy keys. The section is
        // bound STRICTLY, as the resolver reads the same members: a stray member would otherwise pass
        // the start and be refused on the first resolution, leaving a running host that rejects every
        // event. A policy of a broken form refuses the start as well (RP-082).
        services.Configure<RestrictionPoliciesOptions>(
            configuration.GetSection(RestrictionPoliciesOptions.SectionName),
            static binder => binder.ErrorOnUnknownConfiguration = true);
        services.AddSingleton<IValidateOptions<RestrictionPoliciesOptions>, RestrictionPoliciesOptionsValidator>();
        services.AddOptionsWithValidateOnStart<RestrictionPoliciesOptions>();

        // Clock of the engine: TTL, expiry, cleanup boundaries and event moments read it instead of
        // the static system clock, which is what lets a test move time without touching product code.
        // Registered here rather than inherited from the configuration module: the engine assembly is
        // used without it. TryAdd is idempotent, so both components together still yield one instance.
        // Placed BEFORE the resolver call below on purpose, unlike the neighbouring roots that
        // register their clock after their callback: the resolver claims the same slot, so a line
        // moved down here would never register anything. A host substituting the provider therefore
        // registers it before AddVeriqaTransactionEngine, not inside the configure callback.
        services.TryAddSingleton(TimeProvider.System);

        // Canonical multi-level configuration resolver (SPEC-012 §10, CFG-210/211/212).
        // Shared foundation: consumed by re-leveling, ui_config, the channel track.
        // The single precedence resolver in the core (anti-fork CFG-202).
        services.AddVeriqaConfigurationResolver();

        // The catalogs the ENGINE owns, stated at the engine's own composition through the one method
        // that lists them (see AddTransactionEngineConfigKeyCatalogs).
        services.AddTransactionEngineConfigKeyCatalogs(configuration);

        // Register the configuration validator
        services.AddSingleton<IValidateOptions<TransactionEngineOptions>,
            TransactionEngineOptionsValidator>();

        // Enable validation at startup
        services.AddOptionsWithValidateOnStart<TransactionEngineOptions>();

        // Messages (SPEC-036): the section a deployment writes them in, read by a configurator of its
        // own — the root of the section holds one member per kind while the options type keeps them in
        // a member, and services.Configure(section) cannot express that gap (§4.6, TPL-116).
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<MessageTemplatesOptions>, MessageTemplatesOptionsConfigurator>(
                _ => new MessageTemplatesOptionsConfigurator(configuration)));

        // Two startup checks over that section, deliberately apart (TPL-111): the FORM of a
        // declaration, and a template ladder AGAINST the contract serving it — a question that spans
        // the two settings a message is made of and therefore belongs to neither of them alone.
        services.AddSingleton<IValidateOptions<MessageTemplatesOptions>, MessageTemplatesOptionsValidator>();
        services.AddSingleton<IValidateOptions<MessageTemplatesOptions>, MessageContractConformanceValidator>();
        services.AddOptionsWithValidateOnStart<MessageTemplatesOptions>();

        // The startup SIGNAL over the same section: a ladder traded away without meaning to. It is not
        // a third validator because what it reports is legitimate — a validator answers with errors,
        // and refusing the start would forbid a short ladder and a one-part mail, both of which the
        // specification allows (SPEC-036 §4.3, SPEC-016 §4.3). Said once at startup instead.
        services.AddHostedService<MessageTemplateDegradationStartupDiagnostics>();

        // Fail-fast caller-value validator (SPEC-036 §4.4). Registered as a live engine contract; its
        // production caller is track A (TASK-078), its current consumers are the unit tests.
        services.TryAddSingleton<CallerSlotValidator>();

        // Lifecycle metrics of the engine. AddMetrics is the framework's own way of bringing
        // IMeterFactory in and is idempotent, so it is called here rather than assumed: an
        // integrator may consume this assembly without ServiceDefaults, and a missing factory would
        // then fail at resolve time.
        services.AddMetrics();
        services.TryAddSingleton<TransactionMetrics>();

        // The identity-match axis of the engine (SPEC-039 R40): the verdict written where the resolved
        // identity is, and the expectations term of the idempotent-repeat comparison. The reversible
        // protection its values need is NOT registered here — it comes from the composition that owns
        // a key ring (the auth server), and the axis asks for it once per operation instead of holding
        // it, so a host taking the engine alone still builds.
        services.TryAddSingleton<IdentityMatchService>();

        // The restriction policies of the admissibility gate (SPEC-018 §6): the reader of the claim a
        // rule checks and the evaluator of a resolved set. Both stateless. External admissibility
        // checks are the deployment's to register — the engine ships none and takes whatever
        // collection the container holds, an empty one included.
        services.TryAddSingleton<IRestrictionClaimReader, RestrictionClaimReader>();
        services.TryAddSingleton<IRestrictionPolicyEvaluator, RestrictionPolicyEvaluator>();

        // Register the main transaction service
        services.AddSingleton<ITransactionService, TransactionService>();

        // The one computation of the claims completion (SPEC-041 L91). Scoped: it reads the channel
        // adapters, whose lifetime a host may narrow, and the service resolves it from a scope of its
        // own per operation rather than holding it.
        services.TryAddScoped<ClaimCompletionPlanner>();

        // Tenant of the calling client (the source of OidcContext.TenantId on the creation path).
        // The shipped answer is null — the single default tenant of a self-hosted installation — and
        // TryAdd keeps it out of the way of a resolver the host registered before this call. A
        // component wired AFTER this one displaces it explicitly instead (see
        // ReplaceShippedClientTenantResolver), because TryAdd alone would leave the null standing.
        services.TryAddSingleton<IClientTenantResolver, DefaultClientTenantResolver>();

        // Register Identity Resolution (TASK-002):
        // DefaultIdentityResolutionService — the basic implementation (sub = channel_type:channel_user_id).
        // IChannelIdentityRepository is deliberately NOT registered: the shipped assembly stores no
        // channel identities (they are PII — channel user id, phone, email, display name — and the
        // port promises indefinite storage without deletion or TTL, which is the integrator's
        // decision to make, not a default). Nothing here injects the port either — the resolver asks
        // for it once per operation from a scope of its own. That keeps "nobody registered it" a
        // legal composition, picks up an implementation the host registered before or after this
        // call, and imposes no lifetime on it: a Scoped EF implementation is safe, where injecting
        // it into this singleton would capture one DbContext for the whole process.
        services.TryAddSingleton<IIdentityResolutionService, DefaultIdentityResolutionService>();

        // Apply user settings
        var builder = new TransactionEngineBuilder(services);
        configure(builder);

        // The background cleanup service is registered AFTER the builder on purpose: a relational store
        // registers its migration hosted service there, and the host starts hosted services one by one
        // in registration order, awaiting each StartAsync. Registered earlier, the cleanup cycle would
        // query an empty database before the migration had created it and log a failed login on the
        // first start of every new installation.
        services.AddHostedService<TransactionCleanupService>();

        // In-process dispatch to the registered handlers belongs to the engine and is wired
        // whichever transport the deployment chose: the audit trail, the real-time sign-in push and
        // the prompt cleanup are subscribers, and a transport that took their events away would
        // switch all three off without a word. Its background reader is a hosted service of its own.
        services.TryAddSingleton<ChannelTransactionEventDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<ChannelTransactionEventDispatcher>());

        // Every publisher registration present by now — the satellite's, the host's, or one from an
        // earlier call of this method — is a transport, and the single ITransactionEventPublisher
        // the consumers resolve is the fan-out over the dispatcher and those transports.
        CollectOutboundEventPublishers(services);
        services.AddSingleton<ITransactionEventPublisher, CompositeTransactionEventPublisher>();

        // A publisher registered AFTER this method wins the resolution and takes the in-process
        // subscribers away with it. That bypass is refused at startup instead of passing silently.
        services.AddHostedService<TransactionEventPublisherCompositionStartupCheck>();

        // If no store was registered via the builder and none was added manually — use in-memory
        if (!builder.StoreRegistered && !HasRegistration(services, typeof(ITransactionStore)))
        {
            builder.UseInMemoryStore();
        }

        // The store is known by now — whichever it is, it gets the state-transition guard. Doing it
        // here rather than inside the shipped stores is what makes the check reach a store an
        // integrator wrote: composition covers every implementation, an implementation covers only
        // itself.
        GuardTransactionStoreRegistrations(services);

        // A store registered AFTER this method wins the resolution and is therefore not guarded.
        // That bypass is refused at startup instead of passing silently.
        services.AddHostedService<TransactionStoreGuardStartupCheck>();

        return services;
    }

    /// <summary>
    /// Registers the catalogs of declared keys the ENGINE owns (SPEC-012 §10.6, CFG-244) — the ONE place
    /// that lists them.
    /// </summary>
    /// <remarks>
    /// Two compositions call it: the engine's own, and the composition of the auth server's
    /// configuration resolution, which a host takes without the engine's runtime services. Keeping the
    /// list here rather than repeating it there is what makes a catalog added to the engine reach both
    /// hosts. A host taking the engine WITHOUT the auth server HOLDS them all the same: the snapshot
    /// report of the resolver walks them right there, and a registrar of levels — which such a host
    /// brings itself — finds them instead of nothing. Each call carries its own per-catalog guard, so
    /// composing the engine twice — or alongside the composition of the auth server — still leaves one
    /// registration of each; a bare AddSingleton here would have the registrar walk a catalog twice
    /// and the start refused with "declared twice".
    /// </remarks>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration the core level is read from.</param>
    /// <returns>Service collection for chaining.</returns>
    internal static IServiceCollection AddTransactionEngineConfigKeyCatalogs(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddVeriqaConfigKeyCatalog(configuration, TransactionEngineConfigKeys.Catalog);
        services.AddVeriqaConfigKeyCatalog(configuration, MessageTemplateConfigKeys.Catalog);
        services.AddVeriqaConfigKeyCatalog(configuration, InitiatorContextConfigKeys.Catalog);
        services.AddVeriqaConfigKeyCatalog(configuration, IdentityMatchConfigKeys.Catalog);
        services.AddVeriqaConfigKeyCatalog(configuration, ClaimCompletionConfigKeys.Catalog);
        services.AddVeriqaConfigKeyCatalog(configuration, RestrictionPolicyConfigKeys.Catalog);

        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TResolver"/> as the <see cref="IClientTenantResolver"/> of the
    /// installation in place of the null answer the engine ships.
    /// </summary>
    /// <typeparam name="TResolver">Resolver to install.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <returns>Service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The engine registers <see cref="DefaultClientTenantResolver"/> with <c>TryAdd</c>, which is the
    /// right answer for a host using the engine WITHOUT the auth server, and the wrong one to queue
    /// behind: a component that knows better but is wired after the engine would never get a turn,
    /// because <c>TryAdd</c> is first-wins. This method displaces that shipped answer and only that
    /// one — anything else already registered is a resolver of the host, and it is left alone. So the
    /// order of preference is the same on every path: the host's resolver, then
    /// <typeparamref name="TResolver"/>, then the engine's null answer.
    /// </para>
    /// <para>
    /// A host replacing the resolver of a component that used this method needs a last-wins
    /// registration (<c>AddSingleton</c> or <c>Replace</c>), not a <c>TryAdd</c>.
    /// </para>
    /// </remarks>
    public static IServiceCollection ReplaceShippedClientTenantResolver<TResolver>(
        this IServiceCollection services)
        where TResolver : class, IClientTenantResolver
    {
        ArgumentNullException.ThrowIfNull(services);

        var shipped = services.FirstOrDefault(descriptor =>
            descriptor.ServiceType == typeof(IClientTenantResolver)
            && descriptor.ImplementationType == typeof(DefaultClientTenantResolver));

        if (shipped is not null)
        {
            services.Remove(shipped);
        }

        services.TryAddSingleton<IClientTenantResolver, TResolver>();

        return services;
    }

    /// <summary>
    /// Turns every registration of <see cref="ITransactionEventPublisher"/> present in the
    /// collection into a registration of <see cref="TransactionEventTransport"/>, preserving the
    /// lifetime of the original registration.
    /// </summary>
    /// <remarks>
    /// The interface has one slot and the fan-out has to occupy it, so the transports have to be
    /// resolvable under a type of their own — otherwise the fan-out would enumerate itself. The
    /// fan-out of an earlier call of this method is dropped rather than converted: it is the
    /// composition, not a transport, and the call in progress registers it again.
    /// </remarks>
    /// <param name="services">Service collection.</param>
    private static void CollectOutboundEventPublishers(IServiceCollection services)
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];

            // Keyed registrations are out of scope: the consumers of the engine resolve the
            // publisher by the plain service type, so a keyed one is never what they get.
            if (descriptor.ServiceType != typeof(ITransactionEventPublisher) || descriptor.IsKeyedService)
            {
                continue;
            }

            if (descriptor.ImplementationType == typeof(CompositeTransactionEventPublisher))
            {
                services.RemoveAt(index);
                continue;
            }

            var original = descriptor;

            // The container releases a service once per REGISTRATION it realizes, not once per
            // object: a publisher reachable through several registrations is released once for each
            // of them, which is why IDisposable requires the calls after the first to do nothing.
            // This rewrite replaces exactly ONE registration, so the wrapper owes exactly what that
            // one owed — no more, and no less. A factory or a type registration had its result
            // released by the container, so the wrapper releases the publisher; a ready instance was
            // never released by it, so the wrapper does not. Every other registration of the same
            // object stays untouched and keeps doing what it did.
            var ownsPublisher = original.ImplementationInstance is null;

            services[index] = ServiceDescriptor.Describe(
                typeof(TransactionEventTransport),
                provider => new TransactionEventTransport(CreatePublisher(provider, original), ownsPublisher),
                original.Lifetime);
        }
    }

    /// <summary>
    /// Produces the publisher described by a registration, whichever of the three forms it takes.
    /// </summary>
    /// <param name="provider">Service provider of the resolution in progress.</param>
    /// <param name="descriptor">The original registration.</param>
    /// <returns>The publisher to use as an outbound transport.</returns>
    private static ITransactionEventPublisher CreatePublisher(
        IServiceProvider provider,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is ITransactionEventPublisher instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (ITransactionEventPublisher)descriptor.ImplementationFactory(provider);
        }

        return (ITransactionEventPublisher)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }

    /// <summary>
    /// Wraps every registration of <see cref="ITransactionStore"/> present in the collection with the
    /// state-transition guard, preserving the lifetime of the original registration.
    /// </summary>
    /// <param name="services">Service collection.</param>
    private static void GuardTransactionStoreRegistrations(IServiceCollection services)
    {
        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];

            // Keyed registrations are out of scope: the engine and every consumer of the store
            // resolve it by the plain service type, so a keyed one is never what they get.
            if (descriptor.ServiceType != typeof(ITransactionStore) || descriptor.IsKeyedService)
            {
                continue;
            }

            var original = descriptor;

            // Same rule as for the publisher above, and for the same reason: the container releases
            // a service once per registration it realizes, this rewrite replaces exactly one
            // registration, and the guard owes exactly what that one owed. A factory or a type
            // registration had its result released by the container, so the guard releases the inner
            // store; a ready instance was never released by it, so the guard does not.
            var ownsStore = original.ImplementationInstance is null;

            services[index] = ServiceDescriptor.Describe(
                typeof(ITransactionStore),
                provider => StateTransitionGuardingTransactionStore.Wrap(CreateStore(provider, original), ownsStore),
                original.Lifetime);
        }
    }

    /// <summary>
    /// Produces the store described by a registration, whichever of the three forms it takes.
    /// </summary>
    /// <remarks>
    /// The factory replacing the registration is invoked exactly as often as the container would have
    /// created the original — the lifetime is carried over unchanged — so a singleton store is still
    /// constructed once.
    /// </remarks>
    /// <param name="provider">Service provider of the resolution in progress.</param>
    /// <param name="descriptor">The original registration.</param>
    /// <returns>The store instance to guard.</returns>
    private static ITransactionStore CreateStore(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is ITransactionStore instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (ITransactionStore)descriptor.ImplementationFactory(provider);
        }

        return (ITransactionStore)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }

    /// <summary>
    /// Checks whether a service type is already registered in the service collection.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="serviceType">Service type to look for.</param>
    /// <returns><see langword="true"/> if the type is already registered.</returns>
    private static bool HasRegistration(IServiceCollection services, Type serviceType)
    {
        // Check for an explicit registration made by the host or by a standard implementation
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == serviceType)
            {
                return true;
            }
        }

        return false;
    }
}
