// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
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
    /// Registers the configuration of the OIDC server, VeriqaOptions, rate limiting and the hop mode.
    /// The client entries and the configuration resolution of the auth server are not here: they come
    /// from <see cref="AuthServerConfigResolutionExtensions.AddVeriqaAuthServerConfigResolution"/>.
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
        // judges the TEXT the deployment wrote for the claim levels of a client entry, and a host that
        // supplied a subsection would otherwise be judged over a root where that section is not there
        // at all.
        services.AddSingleton<IValidateOptions<VeriqaOptions>>(
            provider => new VeriqaOptionsValidator(
                provider.GetRequiredService<IAuthPageLanguageRegistry>(),
                configuration));

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
        services.AddSingleton<IPostConfigureOptions<VeriqaOptions>, VeriqaOptionsPostConfigure>();

        // OIDC server configuration (SPEC-002 §9.1)
        services
            .AddOptionsWithValidateOnStart<OidcServerOptions>()
            .Bind(configuration.GetSection(OidcServerOptions.SectionName));

        services.AddSingleton<IValidateOptions<OidcServerOptions>, OidcServerOptionsValidator>();

        // The client entries, their level and every reader of the configuration resolution come from
        // the composition of the resolution (AuthServerConfigResolutionExtensions), called before this
        // method.

        // The OpenIddict store provider is not read from configuration by the core:
        // it is owned by the host and supplied as a delegate (SPEC-012 §5.2, CFG-117).

        // Request rate limiting configuration (SPEC-007 §6.1)
        services
            .AddOptionsWithValidateOnStart<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName));

        services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();

        // The surfaces the subject of a confirmation may be shown on (SPEC-012 §4.11). The section is
        // bound and validated here; the axis itself is a key of the canonical resolver, stated as a
        // chain and bound level by level by the ONE registrar of the catalogs the composition of the
        // resolution registers (AuthServerConfigResolutionExtensions) — the section is the core level's
        // source, not a second way to read the axis.
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
        // the catalogs of the composition of the resolution — the section is the core level's source,
        // not a second way to read them.
        services
            .AddOptionsWithValidateOnStart<HopModeOptions>()
            .Bind(configuration.GetSection(HopModeOptions.SectionName));

        // The single reading of the effective hop settings, which every slice of the hop mode takes its
        // values from (SPEC-025 HOP-033a). It reads the core level of its startup values off the
        // configuration the options are bound from — the one the integrator supplied — for the same
        // reason the validators above do. The walk of the client entries is handed in only when the
        // application level is served by the reader the composition of the resolution ships (stated
        // there, where the first stated reader wins): the startup predicates then read what the entries
        // state, and under any other reader the records of the level are out of reach of startup
        // (SPEC-025 HOP-125).
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
        // through the registrar of the catalogs the composition of the resolution registers, which
        // declares it and binds every level of it in one pass — the rate limits, the token settings and
        // the settings of the sign-in page alike.
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

        // The gate of the custom sign-in page script as the RESOLUTION of that script sees it — read off
        // the global section by address rather than off the bound options class, which is what the
        // startup report on a refused script judges by. It is handed the same configuration instance and
        // for the same reason as the walk of the client entries (EffectiveClientEntries).
        services.TryAddSingleton(_ => new EffectiveCustomJsGate(configuration));

        // The consumer-side gathering point of the page settings: the sign-in page resolves each of its
        // level-owned values through it, once per request.
        services.TryAddSingleton<AuthPageSettingsResolver>();

        // The one reading of the configured claim levels (SPEC-041 C30, L40): the writer of the requirements
        // of a transaction and the startup report on an unobtainable phone number read the levels here.
        services.TryAddSingleton<ClaimRequirementSettings>();

        return services;
    }
}
