// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.Metrics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Diagnostics;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.ChannelAdapter.Webhooks;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.ChannelAdapter.DependencyInjection;

/// <summary>
/// Extension methods for registering channel adapters in the DI container.
/// </summary>
public static class ChannelAdapterServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Veriqa channel adapter system.
    /// If no channel is enabled (all Enabled=false), the server starts
    /// without active adapters — CA-110 issues a warning, not an exception,
    /// so that profiles without channel configuration (e.g. demo-core) start correctly.
    /// </summary>
    /// <remarks>
    /// The channel contour is NOT a deployment form of its own, and nothing registered here stands in
    /// for a contour the deployment did not compose. A host that calls this method and nothing else
    /// compiles and builds a container, but it composes no transaction engine — so it has nothing to
    /// confirm.
    /// <para>
    /// The keys this contour OWNS travel with it: the call below registers their catalogs, and the
    /// reader of catalogs comes with that registration, so their levels are bound wherever the contour
    /// is composed. The one key the contour merely READS is the inbound-verification axis
    /// (<see cref="ChannelInboundVerificationConfigKeys"/>), whose catalog belongs to the auth server —
    /// and the contour REFUSES to start a deployment that enabled a channel without declaring that
    /// value. So a host composing the channels without the auth server states that one catalog itself,
    /// explicitly (<c>AddVeriqaConfigKeyCatalogs</c>), which is the whole of what a deployment composing
    /// the auth server gets from it. Such hosts exist outside tests, and a host that skips this reads
    /// none of the values it wrote for that axis.
    /// </para>
    /// </remarks>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="configure">Adapter configuration action.</param>
    /// <returns>Service collection for call chaining.</returns>
    public static IServiceCollection AddVeriqaChannelAdapters(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<ChannelAdapterBuilder> configure)
    {
        // Report on a channel configuration left at the top-level "Channels" key. Registered
        // unconditionally: the very case it has to catch is "no channel is enabled BECAUSE the
        // configuration stayed at the old path", where nothing else in the pipeline speaks up.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, LegacyChannelsSectionStartupDiagnosticsService>());

        // The two halves of a channel disagreeing — an adapter with no core-level declaration, or a
        // declaration reporting a channel enabled that no adapter serves (SPEC-003 CA-198/CA-199). Reported
        // and never fatal. Registered BEFORE the inbound-verification pass below, because that pass
        // refuses the start of a deployment that enabled a channel without declaring the verification
        // of its inbound events — and a channel enabled with no adapter behind it walks straight into
        // that refusal, which alone tells the operator nothing about the missing half.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ChannelDeclarationStartupDiagnosticsService>());

        // Declared inbound verification of every channel enabled at core level (SPEC-003
        // CA-196/CA-197): a channel switched on without a declared value stops the host, and a polling
        // channel declaring something other than an outbound fetch is warned about. Registered
        // unconditionally and idempotently: it walks whatever channels registered a
        // CoreChannelDeclaration, so a deployment that added none is checked in zero steps.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ChannelInboundVerificationStartupValidator>());

        // The catalogs this CONTOUR owns, stated at its own composition — the channel capability, the
        // design of the pages of the core, the login-confirmation axis, the display of the outcome
        // receipt, the window of the phone step of claim completion, the limit and interval of the
        // channel health probe, and the spare start code and the name of the chat of a channel. They
        // travel with the channels because that is where they are declared: a host that
        // composes the channels without the auth server carries their declarations all the same, and
        // nothing else in such a host would name them. The call guards each catalog on its own, so
        // composing the contour twice, or alongside another owner naming a catalog it already holds,
        // still leaves one registration of each. The keys of an INDIVIDUAL channel are not here: each
        // channel registers its own catalog at its own registration, so a deployment declares the
        // credentials of the channels it added and of no others.
        services.AddVeriqaConfigKeyCatalogs(
            configuration,
            ChannelConfigKeys.Catalog,
            CorePageBrandingConfigKeys.Catalog,
            LoginConfirmationConfigKeys.Catalog,
            CompletionBehaviorConfigKeys.Catalog,
            OutcomeNoticeConfigKeys.Catalog,
            ClaimCompletionConfigKeys.Catalog,
            ChannelHealthConfigKeys.Catalog,
            ChannelChatConfigKeys.Catalog);

        // The text overrides of the pages of the core, declared next to their design and for the same
        // reason. Their key brings its own snapshot catalogs — one per level, which the mechanism does not
        // derive for a key without a domain — so it is registered by its own call, under one guard for the
        // key and its catalogs.
        services.AddCorePageTextKey(configuration);

        // Metrics of the contour. AddMetrics is the framework's own way of bringing IMeterFactory in
        // and is idempotent, so it is called here rather than assumed: an integrator may consume this
        // assembly without ServiceDefaults, and a missing factory would then fail at resolve time.
        services.AddMetrics();
        // Built by a factory rather than by type: the constructor is internal on purpose (recording
        // into a series integrators build dashboards on is the component's own business), and the
        // container only ever calls public constructors.
        services.TryAddSingleton(provider => new ChannelAdapterMetrics(
            provider.GetRequiredService<IMeterFactory>()));

        // Channel track multi-tenancy seam (SPEC-003 §17.4):
        // the default credential provider (N=1 via the TASK-040 resolver), the per-tenant client
        // factory, the channels_enabled resolver. Without a mandatory external dependency (invariant-1);
        // the tenant-level Cloud provider is added opt-in on top (invariant-2).
        services.AddChannelMultiTenancy();

        // Create the builder and hand it to the user for configuration
        var builder = new ChannelAdapterBuilder(services, configuration);
        configure(builder);

        // Clock of the channel contour: token TTL, correlation expiry and prompt retention read it
        // instead of the static system clock. Registered by the component that consumes it rather
        // than inherited from a neighbour. The slot is normally already taken by the resolver inside
        // AddChannelMultiTenancy above, so this line is the guarantee that the contour resolves a
        // clock at all, not the point where the provider is chosen: a host substituting the provider
        // registers it before AddVeriqaChannelAdapters, not inside the configure callback. TryAdd is
        // idempotent, so the instance stays single.
        services.TryAddSingleton(TimeProvider.System);

        // Login confirmation settings (SPEC-012 §4.4): a single LoginConfirmationMode axis, cut by the
        // channel — the plain Mode and the per-channel ModeByChannel map next to it. The key states the
        // section and the members its core level is read at, so no getter of its own is registered here;
        // the options binding below stays what the rest of the contour reads the section through.
        services.Configure<LoginConfirmationOptions>(
            configuration.GetSection(LoginConfirmationOptions.SectionName));

        // Display of the outcome receipt: the desired intent of the deployment, which is the core level
        // of a key a tenant and an application may override. Bound the same way the login-confirmation
        // section is — the key states the section and the member its core level is read at, and this
        // binding is what the section is read through outside that resolution.
        services.Configure<OutcomeNoticeOptions>(
            configuration.GetSection(OutcomeNoticeOptions.SectionName));

        // Channel message localization (SPEC-017 §7.2, ICC-050):
        // resolving Natural Keys into the recipient's language from the host's locale files
        services.Configure<ChannelLocalizationOptions>(
            configuration.GetSection(ChannelLocalizationOptions.SectionName));

        // A locale tag that denotes no locale, and a regional mapping nothing looks up, are both
        // invisible at run time — the messages simply come out in the base wording and the dates in
        // the conventions the operator meant to replace. Judged at start instead.
        services.AddSingleton<IValidateOptions<ChannelLocalizationOptions>, ChannelLocalizationOptionsValidator>();
        services.AddOptionsWithValidateOnStart<ChannelLocalizationOptions>();

        // Start of a sign-in by a short code, for a channel that declared ChannelCapabilities.CodeStart:
        // the length of the code is the one setting, judged at start — a value outside the range
        // would otherwise surface only when the first page of such a channel is rendered. The codes
        // live in a store of their own (in-process by default, replaceable for several instances), and
        // the issuer is the single point a code is given to a transaction at.
        services.Configure<ChannelCodeStartOptions>(
            configuration.GetSection(ChannelCodeStartOptions.SectionName));
        services.AddSingleton<IValidateOptions<ChannelCodeStartOptions>, ChannelCodeStartOptionsValidator>();
        services.AddOptionsWithValidateOnStart<ChannelCodeStartOptions>();
        services.TryAddSingleton<IChannelStartCodeStore, InMemoryChannelStartCodeStore>();
        services.TryAddSingleton<IChannelStartCodeIssuer, ChannelStartCodeIssuer>();

        // Dropping of a repeated delivery of an inbound event within a short window, before the processor
        // acts on it: a platform that delivers one event twice almost at once would otherwise get two
        // processings. The window is judged at start — a negative one would silently read as "off". The
        // marks live in this process only, so the deduplicator is a singleton shared by every event scope.
        services.Configure<ChannelInboundDeduplicationOptions>(
            configuration.GetSection(ChannelInboundDeduplicationOptions.SectionName));
        services.AddSingleton<IValidateOptions<ChannelInboundDeduplicationOptions>, ChannelInboundDeduplicationOptionsValidator>();
        services.AddOptionsWithValidateOnStart<ChannelInboundDeduplicationOptions>();
        services.TryAddSingleton<InboundEventDeduplicator>();

        // The single point at which the render points of the channel contour obtain a message — its
        // contract and its template ladder, each through the canonical resolver, so a deployment's
        // variants reach the rendered text instead of being validated at startup and then ignored
        // (SPEC-036 TPL-056).
        services.TryAddSingleton<IMessageTemplateAccessor, MessageTemplateAccessor>();

        // The narrow public accessor channel adapters take the templates of their own messages from:
        // one instance for every channel — the channel is read off the adapter passed in, the rest of
        // the address off the transaction — resolving through the accessor above.
        services.TryAddSingleton<IChannelMessageTemplates, ChannelMessageTemplates>();

        services.TryAddSingleton<IConfirmationPromptLocalizer, LocaleFileConfirmationPromptLocalizer>();

        // Terminal text of a transaction (SPEC-036 TPL-123, SPEC-039 R51): the ONE port every render
        // point of an outcome takes its wording from.
        services.TryAddSingleton<IOutcomeReceiptText, OutcomeReceiptTextResolver>();

        // Confirmation context (SPEC-017 §7): the context factory, the prompt orchestrator
        // and the no-op anomaly heuristic (replaced by the host when a history source exists, ICC-072)
        services.TryAddSingleton<IInitiatorAnomalyDetector, NoOpInitiatorAnomalyDetector>();
        services.TryAddSingleton<IConfirmationPromptContextFactory, ConfirmationPromptContextFactory>();
        services.TryAddSingleton<IConfirmationPromptOrchestrator, ConfirmationPromptOrchestrator>();

        // Expiry notice in the channel (SPEC-003 §4.5): the store where the core records the chat a
        // transaction was started from, with the references to the sent prompt and to a phone number
        // request, and the lifecycle handler that, on TransactionExpiredEvent, reports "expired" into
        // that chat. The same record addresses the receipt of a sign-in window cancel. The default store
        // is in-process (single instance / self-hosted / demo); a shared one is registered by
        // UseRedisChannelStores.
        services.TryAddSingleton<IChannelPromptMessageStore, InMemoryChannelPromptMessageStore>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITransactionEventHandler, ChannelPromptExpiryHandler>());

        // Deep-link prefill context (SPEC-003 §10.4): the ownership levels and the expiry of the transaction
        // the Email channel builds its Push mail for. A concrete internal collaborator of the channel
        // contour, registered by its own type — there is no interface to substitute, so the context can
        // only come from the stored transaction.
        services.TryAddSingleton<DeepLinkPrefillContextReader>();

        // Branding of the generated service pages and the resource scope of a request. The auth
        // server registers the very same services (a host may run it with no adapter enabled), so
        // the pair lives in one method both composition roots call — the reasoning is there.
        services.AddCorePageUiServices();

        // Effective confirmation surface resolution (SPEC-012 §4.4.2): the layer resolver →
        // expansion of the ChannelDefault sentinel from the channel's single confirmation fact →
        // routing of an in-channel confirmation the channel cannot perform to the core's surface.
        services.TryAddSingleton<IEffectiveConfirmationSurfaceResolver, EffectiveConfirmationSurfaceResolver>();

        // Post-completion action resolution (SPEC-012 §4.9): the layer resolver asked about the
        // channel → clamp of an action the channel cannot render to the channel's default. The journal
        // is the one collaborator a legitimate host may lack; the resolver then only logs the clamp.
        services.TryAddSingleton<ICompletionBehaviorResolver, CompletionBehaviorResolver>();

        // Post-completion action of a sign-in (SPEC-012 §4.9): the one performer every path that
        // completes a sign-in calls — the processing of a channel event through the processor below,
        // the web paths by taking it from the container.
        services.TryAddSingleton<ICompletionBehaviorDispatcher, CompletionBehaviorDispatcher>();

        // Outcome of a claim completion ended outside a channel event (SPEC-003 CA-142): the web paths
        // that end the completion take it from the container, like the performer above, and call it
        // before that performer — the post-completion action follows the receipt.
        services.TryAddSingleton<IClaimCompletionOutcomeReporter, ClaimCompletionOutcomeReporter>();

        // Receipt of the cancel button of a page in the chat where the question of the transaction still
        // stands — "Cancel sign-in" of the sign-in window, "Cancel confirmation" of the entry page of a
        // confirmation, and "Cancel sign-in" or "Cancel confirmation" of the scanner page of the unified hop
        // QR code: both cancel paths take it from the container, each beside the reporter above, which
        // covers a sign-in cancelled in the claims completion. It reads the record of the chat the expiry
        // handler reads.
        services.TryAddSingleton<ISignInCancelChannelReporter, SignInCancelChannelReporter>();

        // Receipt of the "Yes" and the "No" of the web confirmation page in the chat the notice of the
        // hand-over to that page went to (SPEC-012 CFG-266): both answers of the page take it from the
        // container, like the two reporters above. It reads the channel snapshot of the transaction and
        // keeps no record of its own.
        services.TryAddSingleton<IWebConfirmationReceiptReporter, WebConfirmationReceiptReporter>();

        // Display intent of the outcome receipt: the ONE port both points that build a notice — the
        // live processing of an event and the background expiry handler — take the resolved intent from.
        services.TryAddSingleton<IOutcomeNoticeDisplayIntentSource, OutcomeNoticeDisplayIntentSource>();

        // Phone step of claim completion (SPEC-041 L44): the chat → transaction record of the last
        // phone number request sent into a chat — the answer to the request names no transaction —
        // and the one seam claim completion delivers its messages into the user's chat through. The
        // default store is in-process; UseRedisChannelStores replaces it for several instances.
        services.TryAddSingleton<IPhoneRequestCorrelationStore, InMemoryPhoneRequestCorrelationStore>();

        // Link handles of post-completion buttons: fingerprint of the handle → the sign-in it stands
        // for. The default store is in-process; UseRedisChannelStores replaces it for several instances.
        services.TryAddSingleton<ILinkHandleStore, InMemoryLinkHandleStore>();
        services.TryAddSingleton<IClaimCompletionChannelDelivery, ClaimCompletionChannelDelivery>();

        // The phone step itself: the one builder of the request, and the starter the engine asks when
        // the step begins — it asks the channel of the transaction for the number and ends the wait of
        // an Optional one when its window runs out. Added as one more starter rather than in place of
        // one: the engine picks the starter of each step, and the last registered wins, so a component
        // extending the phone step registers its own after this one.
        services.TryAddSingleton<PhoneNumberRequestBuilder>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IClaimCompletionStepStarter, PhoneStepStarter>());

        // The message a sign-in sends into the chat when its question is handed over to the core page
        // (SPEC-012 §4.4.2): the sender has just written to the bot and is told to look at the window.
        services.TryAddSingleton<WebConfirmationNoticeSender>();

        // The processing of an inbound channel event — the one every path an event arrives by drives.
        // Scoped, because a collaborator of it may be scoped in the host (an integrator's identity
        // resolution), and built by a factory, because its constructor is internal. What it cannot work
        // without is taken with GetRequiredService — the three collaborators of the in-chat question
        // included, which this very method registers above; the rate limiter and the journal are the
        // two a legitimate host may lack (they come with the auth server and the transaction engine).
        // The handler of menu item presses is the host's own and optional. The processor gets where to
        // take it from rather than the handler itself: it is created in the scope of the event (so
        // whatever lifetime the host chose for it is honoured) and only when an item is pressed — a
        // handler that fails to be created must not take the sign-ins of the channel down with it. The mark
        // of the letters whose budget is charged comes with the channel that sends such letters, and a
        // composition without one charges every pass. The dropping of repeated deliveries is the one
        // singleton every scope shares: its marks must outlive the event that set them.
        services.TryAddScoped(provider => new ChannelInboundProcessor(
            provider.GetRequiredService<ITransactionService>(),
            provider.GetRequiredService<ITransactionStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IIdentityResolutionService>(),
            provider.GetRequiredService<ILogger<ChannelInboundProcessor>>(),
            provider.GetRequiredService<ChannelAdapterMetrics>(),
            provider.GetService<IUserAuthRateLimiter>(),
            provider.GetRequiredService<IConfirmationPromptOrchestrator>(),
            provider.GetRequiredService<IConfirmationPromptLocalizer>(),
            provider.GetRequiredService<IChannelPromptMessageStore>(),
            provider.GetRequiredService<IOutcomeReceiptText>(),
            provider.GetRequiredService<IOutcomeNoticeDisplayIntentSource>(),
            provider.GetService<ITransactionEventPublisher>(),
            provider.GetServices<CoreChannelDeclaration>(),
            provider.GetRequiredService<IPhoneRequestCorrelationStore>(),
            provider.GetRequiredService<PhoneNumberRequestBuilder>(),
            provider.GetRequiredService<IClaimCompletionChannelDelivery>(),
            provider.GetRequiredService<ICompletionBehaviorDispatcher>(),
            provider.GetRequiredService<WebConfirmationNoticeSender>(),
            () => provider.GetService<IMenuItemPressHandler>(),
            provider.GetRequiredService<IChannelStartCodeStore>(),
            provider.GetService<ILetterBudgetChargeMark>(),
            provider.GetRequiredService<InboundEventDeduplicator>()));

        // Which channels still have no webhook registered: written by the background registration of a
        // channel that registers its own webhook, read by the health check below.
        services.TryAddSingleton<ChannelWebhookRegistrationState>();

        // Channel state on the standard health-checks surface: one check asking every registered
        // adapter the very question its own contract answers. It is registered here, in the channel
        // contour's composition root, so a host only has to publish an endpoint selecting it by tag.
        //
        // failureStatus is Degraded on purpose, and it does not merely repeat the ceiling the check
        // body already holds: should an exception escape that body (resolving the check's own
        // dependencies, cancellation of the linked source), the health check service takes the status
        // from the registration rather than from the body — and the general health endpoint, which
        // aggregates every check without a tag filter, would then answer 503 on a replica that is
        // perfectly fine.
        //
        // The guard keeps the registration idempotent. AddVeriqaChannelAdapters may run more than once
        // over one collection (host + extension library), while check names have to be unique: a second
        // registration under the same name makes the health check service throw when it is built.
        if (services.All(descriptor => descriptor.ServiceType != typeof(ChannelAdapterHealthCheck)))
        {
            services.AddSingleton<ChannelAdapterHealthCheck>();

            // The very same instance also runs as a hosted service, and only to take the first
            // snapshot of the channels at start-up: the check never probes a channel from the
            // request that reads its report, so without a start-up round the first reader would see
            // the not-answered-yet placeholder instead of the channel states.
            services.AddHostedService(provider => provider.GetRequiredService<ChannelAdapterHealthCheck>());

            services.AddHealthChecks()
                .AddCheck<ChannelAdapterHealthCheck>(
                    name: ChannelHealthCheckNames.Name,
                    failureStatus: HealthStatus.Degraded,
                    tags: [ChannelHealthCheckNames.Tag]);
        }

        // CA-110: a warning if no channel is enabled.
        // No exception is thrown — the AuthServer may start without channel adapters
        // for development/demo scenarios (e.g. the demo-core profile).
        //
        // The builder flag alone is not the whole answer: HasAdapters is internal and only the
        // builder's own AddChannel/AddXxx methods raise it, so an adapter registered straight into
        // the (public) service collection — by an ecosystem package extending this builder, or by
        // the host itself — would leave the flag down and produce a warning about an empty channel
        // set while the container already holds one. The collection scan is the same technique the
        // transaction engine uses for its store and publisher defaults.
        if (!builder.HasAdapters
            && !services.Any(descriptor => descriptor.ServiceType == typeof(IChannelAdapter)))
        {
            // Log a startup warning via deferred logging
            services.AddHostedService<ChannelAdapterStartupWarningService>();
        }

        return services;
    }
}
