// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Veriqa.Core.AuthServer.ClaimCompletion;
using Veriqa.Core.AuthServer.Compatibility;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Endpoints;
using Veriqa.Core.AuthServer.InitiatorContext;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.CollectedClaims;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.DependencyInjection;

/// <summary>
/// Extensions for registering Veriqa authentication, infrastructure, and UI services.
/// </summary>
internal static class VeriqaServicesExtensions
{
    /// <summary>
    /// Registers Cookie authentication for the intermediate callback flow.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    internal static IServiceCollection AddVeriqaAuthentication(this IServiceCollection services)
    {
        // The method registers the Cookie scheme for one-time authentication between callback and authorize
        services.AddAuthentication()
            .AddCookie(OidcConstants.CookieAuthScheme, options =>
            {
                options.LoginPath = OidcEndpoints.Authorize;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(OidcConstants.AuthCookieExpireMinutes);
                // The cookie is one-time: an authorize request that keeps it (another client's request)
                // must not re-issue it with a fresh expiration past the original window.
                options.SlidingExpiration = false;
                options.Cookie.Name = OidcConstants.AuthCookieName;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.HttpOnly = true;
                // SPEC-002: unconditional Secure to protect authentication cookies
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            });

        services.AddAuthorization();

        return services;
    }

    /// <summary>
    /// Registers infrastructure services: the claims mapper, database migrations, and ClientSeeder.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    internal static IServiceCollection AddVeriqaInfrastructure(this IServiceCollection services)
    {
        // The method registers IClaimsMapper (stateless, singleton),
        // DatabaseMigrationService (EF Core migrations for relational providers: PostgreSQL/MySQL)
        // and the ClientSeeder background service.
        // DatabaseMigrationService is registered BEFORE ClientSeeder
        // so that migrations are applied before clients are created.
        //
        // TryAddSingleton, not AddSingleton: this is the shipped default of a replaceable extension
        // point. AddVeriqaAuthServer invokes the host's configure delegate BEFORE this line, so a
        // plain AddSingleton here would silently overwrite UseClaimsMapper<T>() and any direct
        // registration the host made before AddVeriqa*. A substitution made afterwards still wins —
        // it is simply the later registration of a single service.
        services.TryAddSingleton<IClaimsMapper, ClaimsMapper>();

        // Reversible protection of the relying party's expectations (SPEC-039 C23). The engine states
        // the port and compares the values; the means comes from here, because the key ring belongs to
        // the host. TryAddSingleton for the same reason as IClaimsMapper above: an integrator that
        // registered its own protector before AddVeriqa* keeps it.
        services.TryAddSingleton<IIdentityValueProtector, DataProtectionIdentityValueProtector>();
        services.AddHostedService<DatabaseMigrationService>();
        services.AddHostedService<ClientSeeder>();

        // Startup report on the effective client compatibility quirks (a relaxation in effect is a
        // deployment anomaly the operator must see). Registered after ClientSeeder so the report follows
        // the client registration lines in the log.
        services.AddHostedService<CompatibilityQuirksStartupDiagnosticsService>();

        // Startup report on the client entries whose custom sign-in page script the closed deployment
        // gate refuses (SPEC-012 §4.3): the two-step enabling would otherwise fail silently. Next to
        // the quirks report because both read the same client entries.
        services.AddHostedService<AuthPageCustomJsStartupDiagnosticsService>();

        // Startup report on browser fingerprint collection switched on where none of it can happen
        // (SPEC-025 HOP-090, HOP-123): the switch is accepted, so silence would leave the operator
        // believing the collection works.
        services.AddHostedService<HopModeStartupDiagnosticsService>();

        // The one judgement of the ways of obtaining the email (SPEC-041 C96), asked by the requirements
        // builder below, the startup check of the email requirement and the email block of the web step.
        // Singleton: it holds the resolver and the mail port of the Email adapter, both singletons.
        services.TryAddSingleton<EmailCompletionAvailability>();

        // The rules a value from the collected claims store is accepted by (SPEC-041 L58), asked by the planner
        // of the claims completion. Registered whether or not a store is: without a store nothing asks them.
        // Singleton: it holds the resolver and the judgement above, both singletons.
        services.TryAddSingleton<IStoredClaimValueRules, StoredClaimValueRules>();

        // The rules of the email block of the web step (SPEC-041 L108, E61), shared by the page that shows the
        // block and the handler of its actions. Singleton: it holds the judgement above and the localizer.
        services.TryAddSingleton<EmailCompletionBlock>();

        // The one point where the requirement set of a sign-in transaction is written, on /connect/authorize
        // (SPEC-041 C35, L48). Scoped: it reads the channel adapters, whose lifetime a host may narrow.
        services.TryAddScoped<ClaimRequirementsBuilder>();

        // The one place the avatar of the channel is asked for when the claims of a completed transaction
        // are issued — the authorization code, the link handle exchange and the confirmation token. Scoped
        // for the same reason as the requirements builder above: it reads the channel adapters.
        services.TryAddScoped<ChannelAvatarSupplement>();

        // The web step of the claims completion (SPEC-041 L53): the starter the engine asks when the step
        // begins chooses and writes the surface the step is shown on, and sends the link button into the chat
        // when that surface is the device that confirmed the sign-in. Added as one more starter rather than in
        // place of one: the engine picks the starter of each step, and the last registered wins. Scoped for the
        // same reason as the requirements builder above: it reads the channel adapters; the engine resolves
        // it from a scope of its own.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IClaimCompletionStepStarter, WebStepClaimCompletionStarter>());

        // The model of the form of the web step (SPEC-041 L108): built here, next to the catalog of the claims,
        // for every page that shows the form, whichever assembly renders it. Scoped for the same reason as
        // the requirements builder above: it reads the channel adapters.
        services.TryAddScoped<IClaimCompletionFormModelBuilder, ClaimCompletionFormModelBuilder>();

        // The page of the claims completion on the device the sign-in was started on and the one check of the
        // right of its actions (SPEC-041 L108, C39, N78), both read by the callback handlers. Scoped: the page
        // uses the form model builder above.
        services.TryAddSingleton<ClaimCompletionPageCarry>();
        services.TryAddScoped<ClaimCompletionPageResponder>();
        services.TryAddScoped<ClaimCompletionActionRight>();

        // The page of the completion on the device that confirmed the sign-in, opened by the confirmation page
        // of the mail (SPEC-041 L52, N78): it issues the form token there. Reached through the port in
        // ChannelAdapter.UI, since the page of the mail lives in an assembly without a reference to this one.
        // Scoped: it answers with the page responder above.
        services.TryAddScoped<IClaimCompletionCrossDevicePage, ClaimCompletionCrossDevicePage>();

        // Startup report on a phone number required where no registered channel can provide it (SPEC-041
        // R20): the requirement is accepted, so silence would leave the operator to learn it from refused
        // sign-ins.
        services.AddHostedService<ClaimRequirementsStartupDiagnosticsService>();

        // Startup refusal of an email required where no method of its list is available (SPEC-041 L92,
        // C96): such a requirement would be lowered on every sign-in without a word, so the start stops.
        services.AddHostedService<EmailCompletionStartupValidator>();

        // The QR pixel scales outside the range their setting admits are reported by the common
        // snapshot report of the configuration mechanism, over the catalogs registered with it
        // (SPEC-012 §8.2, CFG-152/CFG-154) — no report of its own is registered here.

        // Initiator context check (SPEC-017): context collector,
        // UA normalizer, offline GeoIP, and configuration diagnostics at startup
        services.AddSingleton<IUserAgentNormalizer, UaParserUserAgentNormalizer>();

        // The GeoIP default is a null object: this package ships no GeoIP library, because the
        // .mmdb database a lookup needs is not shipped either. A real provider arrives with the
        // Veriqa.Core.AuthServer.MaxMind satellite through AddMaxMindGeoIp().
        // TryAddSingleton, not AddSingleton, for the same reason as IClaimsMapper above — and here
        // it also makes the satellite win in both reachable orders: registered before this line it
        // is left alone, registered after it is the later registration of a single service.
        services.TryAddSingleton<IGeoIpProvider, UnavailableGeoIpProvider>();

        // The reading of the observable request attributes (IP, User-Agent, GeoIP, RawMetadata), shared by
        // the initiator context and the scanner context of the hop mode (SPEC-025 HOP-084).
        services.AddSingleton<RequestContextCapture>();
        services.AddSingleton<IInitiatorContextCollector, InitiatorContextCollector>();
        services.AddHostedService<InitiatorContextStartupDiagnosticsService>();

        return services;
    }

