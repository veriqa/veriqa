// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Configuration.Snapshot;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.DependencyInjection;

/// <summary>
/// Extensions for registering the Veriqa configuration in the DI container.
/// Registers the Options pattern with validation at application startup.
/// </summary>
internal static class VeriqaConfigurationExtensions
{
    /// <summary>
    /// Registers the configuration of the OIDC server, clients, VeriqaOptions, and rate limiting.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    internal static IServiceCollection AddVeriqaConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // The method registers the Options pattern with validation at application startup

        // Root Veriqa configuration (SPEC-012 §6.1)
        services
            .AddOptionsWithValidateOnStart<VeriqaOptions>()
            .Bind(configuration.GetSection(VeriqaOptions.SectionName));

        // Handed the configuration instance the options are bound from — the one the integrator
        // supplied — rather than whatever the container resolves for IConfiguration: the validator
        // judges the TEXT the deployment wrote for the source of a claim, and a host that supplied a
        // subsection would otherwise be judged over a root where that section is not there at all.
        services.AddSingleton<IValidateOptions<VeriqaOptions>>(
            provider => new VeriqaOptionsValidator(
                provider.GetRequiredService<IAuthPageLanguageRegistry>(),
                configuration));

        // The claims catalog section bound ON ITS OWN, for the readers that judge or serve the core
        // catalog outside the root: the rules of a client entry (SPEC-041 C30) and the core level of the
        // catalog key. Reading it off the root would make every read of the client entries validate the
        // whole root, whose validator needs services a composition without a channel does not have. The
        // text is the same the root binds, and the startup validation of the root judges it; this
        // binding is not validated a second time. Same normalization as the root post-configure.
        services
            .AddOptions<ScopesClaimsOptions>()
            .Bind(configuration.GetSection(ScopesClaimsOptions.SectionName))
            .PostConfigure(options => options.Scopes ??= []);

        // The QR group of the sign-in page section — the ONE part of Veriqa:AuthPageDesign still read as
        // an input record, and it is bound on the section it names itself rather than as a field of the
        // root class: every other key of that section is declared with the address of each of its levels
        // and read through them, so a field of the root class would bind the same section a second time
        // and stop the host on a value the resolution is required to skip silently (SPEC-012
        // CFG-240/CFG-246, see VeriqaOptions). What this binding serves is the critical check of the
        // GLOBAL section that SPEC-012 §8.2 keeps apart from the snapshot-boundary check of every level.
        services
            .AddOptionsWithValidateOnStart<QrCodeOptions>()
            .Bind(configuration.GetSection(QrCodeOptions.SectionName));

        services.AddSingleton<IValidateOptions<QrCodeOptions>, QrCodeOptionsValidator>();
        services.AddSingleton<IPostConfigureOptions<QrCodeOptions>, QrCodeOptionsPostConfigure>();

        // PostConfigure to normalize the Options before validation runs
        services.AddSingleton<IPostConfigureOptions<OidcClientsOptions>, OidcClientsOptionsPostConfigure>();
        services.AddSingleton<IPostConfigureOptions<VeriqaOptions>, VeriqaOptionsPostConfigure>();

        // OIDC server configuration (SPEC-002 §9.1)
        services
            .AddOptionsWithValidateOnStart<OidcServerOptions>()
            .Bind(configuration.GetSection(OidcServerOptions.SectionName));

        services
            .AddOptionsWithValidateOnStart<OidcClientsOptions>()
            .Bind(configuration.GetSection(OidcClientsOptions.SectionName));

        services.AddSingleton<IValidateOptions<OidcServerOptions>, OidcServerOptionsValidator>();
        services.AddSingleton<IValidateOptions<OidcClientsOptions>, OidcClientsOptionsValidator>();

