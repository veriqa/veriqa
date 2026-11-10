// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Endpoints;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.AuthServer.Hubs;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.CollectedClaims;
using Veriqa.Core.TransactionEngine.DependencyInjection;

namespace Veriqa.Core.AuthServer.DependencyInjection;

/// <summary>
/// Facade for registering the entire Veriqa OpenIddict integration in the DI container.
/// Coordinates the calls to the specialized extension classes.
/// </summary>
public static class OpenIddictIntegrationExtensions
{
    /// <summary>
    /// High-level method for wiring up Veriqa AuthServer as an embeddable library.
    /// Registers all required services: OpenIddict, authentication, infrastructure,
    /// UI services, and rate limiting. Supports replaceable extension points via the builder.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="environment">Host environment.</param>
    /// <param name="configure">Optional configuration callback (channel adapters, Transaction Engine, extension points).</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaAuthServer(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        Action<VeriqaAuthServerBuilder>? configure = null)
    {
        // The method registers the full Veriqa AuthServer via VeriqaAuthServerBuilder
        var builder = new VeriqaAuthServerBuilder(services, configuration, environment);

        // The user configures the Transaction Engine, channels, and extension points
        configure?.Invoke(builder);

        // Clock of the auth server: the initiator context snapshot, the polled status TTL and the
        // status the hub replays to a fresh subscriber read it instead of the static system clock.
        // Registered by the component that consumes it, and after the callback, so a host that
        // supplied its own provider there keeps it — here that ordering is real, nothing before this
        // line claims the slot. Kept in this facade rather than pushed
        // down into AddVeriqaOpenIddict the way IClientTenantResolver is: every consumer of the clock
        // is reached only through a surface that also takes an engine service — ITransactionService
        // for the endpoints, ITransactionStore for the hub — so a host wiring the OIDC integration on
        // its own has the engine, and the clock the engine registers, anyway.
        // TryAdd is idempotent, so the engine and the channel contour still share the single instance.
        services.TryAddSingleton(TimeProvider.System);

        // If the Transaction Engine was not configured explicitly — wire it up with the default in-memory store
        if (!builder.TransactionEngineConfigured)
        {
            services.AddVeriqaTransactionEngine(configuration);
        }

        // Main registration of the OpenIddict integration.
        // The database delegate (if any) is supplied through the builder: UseOpenIddictDatabase(...)
        services.AddVeriqaOpenIddict(configuration, environment, builder.OpenIddictDatabaseConfigurator);

        // If the channel adapters were not configured explicitly — register without adapters (with a warning)
        if (!builder.ChannelAdaptersConfigured)
        {
            services.AddVeriqaChannelAdapters(configuration, _ => { });
        }

        // Rate limiting (endpoint-level policies + per-user limiter)
        services.AddVeriqaRateLimiting();

        return services;
    }

    /// <summary>
    /// Puts the Veriqa AuthServer middleware into the pipeline, in the order the server requires:
    /// security headers, static files, authentication, authorization, rate limiting. The third part
    /// of the facade, next to <see cref="AddVeriqaAuthServer"/> and <see cref="MapVeriqaAuthServer"/>.
    /// </summary>
    /// <param name="app">Application pipeline builder.</param>
    /// <returns>The builder for chaining.</returns>
    /// <remarks>
    /// One step of the sequence is Veriqa's own (<c>UseSecurityHeaders</c>); the other four are stock
    /// ASP.NET Core. What the method really carries is the ordering constraint: the security headers
    /// must come BEFORE the static files and the routing, or the responses served by the static-file
    /// middleware leave without a CSP (SPEC-007 section 6.3). Stated as a comment in a host's
    /// <c>Program.cs</c>, that constraint is a thing to remember; stated here, it is code.
    /// <para>
    /// The sequence stays open: building it by hand remains valid and is what a host with its own
    /// middleware between the steps does — the shipped standalone host inserts
    /// <c>UseForwardedHeaders</c>, and a site contour also adds <c>UseRouting</c> and
    /// <c>UseRequestLocalization</c>. Use this method when nothing of yours goes between the steps,
    /// and write the five calls out when something does.
    /// </para>
    /// </remarks>
    public static IApplicationBuilder UseVeriqaAuthServer(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Security headers: CSP (with nonce), X-Frame-Options, X-Content-Type-Options.
        // BEFORE the static files and the routing — this is the ordering the method exists for.
        app.UseSecurityHeaders();

        // Static assets of the sign-in page (the SignalR client, localization).
        app.UseStaticFiles();

        app.UseAuthentication();
        app.UseAuthorization();

        // Rate limiting: after routing, before the endpoints are executed.
        app.UseRateLimiter();

        return app;
    }

