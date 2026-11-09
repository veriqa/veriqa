// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Configuration.Snapshot;
using Veriqa.Core.AuthServer.Endpoints;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.AuthServer.UiConfig;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.DependencyInjection;

namespace Veriqa.Core.AuthServer.DependencyInjection;

/// <summary>
/// The configuration resolution of the auth server, composed on its own (SPEC-012 §10.6, CFG-231,
/// CFG-235): the canonical resolver with the levels the auth server serves, their readers and walkers,
/// and the key catalogs a deployment with the auth server holds — without the OIDC server, its
/// authentication, SignalR, the pages and the endpoints.
/// </summary>
public static class AuthServerConfigResolutionExtensions
{
    /// <summary>
    /// Registers the canonical resolver with the auth server's levels (Application, UiConfig), their
    /// readers and walkers and the auth server's key catalogs — without the OIDC server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resolution is the same one <see cref="OpenIddictIntegrationExtensions.AddVeriqaAuthServer"/>
    /// serves, because that registration takes it from here: the levels, the readers and walkers of
    /// their records, the catalogs of the auth server, of the transaction engine and of the channel
    /// contour, and the texts of the pages of the server an owning level may override. The catalogs of
    /// an INDIVIDUAL channel are not part of it: a channel brings its own when it is registered.
    /// </para>
    /// <para>
    /// A contour that serves a level from a store of its own states its reader
    /// (<c>AddVeriqaConfigLevelNodeReader</c>) or its <see cref="IUiConfigStore"/> BEFORE this call: the
    /// readers and the store registered here only take a slot nobody has claimed.
    /// </para>
    /// <para>
    /// The call is idempotent: a repeated call — including the one the auth server's own registration
    /// makes — adds nothing, and the configuration of the first call stays in effect. The composition
    /// expects a Generic or Web Host: the post-configure step of the client entries reads the
    /// application lifetime.
    /// </para>
    /// </remarks>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">
    /// Application configuration — the instance the levels, the catalogs and the client entries are
    /// read from.
    /// </param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddVeriqaAuthServerConfigResolution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The composition holds registrations that are not idempotent on their own — the bindings of
        // options, their validators and post-configure steps, the contributions of overridable texts —
        // so the guard stands over the whole of it: the marker is registered once, with the first call,
        // and a call that finds it adds nothing.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(CompositionMarker)))
        {
            return services;
        }

        services.AddSingleton(new CompositionMarker());

        // The canonical resolver itself. A host with the engine or the channels already has it; a host
        // that takes only this composition has nothing else that would register it. Idempotent.
        services.AddVeriqaConfigurationResolver();

        AddClaimsCatalog(services, configuration);
        AddClientEntries(services, configuration);
        AddApplicationLevel(services, configuration);
        AddUiConfigLevel(services, configuration);
        AddKeyCatalogs(services, configuration);
        AddOverridablePageTexts(services);