    /// <summary>
    /// Registers UI services: QR generation, channel data, the page renderer, and the SignalR event handler (SPEC-007).
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    internal static IServiceCollection AddVeriqaUiServices(this IServiceCollection services)
    {
        // The method registers all UI services for the authentication page

        // QR code generation (SPEC-007 §3.1, UI-013). One instance behind both faces: the public PNG
        // service and the internal module-matrix seam the confirmation answer draws its image from, so
        // the two cannot resolve the scale or pick the error correction level differently.
        services.AddSingleton<QrCodeService>();
        services.AddSingleton<IQrCodeService>(serviceProvider => serviceProvider.GetRequiredService<QrCodeService>());
        services.AddSingleton<IQrModuleMatrixSource>(serviceProvider => serviceProvider.GetRequiredService<QrCodeService>());

        // Channel data preparation (deep link + QR) (SPEC-007 §3, §4), over the one filter and order of the
        // channels of a display list, which the unified hop QR code judges its channel set by as well
        // (SPEC-025 HOP-182). Scoped: the filter reads the channel adapters, whose lifetime a host may
        // narrow to the request.
        services.TryAddScoped<ChannelSetFilter>();
        services.AddScoped<IChannelDisplayService, ChannelDisplayService>();

        // The one context of a page of a live transaction that never leaves itself — the entry page of a
        // confirmation (SPEC-039 C15) and the scanner page of the unified hop QR code (SPEC-025 HOP-146).
        // Scoped: it states the external resources of the page in the resource scope of the request.
        services.TryAddScoped<TransactionPageContextBuilder>();

        // The one point the lines a core page shows for a channel are resolved at — messages of the template
        // mechanism for the sign-in window, the entry page of a confirmation and the launch page of the hop.
        // Scoped, as its callers are: it reads the canonical resolver, whose lifetime a host may narrow.
        services.TryAddScoped<CorePageChannelLineResolver>();

        // Data-driven registry of the languages the auth window can render (SPEC-007 §12, UI-080).
        // Singleton: both inputs are process-constant, the sets are built once per process.
        services.TryAddSingleton<IAuthPageLanguageRegistry, AuthPageLanguageRegistry>();

        // Authentication page renderer (SPEC-007 §7, UI-050).
        // The built-in renderer is registered as its own type as well: the scanner page of the hop always
        // renders with it (SPEC-025 HOP-197), whatever renderer UseAuthPageRenderer<T>() substitutes for the
        // sign-in window and the confirmation entry page. The interface resolves to the same instance.
        // TryAddSingleton for the same reason as IClaimsMapper above: the shipped default must not
        // overwrite UseAuthPageRenderer<T>() called from the configure delegate.
        services.TryAddSingleton<DefaultAuthPageRenderer>();
        services.TryAddSingleton<IAuthPageRenderer>(
            serviceProvider => serviceProvider.GetRequiredService<DefaultAuthPageRenderer>());

        // Branding of the generated core pages and the resource scope of a request. Both live in the
        // channel contour, which owns the render points, and are registered here as well because a
        // host may run the auth server without a single channel adapter — one shared method, so the
        // two roots cannot drift apart.
        services.AddCorePageUiServices();

        // The texts of the pages this server owns that an owning level may override are registered by the
        // composition of the configuration resolution (AuthServerConfigResolutionExtensions): the snapshot
        // report of the text key needs them wherever the resolution is composed.

        // SignalR transaction event handler (SPEC-007 §5)
        services.AddSingleton<ITransactionEventHandler, SignalRTransactionEventHandler>();

        return services;
    }
}