    /// <summary>
    /// High-level method for registering all Veriqa AuthServer endpoints.
    /// Registers the OIDC endpoints, the SignalR hub and the endpoints of the registered channels —
    /// their webhooks plus whatever routes a channel maps for itself (a page of its own, an inbound
    /// route outside the path convention). Which channel contributes what is the channel's business:
    /// the auth server only hands out the rate-limit policies it defined for each kind of channel route
    /// (<see cref="ChannelEndpointPolicies"/>).
    /// </summary>
    /// <param name="endpoints">Endpoint router.</param>
    /// <returns>Router for chaining.</returns>
    public static IEndpointRouteBuilder MapVeriqaAuthServer(this IEndpointRouteBuilder endpoints)
    {
        // The method registers all public Veriqa AuthServer endpoints
        endpoints.MapVeriqaOidcEndpoints();
        endpoints.MapChannelWebhookEndpoints(RateLimitOptions.ChannelEndpointPolicies);

        return endpoints;
    }


    /// <summary>
    /// Registers all services of the OpenIddict integration: configuration, EF Core, OpenIddict,
    /// Cookie authentication, infrastructure, and UI services.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="environment">Host environment (used to select certificates).</param>
    /// <param name="configureDatabase">
    /// Optional action configuring the EF Core provider of the OpenIddict store. The provider
    /// choice, connection string and migrations assembly belong to the caller (host-owned,
    /// SPEC-001 §10.2):
    /// <code>
    /// services.AddVeriqaOpenIddict(configuration, environment, ef => ef.UseNpgsql(connectionString,
    ///     npgsql => npgsql.MigrationsAssembly("Veriqa.Core.AuthServer.Migrations.PostgreSql")));
    /// </code>
    /// A relational provider configured without a migrations assembly leaves MigrateAsync without
    /// migrations to apply (standard EF Core behaviour). When <see langword="null"/> the volatile
    /// InMemory provider is used — a choice that has to be spoken through
    /// <see cref="UseInMemoryOpenIddictStore"/>, otherwise the host fails to start outside the
    /// Development environment (SPEC-012 CFG-118, CFG-119).
    /// </param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaOpenIddict(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        Action<DbContextOptionsBuilder>? configureDatabase = null)
    {
        // The method performs the full registration of the OpenIddict integration via the specialized classes

        // The configuration resolution of the auth server — the resolver with its levels, their readers
        // and walkers, the client entries and the key catalogs. It is the one copy of these registrations
        // for both entry points and for a host that takes the resolution alone; idempotent, so a host
        // that composed it before this call keeps what it composed.
        services.AddVeriqaAuthServerConfigResolution(configuration);

        services.AddVeriqaConfiguration(configuration);

        // Tenant of the calling client: the TenantId of the client's own configuration entry.
        // Registered HERE rather than in AddVeriqaAuthServer because this call is the one point BOTH
        // entry points pass through — the facade, and a host wiring the OIDC integration itself, which
        // is what the shipped Veriqa.Core.AuthServer.Host does AFTER the Transaction Engine. It also
        // sits next to the composition of the resolution, where the OidcClientsOptionsAccessor it reads
        // through comes from. The registration displaces the engine's shipped null answer instead of
        // queueing behind it, so the auth server answers wherever it is present and no matter when the
        // engine was wired; a resolver of the host is left untouched and still wins over both.
        services.ReplaceShippedClientTenantResolver<OidcClientTenantResolver>();

        services.AddVeriqaDbContext(configureDatabase);
        services.AddVeriqaOpenIddictServer(configuration, environment);
        services.AddVeriqaAuthentication();
        services.AddVeriqaInfrastructure();

        // Pruning of the OpenIddict token and authorization records, on every store provider.
        // Registered after the infrastructure, so it starts once the database migration has run.
        // The clock is claimed with TryAdd: this service is reached without an engine surface, and a
        // clock already registered — by the host, the engine or the facade — is kept.
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<OpenIddictTokenPruneService>();

        // SignalR (SPEC-007 §5)
        services.AddSignalR();

        // AntiForgery for the forms of the core pages: the question page (OnWebPage, SPEC-007 UI-038,
        // UI-123, CFG-045) and the cancel form of the sign-in window and of the entry page of a confirmation.
        // The token is validated manually in the endpoint (IAntiforgery.ValidateRequestAsync) — without depending
        // on the UseAntiforgery middleware in the host pipeline.
        // The cookie of the token goes out SameSite=Lax rather than the framework's Strict. A page of the core
        // a user reaches by a navigation started on another site — the page of a web provider channel back
        // from the provider, a page opened from a webmail — issues tokens; a Strict cookie is not sent with
        // that navigation, so a fresh one replaces it and every form of the core already open in another tab
        // of the same browser fails as out of date. A cross-site POST still carries no Lax cookie, and the
        // token is checked as before. A host that states the options after this call keeps its own value.
        services.AddAntiforgery(options => options.Cookie.SameSite = SameSiteMode.Lax);

        services.AddVeriqaUiServices();

        return services;
    }