        return services;
    }

    /// <summary>
    /// Registers the claims catalog section bound on its own and the startup check of that catalog.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    private static void AddClaimsCatalog(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(ScopesClaimsOptions.SectionName);

        // The claims catalog section bound ON ITS OWN, for the readers that judge or serve the core
        // catalog outside the root: the rules of a client entry (SPEC-041 C30) and the core level of the
        // catalog key. Reading it off the root would make every read of the client entries validate the
        // whole root, whose validator needs services a composition without a channel does not have. This
        // default instance is NOT validated: its readers read it while the client entries are being
        // built, and a failure here would surface inside them, once per reader.
        BindCatalog(services.AddOptions<ScopesClaimsOptions>());

        // The startup check of the same catalog runs on a NAMED instance of the same section — the same
        // binding and normalization, judged at start by a validator that answers for this name alone.
        // The catalog stops the start as a section of its own, the way every other section validated
        // on start does, and the readers of the default instance above never meet the failure.
        BindCatalog(
            services.AddOptionsWithValidateOnStart<ScopesClaimsOptions>(
                ScopesClaimsCatalogValidator.CheckedInstanceName));

        // Handed the configuration instance the options are bound from — the one the integrator supplied
        // — rather than whatever the container resolves for IConfiguration: the validator judges the TEXT
        // the deployment wrote, and a host that supplied a subsection would otherwise be judged over a
        // root where that section is not there at all.
        services.AddSingleton<IValidateOptions<ScopesClaimsOptions>>(
            _ => new ScopesClaimsCatalogValidator(configuration));

        // The ONE binding both instances share — the section and the normalization of the root
        // post-configure — so the text the startup check judges and the catalog its readers read cannot
        // drift apart.
        void BindCatalog(OptionsBuilder<ScopesClaimsOptions> builder) =>
            builder.Bind(section).PostConfigure(options => options.Scopes ??= []);
    }

    /// <summary>
    /// Registers the OIDC client entries: their binding, normalization and validation, the catalog of
    /// the entries the binder dropped and the guarded accessor of the snapshot.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    private static void AddClientEntries(IServiceCollection services, IConfiguration configuration)
    {
        // PostConfigure to normalize the Options before validation runs
        services.AddSingleton<IPostConfigureOptions<OidcClientsOptions>, OidcClientsOptionsPostConfigure>();

        services
            .AddOptionsWithValidateOnStart<OidcClientsOptions>()
            .Bind(configuration.GetSection(OidcClientsOptions.SectionName));

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
    }

    /// <summary>
    /// Registers the application level: its source, the walk of the client entries, and the reader and
    /// walker of the level over that walk.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    private static void AddApplicationLevel(IServiceCollection services, IConfiguration configuration)
    {
        // The application level is served by the SAME Options source as the core one — the OIDC client
        // entry lives in the application configuration. Declaring it here is what makes a host that
        // takes this composition own the level, while a host without it keeps { Core } alone (SPEC-012
        // §10.6).
        services.AddVeriqaOptionsConfigLevels(ConfigLevel.Application);

        // The ONE walk of the entries of this level — the raw section restricted to the effective set —
        // which every reader of the level goes through: the node reader below, the walker of the
        // snapshot report and the startup report on the refused sign-in page script. It is handed the
        // configuration instance the entries are bound from — the one the integrator supplied — rather
        // than whatever the container resolves for IConfiguration, for the same reason the catalog of
        // dropped entries is: a host that supplies a subsection would otherwise be served from a
        // different root, where the entries are not there at all.
        services.TryAddSingleton(provider => new EffectiveClientEntries(
            configuration,
            provider.GetRequiredService<OidcClientsOptionsAccessor>()));

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
    }

    /// <summary>
    /// Registers the ui_config level: the record catalog and its store, the source of the level, and the
    /// readers and walker of its records.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    private static void AddUiConfigLevel(IServiceCollection services, IConfiguration configuration)
    {
        // The ui_config record + self-hosted store (SPEC-012 CFG-203, SPEC-002 §4.6). The store is
        // claimed with TryAdd: a store a contour registered before this composition stays.
        services.AddVeriqaUiConfig(configuration);

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
    }

    /// <summary>
    /// Registers the key catalogs of the auth server and, through the methods of their owners, those of
    /// the transaction engine and of the channel contour.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    private static void AddKeyCatalogs(IServiceCollection services, IConfiguration configuration)
    {
        // The keys stated as chains — the sign-in page design, the token and rate-limit settings, the
        // settings of the ui_config record: the catalogs of the owners that declare them and the ONE
        // registrar that reads them. It declares each key and binds every level of it, the core level
        // over the configuration instance the integrator supplied and a level with a record over the
        // node reader of that level.
        //
        // The catalogs of the AUTH SERVER are named by the list this assembly owns. The catalogs of the
        // engine and of the channel contour are NOT listed here: each owner keeps its list in one method,
        // which its own composition calls as well — so a catalog an owner adds reaches a host that takes
        // only this composition without a second list to forget. The guard inside every registration
        // makes every order equivalent: whatever the order of the compositions, each catalog ends up in
        // the container exactly once. The registrar that reads them comes with the call: a catalog states
        // its keys, and the registrar is what turns that statement into declarations and binds each level
        // of a declared key over the reader stated for that level — the two above, plus whatever a
        // contour states for a level this deployment does not have (the tenant level of the cloud
        // contour). The readers are gathered when the registrar is built, so a contour composed after
        // this line is still seen.
        services.AddVeriqaConfigKeyCatalogs(configuration, [.. VeriqaConfigKeyCatalogs.All]);
        services.AddTransactionEngineConfigKeyCatalogs(configuration);
        services.AddChannelContourConfigKeyCatalogs(configuration);

        // The core level of the claims catalog (SPEC-041 C30) has no path the registrar above could bind:
        // it is the catalog of the Veriqa:ScopesClaims section as bound, read by a getter over the default
        // instance of the section bound on its own — the same text the startup check of the catalog judged
        // on its named instance.
        services.AddVeriqaConfigKeys<ScopesClaimsCoreConfigKeys>();
    }

    /// <summary>
    /// Registers the texts of the pages of the server an owning level may override — without them the
    /// snapshot report of the text key refuses every override as a text no page shows.
    /// </summary>
    /// <param name="services">Service collection.</param>
    private static void AddOverridablePageTexts(IServiceCollection services)
    {
        // The texts of the pages this server owns that an owning level may override (SPEC-012 §4.3): the
        // sign-in window — both surfaces that render it and the statuses of its script — the block of
        // the start by a code those surfaces show, the two launch pages of the hop, the "session expired"
        // page of the callback, the auto-posting page of the form_post response and the title and answers of
        // the "Yes"/"No" question page of both types (its question is a message of the template mechanism,
        // as are the lines of the channels of the pages), the title and the instruction of the form of the
        // claims completion, and the action under an outcome the scanner page, the launch page and the
        // receipts end on. The other pages of the server keep their texts. These are lists of strings,
        // not the services of the pages.
        services.AddCorePageOverridableTexts(
        [
            .. AuthPageStrings.OverridableTexts,
            .. ChannelStartCodeBlock.OverridableTexts,
            .. HopInterstitialPage.OverridableTexts,
            .. HopCodeStartPage.OverridableTexts,
            .. CallbackExpiredPage.OverridableTexts,
            .. FormPostResponsePage.OverridableTexts,
            .. WebConfirmPage.OverridableTexts,
            .. ClaimCompletionPage.OverridableTexts,
            .. OutcomeCloseAction.OverridableTexts
        ]);

        // The name and the two phrases of the button of every registered channel but Email, so a channel a
        // deployment plugs in is renamed and reworded by the same entries as a built-in one (SPEC-007 UI-121,
        // SPEC-012 CFG-254). Built by a factory: the adapters are known to the built provider only. A host
        // without channels gets an empty contribution.
        services.AddSingleton<CorePageOverridableTextContribution>(RegisteredChannelTexts);
    }

    /// <summary>
    /// Builds the overridable texts of the registered channels: for each adapter but Email, the key of its name
    /// (<see cref="ChannelNames.NameKeyOf"/>) and the phrases of its button
    /// (<see cref="AuthPageStrings.ChannelPhrases"/>), from the label the adapter declared. A channel whose texts
    /// carry the path separator of the configuration is left out — no level addresses an entry for such a
    /// text — with one warning; its name is still translated by the locale files.
    /// </summary>
    /// <param name="provider">Root service provider.</param>
    /// <returns>The contribution of the registered channels.</returns>
    private static CorePageOverridableTextContribution RegisteredChannelTexts(IServiceProvider provider)
    {
        var logger = provider.GetRequiredService<ILogger<CorePageOverridableTexts>>();
        var texts = new List<string>();

        // The adapters may be registered with a lifetime narrower than the process, so they are read inside a
        // scope of their own. None of them depends on the overridable texts, so there is no cycle.
        using var scope = provider.CreateScope();

        foreach (var adapter in scope.ServiceProvider.GetServices<IChannelAdapter>())
        {
            var displayName = ChannelNames.ReadDeclaredMetadata(adapter, logger).DisplayName;

            if (ChannelNames.NameKeyOf(adapter.ChannelType, displayName) is not { } nameKey)
            {
                continue;
            }

            IReadOnlyList<string> channelTexts =
            [
                nameKey,
                .. AuthPageStrings.ChannelPhrases(ChannelNames.GetChannelName(adapter.ChannelType, displayName))
            ];

            if (channelTexts.Any(text => text.Contains(ConfigNode.PathSeparator)))
            {
                logger.LogWarning(
                    "The name of channel {ChannelType} carries '{PathSeparator}' — its name and button texts "
                    + "cannot be overridden by {TextsKey}; the locale files still translate the name",
                    adapter.ChannelType,
                    ConfigNode.PathSeparator,
                    CorePageTextConfigKeys.KeyName);
                continue;
            }

            texts.AddRange(channelTexts);
        }

        return new CorePageOverridableTextContribution(texts);
    }

    /// <summary>
    /// Marker of a composition already registered — what makes a repeated call add nothing.
    /// </summary>
    private sealed class CompositionMarker;
}