        // Catalog of the client entries the configuration binder could not build and therefore dropped
        // from the effective set: the binder treats an element it cannot build as a failure of that
        // element alone, so nothing downstream of the bind above can see the loss and an entry would
        // vanish from the effective set without a single line in the log — with every setting it stated
        // silently ceding to the level below it (SPEC-012 §10.3, CFG-210). The report that walks it is
        // the common snapshot report of the configuration mechanism.
        //
        // The catalog is registered here rather than next to the other ones because it reads the RAW
        // section, and the section it must read is the one bound just above — the instance the integrator
        // handed us, not whatever the container resolves for IConfiguration. Registering it where that
        // instance is in hand is what keeps the two from drifting apart.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigSnapshotCatalog, OidcClientEntryBindingCatalog>(
                _ => new OidcClientEntryBindingCatalog(configuration)));

        // The single guarded entry point to the clients snapshot: a failed reload of one client's entry
        // must not throw out of the per-transaction and per-request reads of its neighbours (CFG-210).
        // Singleton — it owns the last valid snapshot, which must outlive a request scope.
        services.AddSingleton<OidcClientsOptionsAccessor>();

        // The OpenIddict store provider is not read from configuration by the core:
        // it is owned by the host and supplied as a delegate (SPEC-012 §5.2, CFG-117).

        // Request rate limiting configuration (SPEC-007 §6.1)
        services
            .AddOptionsWithValidateOnStart<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName));

        services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();

        // The surfaces the subject of a confirmation may be shown on (SPEC-012 §4.11). The section is
        // bound and validated here; the axis itself is a key of the canonical resolver, stated as a
        // chain and bound level by level by the ONE registrar of the catalogs below — the section is
        // the core level's source, not a second way to read the axis.
        services
            .AddOptionsWithValidateOnStart<ConfirmationSubjectDisplayOptions>()
            .Bind(configuration.GetSection(ConfirmationSubjectDisplayOptions.SectionName));

        // The validator reads the configuration the options are bound from — the one the integrator
        // supplied — rather than whatever the container resolves for IConfiguration: a host that
        // supplies a subsection would otherwise be judged on a different root.
        services.AddSingleton<IValidateOptions<ConfirmationSubjectDisplayOptions>>(
            _ => new ConfirmationSubjectDisplayOptionsValidator(configuration));

        // The node-wide settings of the hop mode (SPEC-025 §11.1): bound and validated at start, while
        // their effective values are keys of the canonical resolver (HopModeConfigKeys), registered with
        // the catalogs below — the section is the core level's source, not a second way to read them.
        services
            .AddOptionsWithValidateOnStart<HopModeOptions>()
            .Bind(configuration.GetSection(HopModeOptions.SectionName));

        // The single reading of the effective hop settings, which every slice of the hop mode takes its
        // values from (SPEC-025 HOP-033a). It reads the core level of its startup values off the
        // configuration the options are bound from — the one the integrator supplied — for the same
        // reason the validators above do. The walk of the client entries is handed in only when the
        // application level is served by the reader this composition ships (stated below, where the
        // first stated reader wins): the startup predicates then read what the entries state, and under
        // any other reader the records of the level are out of reach of startup (SPEC-025 HOP-125).
        services.TryAddSingleton(provider => new HopModeSettings(
            provider.GetRequiredService<IConfigurationResolver>(),
            provider.GetRequiredService<ConfigBindingView>(),
            provider.GetServices<CoreChannelDeclaration>(),
            configuration,
            provider.GetKeyedService<IConfigRecordReader<ConfigNode>>(ConfigLevel.Application) is ClientConfigNodeReader
                ? provider.GetRequiredService<EffectiveClientEntries>()
                : null));

        // The one codec of hop tokens (SPEC-025 HOP-034): a protected binary payload over the host's
        // Data Protection key ring, which is why the provider comes from the host, as it does for the
        // protector of identity-match expectations.
        services.TryAddSingleton<HopTokenCodec>();

        // The one answer to the hop mode of a channel for an act: the settings of a channel that starts by
        // a link, the declaration of a channel whose link carries no transaction. Scoped: the declaration
        // is read off the channel adapters, whose lifetime a host may narrow to the request.
        services.TryAddScoped<HopChannelModes>();

        // The one answer to whether the unified hop QR code applies and the channel set it is judged by
        // (SPEC-025 HOP-182), for every consumer. Scoped: it selects the set over the channel adapters,
        // whose lifetime a host may narrow to the request.
        services.TryAddScoped<HopUnifiedMode>();

        // The one step that turns a channel's deep link into the content of its QR code (SPEC-025
        // HOP-010) and issues the unified hop URL (HOP-183), shared by both projections of a QR code.
        // Scoped: it asks the unified mode above whether the unified QR code applies.
        services.TryAddScoped<HopQrContent>();

        // The unified hop QR code of the sign-in window and the channel choice page of a confirmation
        // (SPEC-025 HOP-183, HOP-187): the one decision and the one picture for both. Scoped: it issues
        // through the step above.
        services.TryAddScoped<HopUnifiedPageQr>();

        // The replacement of the hop QR codes of a page before their tokens expire, behind the method of the
        // authentication hub (SPEC-025 HOP-152). Scoped: it issues through the step above.
        services.TryAddScoped<HopQrReissue>();

        // The check of a presented hop token and the record of its refusals (SPEC-025 HOP-074, HOP-114),
        // shared by every method of the resolve route. Scoped: they read the transaction service and the
        // channel adapters, whose lifetime a host may narrow to the request.
        services.TryAddScoped<HopPresentationCheck>();
        services.TryAddScoped<HopRefusalJournal>();

        // The pages the resolve endpoint answers with. Scoped: the branding resolver they read declares the
        // external resources of the request it serves.
        services.TryAddScoped<HopResolvePages>();

        // The observations of a person's presentation (SPEC-025 §7.2 steps 4–5): the collector of the
        // scanning-device context over the reading of the request shared with the initiator context
        // (HOP-084), the one publisher of the intermediate statuses of the hop family (HOP-100) and the
        // browser mark that tells a person's own reopening of the link from one elsewhere (SPEC-041 L93),
        // all stateless. The steps themselves are scoped: they write through the transaction store, whose
        // lifetime a host may narrow to the request.
        services.TryAddSingleton<ScannerContextCollector>();
        services.TryAddSingleton<HopChannelStatusPublisher>();
        services.TryAddSingleton<HopBrowserMark>();
        services.TryAddScoped<HopObservationSteps>();

        // The observation of a superseded hop token on a person's presentation (SPEC-025 HOP-155): a log
        // entry that decides nothing. Stateless.
        services.TryAddSingleton<HopSupersededObservation>();

        // The receiver of client signals of the launch page (SPEC-025 HOP-066b, HOP-190). Scoped: it reads
        // through the check of the route and writes through the transaction store, whose lifetime a host may
        // narrow to the request.
        services.TryAddScoped<HopSignalReceiver>();

        // What the reading of the hop settings cannot repair stops the host (SPEC-025 HOP-123). The
        // validator judges the core-level facts of that reading and the per-channel values of the
        // configuration the options are bound from; it does not read the bound options back, so asking
        // it about them cannot recurse into their own validation.
        services.AddSingleton<IValidateOptions<HopModeOptions>>(provider => new HopModeOptionsValidator(
            provider.GetRequiredService<HopModeSettings>(),
            provider.GetServices<CoreChannelDeclaration>(),
            configuration));

        // Every key of the auth server is stated as a chain and enters the schema of the deployment
        // through the registrar of the catalogs below, which declares it and binds every level of it in
        // one pass — the rate limits, the token settings and the settings of the sign-in page alike.
        // The values reach both readers alike: the canonical resolver at runtime, and the synchronous
        // composition of the limiter policies, which runs before the container exists and reads the
        // same section through ConfigCoreValues.

        // The Logging section: bound and validated here, while the two keys read from it live in the
        // settings mechanism and are declared by their own catalog, which this deployment registers —
        // an opt-in audit satellite does not decide whether they are part of the schema (CFG-203).
        // The AutoLogin axis was folded into LoginConfirmationMode (TASK-046) and moved to ChannelAdapter.
        services
            .AddOptionsWithValidateOnStart<LoggingOptions>()
            .Bind(configuration.GetSection(LoggingOptions.SectionName));

        services.AddSingleton<IValidateOptions<LoggingOptions>, LoggingOptionsValidator>();

        // The application level is served by the SAME Options source as the core one — the OIDC client
        // entry lives in the application configuration. Declaring it here is what makes a host with the
        // auth server own the level, while a host without it keeps { Core } alone (SPEC-012 §10.6).
        services.AddVeriqaOptionsConfigLevels(ConfigLevel.Application);

        // The ONE walk of the entries of this level — the raw section restricted to the effective set —
        // which every reader of the level goes through: the node reader below, the walker of the
        // snapshot report and the startup report on the refused sign-in page script. It is handed the
        // configuration instance the entries are bound from — the one the integrator supplied — rather
        // than whatever the container resolves for IConfiguration, for the same reason the catalog of
        // dropped entries above is: a host that supplies a subsection would otherwise be served from a
        // different root, where the entries are not there at all.
        services.TryAddSingleton(provider => new EffectiveClientEntries(
            configuration,
            provider.GetRequiredService<OidcClientsOptionsAccessor>()));

        // The gate of the custom sign-in page script as the RESOLUTION of that script sees it — read off
        // the global section by address rather than off the bound options class, which is what the
        // startup report on a refused script judges by. It is handed the same configuration instance and
        // for the same reason as the walk above.
        services.TryAddSingleton(_ => new EffectiveCustomJsGate(configuration));

        // The NODE reader of the same entry — the single point a resolution reads this level through: a
        // setting of it is addressed inside the entry by path, whether or not the entry carries a
        // property of that name, so introducing one changes no type. It is stated AS the reader of its
        // level, and only as the shipped default: a contour that keeps the entries of this level in a
        // store of its own states its reader before this composition, and this line then yields.
        services.AddVeriqaConfigLevelNodeReader(
            ConfigLevel.Application,
            provider => new ClientConfigNodeReader(provider.GetRequiredService<EffectiveClientEntries>()));

        // The WALKER of the same entries — the other half of reading this level: the reader answers a
        // resolution with one entry, the walker hands the snapshot report all of them. It is the owner
        // of the records that enumerates them (SPEC-012 §10.6), so the catalogs of the settings stated
        // here need no class of their own.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigNodeWalker, ClientConfigNodeWalker>(
                provider => new ClientConfigNodeWalker(
                    provider.GetRequiredService<IOptionsMonitor<OidcClientsOptions>>(),
                    provider.GetRequiredService<EffectiveClientEntries>())));

        // The ui_config level is served by the SAME Options source as the core and application ones in
        // the ordinary deployment: the record catalog lives in the application configuration. The cloud
        // contour re-declares the level on its own source, and the demo sandbox introduces no source at
        // all — it substitutes the implementation of the record store behind this level (SPEC-012 §10.6).
        services.AddVeriqaOptionsConfigLevels(ConfigLevel.UiConfig);

        // The reader of the ui_config record — the single point that reaches the record store. It has
        // no extraction bindings of its own any more: every key of this level states the field of the
        // record it is read at, and the node reader below stands over that record.
        services.TryAddSingleton<UiConfigRecordReader>();

        // The NODE reader of the same record: a setting of this level that has no property on
        // UiConfigRecord is addressed inside the record by path, so introducing one does not change a
        // serialization contract that already has consumers. It reads the record THROUGH the reader
        // above rather than reaching the store a second time. Stated as the reader of its level the
        // same way the application one is: a contour substitutes the STORE behind this level
        // (IUiConfigStore) and keeps this reader, but the seam is the same for both levels.
        services.AddVeriqaConfigLevelNodeReader<UiConfigNodeReader>(ConfigLevel.UiConfig);

        // The WALKER of the same records, for the snapshot report. Whether it is the source of the
        // level at all is its own answer: a contour that substitutes the record store leaves the
        // section it reads empty and unread, and the report then NAMES the level as standing outside
        // the walk instead of counting it as walked and clean.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigNodeWalker, UiConfigNodeWalker>());

        // The keys stated as chains — the sign-in page design, the token and rate-limit settings, the
        // settings of the ui_config record: the catalogs of the owners that declare them and the ONE
        // registrar that reads them. It declares each key and binds every level of it, the core level
        // over the configuration instance the integrator supplied and a level with a record over the
        // node reader of that level.
        //
        // This is the AUTH SERVER stating its OWN catalogs, at its own composition, and no one else's:
        // the engine of transactions states its three when the engine is composed, the contour of the
        // channels states its two when the channels are, and a channel satellite states its own when
        // its channel is. Registering only what this assembly owns is what keeps the two ends honest —
        // an owner named from here would still register itself, and an owner composed after this line
        // could not be named here at all. The guard inside the call makes every order equivalent:
        // whatever the order of the compositions, each catalog ends up in the container exactly once.
        // The registrar that reads them comes with the call: a catalog states its keys, and the
        // registrar is what turns that statement into declarations and binds each level of a declared
        // key over the reader stated for that level — the two above, plus whatever a contour states
        // for a level this deployment does not have (the tenant level of the cloud contour). The
        // readers are gathered when the registrar is built, so a contour composed after this line is
        // still seen.
        services.AddVeriqaConfigKeyCatalogs(configuration, [.. VeriqaConfigKeyCatalogs.All]);

        // The core level of the claims catalog (SPEC-041 C30) has no path the registrar above could bind:
        // it is the catalog of the Veriqa:ScopesClaims section as bound, read by a getter over the section
        // bound on its own above — the same text the startup validation of the root judged.
        services.AddVeriqaConfigKeys<ScopesClaimsCoreConfigKeys>();

        // The consumer-side gathering point of the page settings: the sign-in page resolves each of its
        // level-owned values through it, once per request.
        services.TryAddSingleton<AuthPageSettingsResolver>();

        // The one reading of the configured claim levels (SPEC-041 C30, L40): the writer of the requirements
        // of a transaction and the startup report on an unobtainable phone number read the levels here.
        services.TryAddSingleton<ClaimRequirementSettings>();

        return services;
    }
}