    /// <summary>
    /// Explicitly selects the volatile EF Core InMemory store for the OpenIddict data
    /// (clients, tokens, authorizations) for hosts that call <see cref="AddVeriqaOpenIddict"/>
    /// directly, without the auth-server builder — the shipped standalone host does exactly that.
    /// The builder method of the same name delegates here, so the marker of the spoken choice has a
    /// single home and the startup check answers the same way on both entry points
    /// (SPEC-012 CFG-118, CFG-119).
    /// Intended for development, samples and tests only: the data does not survive a restart and is
    /// not shared between replicas. Without this call and without a configured EF Core provider the
    /// host fails to start outside the Development environment.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection UseInMemoryOpenIddictStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The marker only records that the choice was spoken; the store itself is registered by
        // AddVeriqaOpenIddict, which falls back to InMemory when no provider delegate is supplied.
        // TryAdd, so that repeating the call registers nothing twice.
        services.TryAddSingleton<VolatileOpenIddictStoreOptIn>();

        return services;
    }

    /// <summary>
    /// Registers rate limiting for Veriqa: every endpoint-level policy named by
    /// <see cref="RateLimitOptions"/> plus the per-user authentication rate limiter (SPEC-007 §6.1).
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddVeriqaRateLimiting(this IServiceCollection services)
    {
        // The method registers AddRateLimiter with OnRejected and the endpoint-level policies.
        // Source of truth: RateLimitOptions (read via IOptions in AddOptions.Configure).

        // Per-user auth rate limiter (PartitionedRateLimiter, in-memory, single-instance). Its partition
        // registry reads the clock; this method is public and a host may call it on its own, so the clock
        // is claimed here with TryAdd, keeping one already registered by the host or another component.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IUserAuthRateLimiter, UserAuthRateLimiter>();

        // AddRateLimiter: OnRejected — the shared handler for limit violations
        services.AddRateLimiter(options =>
        {
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                // RFC 6749 §5.2: token endpoint responses (including errors) must contain
                // Cache-Control: no-store and Pragma: no-cache. Set them for all policies
                // so /connect/token stays RFC-compliant when the rate limit is hit.
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                context.HttpContext.Response.Headers.Pragma = "no-cache";

                // For /connect/token, an endpoint-level rate limit hit returns
                // a JSON body in the RFC 6749 §5.2 format so OIDC clients are not broken.
                if (context.HttpContext.Request.Path.StartsWithSegments(OidcEndpoints.Token, StringComparison.OrdinalIgnoreCase))
                {
                    context.HttpContext.Response.ContentType = "application/json; charset=UTF-8";
                    await context.HttpContext.Response.WriteAsync(
                        RateLimitOptions.TokenRateLimitErrorJson,
                        cancellationToken);
                    return;
                }

                await context.HttpContext.Response.WriteAsync(RateLimitOptions.RateLimitExceededMessage, cancellationToken);
            };
        });

        // Endpoint-level policies are configured via AddOptions.Configure for a single source of truth
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<RateLimitOptions>, TimeProvider>((limiterOptions, rateLimitOpts, timeProvider) =>
            {
                var config = rateLimitOpts.Value;

                // Re-leveling of the limit VALUES and WINDOWS through the resolver (CFG-223):
                // both the permit limits and the window lengths are read via the resolver (single point),
                // not via a direct config.X. The machinery (FixedWindow/partitions/policies) stays in the
                // core and is NOT split across levels (CFG-230).
                // LIMITATION: the RateLimiter policies are registered ONCE by a SYNCHRONOUS callback of
                // the framework, so only the Core level is available here — and it is pure memory, which
                // is why it can be read without awaiting anything (see ConfigCoreValues). A
                // per-tenant/per-app ceiling on these limits at startup is unattainable anyway: it would
                // require multi-tenant rate-limiting, which is not supported.
                var rateLimitCore = RateLimitCoreValues.Declare(config);

                // Limit on transaction creation (the authorize endpoint)
                limiterOptions.AddFixedWindowLimiter(RateLimitOptions.AuthorizePolicyName, o =>
                {
                    o.PermitLimit = rateLimitCore.Read(RateLimitConfigKeys.AuthorizePermitLimit);
                    o.Window = TimeSpan.FromSeconds(rateLimitCore.Read(RateLimitConfigKeys.AuthorizeWindowSeconds));
                    o.QueueLimit = 0;
                });

                // Limit on SignalR connections
                limiterOptions.AddSlidingWindowLimiter(RateLimitOptions.SignalRPolicyName, o =>
                {
                    o.PermitLimit = rateLimitCore.Read(RateLimitConfigKeys.SignalRPermitLimit);
                    o.Window = TimeSpan.FromSeconds(rateLimitCore.Read(RateLimitConfigKeys.SignalRWindowSeconds));
                    o.SegmentsPerWindow = 6;
                    o.QueueLimit = 0;
                });

                // Limit on webhook endpoints, partitioned by the tenant segment of the route (SPEC-003
                // CA-133): the inbound traffic of one tenant does not spend the budget of another, nor that
                // of the route without a segment, which is one partition of its own. A segment is whatever
                // the caller wrote — the demux refuses an unknown tenant only after the limiter — so the
                // number of partitions is bounded by WebhookMaxPartitions (read directly, like
                // UserAuthMaxPartitions, CFG-230): new segments beyond it share one overflow partition.
                var webhookPermitLimit = rateLimitCore.Read(RateLimitConfigKeys.WebhookPermitLimit);
                var webhookWindow =
                    TimeSpan.FromSeconds(rateLimitCore.Read(RateLimitConfigKeys.WebhookWindowSeconds));
                var webhookPartitions = new PartitionKeyRegistry(config.WebhookMaxPartitions, timeProvider);

                limiterOptions.AddPolicy(
                    RateLimitOptions.WebhookPolicyName,
                    httpContext => RateLimitPartition.GetFixedWindowLimiter(
                        WebhookPartitionKeyOf(httpContext, webhookPartitions, webhookWindow),
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = webhookPermitLimit,
                            Window = webhookWindow,
                            QueueLimit = 0
                        }));

                // Limit on the Token Endpoint (POST /connect/token)
                limiterOptions.AddFixedWindowLimiter(RateLimitOptions.TokenPolicyName, o =>
                {
                    o.PermitLimit = rateLimitCore.Read(RateLimitConfigKeys.TokenPermitLimit);
                    o.Window = TimeSpan.FromSeconds(rateLimitCore.Read(RateLimitConfigKeys.TokenWindowSeconds));
                    o.QueueLimit = 0;
                });

                // Limit on the Callback Endpoint (GET /connect/authorize/callback)
                limiterOptions.AddFixedWindowLimiter(RateLimitOptions.CallbackPolicyName, o =>
                {
                    o.PermitLimit = rateLimitCore.Read(RateLimitConfigKeys.CallbackPermitLimit);
                    o.Window = TimeSpan.FromSeconds(rateLimitCore.Read(RateLimitConfigKeys.CallbackWindowSeconds));
                    o.QueueLimit = 0;
                });

                // Limit on the Polling Endpoint (GET /api/transaction/{id}/status) — SPEC-007 §5.4
                limiterOptions.AddFixedWindowLimiter(RateLimitOptions.PollingPolicyName, o =>
                {
                    o.PermitLimit = rateLimitCore.Read(RateLimitConfigKeys.PollingPermitLimit);
                    o.Window = TimeSpan.FromSeconds(rateLimitCore.Read(RateLimitConfigKeys.PollingWindowSeconds));
                    o.QueueLimit = 0;
                });

                // Limit on the Email Start Endpoint (POST /auth/email/start) — spam protection
                limiterOptions.AddFixedWindowLimiter(RateLimitOptions.EmailStartPolicyName, o =>
                {
                    o.PermitLimit = rateLimitCore.Read(RateLimitConfigKeys.EmailStartPermitLimit);
                    o.Window = TimeSpan.FromSeconds(rateLimitCore.Read(RateLimitConfigKeys.EmailStartWindowSeconds));
                    o.QueueLimit = 0;
                });

                // Limit on the sign-in routes of the web provider channels — a bucket of its own, so that a
                // sign-in through a web provider does not spend the budget of the mails of the Email channel.
                limiterOptions.AddFixedWindowLimiter(RateLimitOptions.WebProviderPagesPolicyName, o =>
                {
                    o.PermitLimit = rateLimitCore.Read(RateLimitConfigKeys.WebProviderPagesPermitLimit);
                    o.Window = TimeSpan.FromSeconds(
                        rateLimitCore.Read(RateLimitConfigKeys.WebProviderPagesWindowSeconds));
                    o.QueueLimit = 0;
                });

                // Limit on the pages the links of the Email mails open — the magic-link page and the page
                // of a link of the claims-completion mail. It runs before the handler, so a rejected
                // request never reaches the read of the token.
                limiterOptions.AddFixedWindowLimiter(RateLimitOptions.EmailLinkPagesPolicyName, o =>
                {
                    o.PermitLimit = rateLimitCore.Read(RateLimitConfigKeys.EmailLinkPagesPermitLimit);
                    o.Window = TimeSpan.FromSeconds(
                        rateLimitCore.Read(RateLimitConfigKeys.EmailLinkPagesWindowSeconds));
                    o.QueueLimit = 0;
                });

                // Limit on the BROWSER pages of the confirmation vertical — the entry page and the
                // question with its two answers. One budget over both: they are one path a user walks.
                // It is a bucket of its own rather than one of the policies above, because those are
                // the sign-in path's and confirmation traffic spending them would slow down signing in.
                limiterOptions.AddFixedWindowLimiter(RateLimitOptions.ConfirmationPagesPolicyName, o =>
                {
                    o.PermitLimit = rateLimitCore.Read(RateLimitConfigKeys.ConfirmationPagesPermitLimit);
                    o.Window = TimeSpan.FromSeconds(
                        rateLimitCore.Read(RateLimitConfigKeys.ConfirmationPagesWindowSeconds));
                    o.QueueLimit = 0;
                });

                // Limit on the hop resolve endpoint (GET {HopPath}/{hop_token}, SPEC-025 HOP-061) — one
                // global bucket of the route: the request carries only an opaque token, and the limit
                // is what protects its decoding. Defined whether or not the route is mapped, as every
                // policy above is.
                limiterOptions.AddFixedWindowLimiter(RateLimitOptions.HopResolvePolicyName, o =>
                {
                    o.PermitLimit = rateLimitCore.Read(RateLimitConfigKeys.HopResolvePermitLimit);
                    o.Window = TimeSpan.FromSeconds(
                        rateLimitCore.Read(RateLimitConfigKeys.HopResolveWindowSeconds));
                    o.QueueLimit = 0;
                });

                // Limit on the receiver of client signals of the launch page (POST {HopPath}/{hop_token},
                // SPEC-025 HOP-061, HOP-066b) — a bucket of its own, apart from the presentation's, with
                // the same kind of key: an exhausted budget of the receiver refuses signals only.
                limiterOptions.AddFixedWindowLimiter(RateLimitOptions.HopSignalPolicyName, o =>
                {
                    o.PermitLimit = rateLimitCore.Read(RateLimitConfigKeys.HopSignalPermitLimit);
                    o.Window = TimeSpan.FromSeconds(
                        rateLimitCore.Read(RateLimitConfigKeys.HopSignalWindowSeconds));
                    o.QueueLimit = 0;
                });

                // Limit on the server-to-server confirmation creation endpoint, partitioned by the
                // AUTHENTICATED CLIENT (SPEC-039 N33). The policies above are global because their
                // callers are browsers; here the callers are relying parties, and a shared budget
                // would let one of them stop the others. The IP key of SPEC-007 §6 is not carried
                // over: a relying party is a server, and its address says nothing about who calls.
                var confirmationPermitLimit = rateLimitCore.Read(RateLimitConfigKeys.ConfirmationCreatePermitLimit);
                var confirmationWindow =
                    TimeSpan.FromSeconds(rateLimitCore.Read(RateLimitConfigKeys.ConfirmationCreateWindowSeconds));

                limiterOptions.AddPolicy(
                    RateLimitOptions.ConfirmationCreatePolicyName,
                    httpContext => RateLimitPartition.GetFixedWindowLimiter(
                        // The authorization middleware runs BEFORE the limiter, so an unauthenticated
                        // request never reaches this line and the partition table is bounded by the
                        // configured clients. The shared partition is the answer to a route that would
                        // ever be mapped without the policy requiring a client token.
                        ClientCredentialsToken.TryGetClientId(httpContext.User, out var clientId)
                            ? clientId
                            : string.Empty,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = confirmationPermitLimit,
                            Window = confirmationWindow,
                            QueueLimit = 0
                        }));

                // Limit on the authenticated result surface, partitioned by the same key and given a
                // budget of its own (SPEC-039 N36): reading results and creating transactions are two
                // different rhythms, and a shared budget would let either starve the other.
                var resultPermitLimit = rateLimitCore.Read(RateLimitConfigKeys.ConfirmationResultPermitLimit);
                var resultWindow =
                    TimeSpan.FromSeconds(rateLimitCore.Read(RateLimitConfigKeys.ConfirmationResultWindowSeconds));

                limiterOptions.AddPolicy(
                    RateLimitOptions.ConfirmationResultPolicyName,
                    httpContext => RateLimitPartition.GetFixedWindowLimiter(
                        ClientCredentialsToken.TryGetClientId(httpContext.User, out var clientId)
                            ? clientId
                            : string.Empty,
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = resultPermitLimit,
                            Window = resultWindow,
                            QueueLimit = 0
                        }));
            });

        return services;
    }

    /// <summary>
    /// Registers all OIDC endpoints via Minimal API and the SignalR hub (SPEC-007 §5.2).
    /// Applies rate limiting to authorization and SignalR (SPEC-007 §6.1).
    /// </summary>
    /// <param name="endpoints">Endpoint router.</param>
    /// <returns>Router for chaining.</returns>
    public static IEndpointRouteBuilder MapVeriqaOidcEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // The method registers all OIDC endpoints and the SignalR hub with rate limiting
        endpoints.MapAuthorizeEndpoint();
        endpoints.MapAuthorizeCallbackEndpoint(RateLimitOptions.CallbackPolicyName);
        endpoints.MapTokenEndpoint(RateLimitOptions.TokenPolicyName);
        endpoints.MapUserInfoEndpoint();
        // The revocation endpoint (/connect/revoke) is handled by OpenIddict natively — no passthrough,
        // no ASP.NET Core endpoint mapping is required (the OpenIddict middleware returns 200 OK per RFC 7009).

        // Transaction status polling endpoint for fallback clients (SPEC-007 §5.4)
        endpoints.MapTransactionStatusEndpoint(RateLimitOptions.PollingPolicyName);

        // Server-to-server creation of a confirmation transaction (SPEC-039 C14), limited per
        // authenticated client (N33)
        endpoints.MapConfirmationTransactionEndpoint(RateLimitOptions.ConfirmationCreatePolicyName);

        // Authenticated result surface of a confirmation transaction (SPEC-039 C24): the verdict of
        // the identity match lives here and NOT on the anonymous status route, which is addressed by
        // an identifier every holder of the way in knows (N35).
        endpoints.MapTransactionResultEndpoint(RateLimitOptions.ConfirmationResultPolicyName);

        // Server-to-server deletion of collected claims (SPEC-041 C82): mapped only where a collected claims
        // store is registered. Without one there is nothing to delete, and the path answers as any unknown one.
        var registeredServices = endpoints.ServiceProvider.GetRequiredService<IServiceProviderIsService>();
        if (registeredServices.IsService(typeof(ICollectedClaimsStore)))
        {
            endpoints.MapCollectedClaimsEndpoint();
        }

        // Server-to-server exchange of a link handle for the identity of its sign-in: mapped where a link
        // handle store is registered. The channel layer registers one together with its other stores, so
        // every installation with channels has the route; only a host composed without that layer has no
        // store — there no handle is ever issued, and the path answers as any unknown one. It shares the
        // budget of the result surface — the same caller reading the outcome of its own transactions.
        if (registeredServices.IsService(typeof(ILinkHandleStore)))
        {
            endpoints.MapLinkHandleRedeemEndpoint(RateLimitOptions.ConfirmationResultPolicyName);
        }

        // Entry page of an already created confirmation transaction (SPEC-039 C15) and the surface that
        // asks the subject about it and takes the answer (SPEC-039 R37) — the browser pages of the
        // confirmation vertical. ONE policy over both: they are one path a user walks, and it is a
        // budget of their own rather than one of the sign-in path's, which their traffic would
        // otherwise exhaust — an observable change of a path these surfaces must not touch.
        endpoints.MapTransactionEntryEndpoint(RateLimitOptions.ConfirmationPagesPolicyName);
        endpoints.MapTransactionConfirmEndpoint(RateLimitOptions.ConfirmationPagesPolicyName);

        // Hop resolve endpoint (SPEC-025 HOP-060, HOP-125): mapped only where hop can be on — at the core
        // level for a registered channel, or at a level bound above it, which startup cannot see. Where
        // it is off everywhere the route does not exist, and its path answers as any unknown one does.
        // A channel whose link carries no transaction maps it as well, when the channel can be available
        // to a layer: the QR code of such a channel is a hop URL whatever the switch states, and the route
        // is where that URL leads. A channel of this kind that is switched off is issued no hop URL and
        // maps nothing.
        var hopModeSettings = endpoints.ServiceProvider.GetRequiredService<HopModeSettings>();
        var codeChannelServed = HopChannelModes.IsCodeChannelServed(endpoints.ServiceProvider, hopModeSettings);
        if (hopModeSettings.IsResolveEndpointMapped || codeChannelServed)
        {
            endpoints.MapHopResolveEndpoint(hopModeSettings.HopPath, RateLimitOptions.HopResolvePolicyName);
        }

        // Receiver of client signals of the launch page (SPEC-025 HOP-066b): the same route, mapped only
        // where signals can be accepted — at the core level, or at a level bound above it. Where they are
        // off without such a level the path answers POST as a route without the method does (405). The
        // page of a channel whose link carries no transaction sends the same signals, so the receiver
        // follows the route such a channel maps.
        if (hopModeSettings.IsSignalReceiverMapped
            || (codeChannelServed && hopModeSettings.MayAcceptClientSignals))
        {
            endpoints.MapHopSignalEndpoint(hopModeSettings.HopPath, RateLimitOptions.HopSignalPolicyName);
        }

        // Authentication SignalR hub (SPEC-007 §5.2) with rate limiting (SPEC-007 §6.1)
        endpoints.MapHub<AuthTransactionHub>(SignalRConstants.AuthHubPath)
            .RequireRateLimiting(RateLimitOptions.SignalRPolicyName);

        return endpoints;
    }

    /// <summary>
    /// The partition of the webhook policy a request falls into: its tenant segment of the route, bounded by
    /// the registry of partitions, or — for the route without a segment — the one partition of that route,
    /// which the registry does not hold, so segments written by callers never push it into the overflow.
    /// A blank segment counts as none, as in <see cref="ChannelTenantDemux"/>, which serves it as the default
    /// tenant.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="partitions">Registry of the partitions of the tenant segments.</param>
    /// <param name="window">Window of the policy — the idle time after which a segment leaves the registry.</param>
    /// <returns>The partition key; empty — the route without a segment.</returns>
    private static string WebhookPartitionKeyOf(
        HttpContext httpContext,
        PartitionKeyRegistry partitions,
        TimeSpan window) =>
        httpContext.Request.RouteValues.TryGetValue(ChannelTenantDemux.TenantRouteKey, out var segment)
            && segment is string tenant
            && !string.IsNullOrWhiteSpace(tenant)
                ? partitions.Resolve(tenant, window)
                : string.Empty;
}
