// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

// Entry point of the Veriqa Core Auth Server Host.
// Standalone host for running the Veriqa.Core.AuthServer library.

using System.Net.Mime;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using StackExchange.Redis;
using Veriqa.Core.AuditTrail.DependencyInjection;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.DependencyInjection;
using Veriqa.Core.AuthServer.Host;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.DependencyInjection;
using Veriqa.ServiceDefaults;

return HostBootstrap.Run("Veriqa Auth Server terminated with a critical error", () =>
{
    // Whether this process runs under a service manager is decided BEFORE the builder is created: the
    // content root cannot be changed afterwards (MS Docs, "Host ASP.NET Core in a Windows Service"), and
    // under a service manager the working directory of the process is not the directory of the program
    // (for a Windows service it is System32), while the localization files and the static content are
    // read relative to the content root.
    var isOsService = WindowsServiceHelpers.IsWindowsService() || SystemdHelpers.IsSystemdService();

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        // Outside a service manager the content root keeps its default resolution, so Docker, `dotnet run`
        // and a run from the IDE behave exactly as before.
        ContentRootPath = isOsService ? AppContext.BaseDirectory : null
    });

    // Lifetime under a service manager: the Windows service control handler and the systemd readiness
    // notification (Type=notify). Both calls check for themselves whether the process runs under that
    // manager and do nothing when it does not — they are safe on either OS.
    builder.Services.AddWindowsService();
    builder.Services.AddSystemd();

    // The external configuration directory of an OS-service installation (/etc/veriqa,
    // %ProgramData%\Veriqa): its files layer over the appsettings the program ships with and stay under
    // the environment variables — added before anything reads the configuration.
    builder.AddExternalConfigurationDirectory();

    // The integrator's own settings file (clients and any other Veriqa setting), layered over the shipped
    // appsettings files and the configuration directory, and under the environment — added before
    // anything reads the configuration.
    builder.AddIntegratorSettingsFile();

    // Wire up Aspire ServiceDefaults (telemetry, health checks, service discovery)
    builder.AddServiceDefaults();

    // ForwardedHeaders — critical for OpenIddict: correct HTTPS detection behind TLS termination/proxy
    // (single shared configuration — ServiceDefaults, common to all hosts).
    builder.Services.ConfigureForwardedHeadersForProxy(builder.Configuration);

    // Wire up the Transaction Engine:
    // If a connection string is set, the store runs on the provider Veriqa:TransactionEngine:Store:Provider
    // selects: EF Core on a relational provider of the host table (RelationalStoreProviders) — the host owns
    // the provider call and the migrations assembly (host-owned contract, SPEC-001 §10.2) — or Redis.
    // Otherwise — InMemory (for development), whatever the provider says. The provider is validated
    // (fail-fast): an unrecognized value is a configuration error, not a silent fallback. The same decision
    // is reused by the audit sink and the health checks.
    var transactionStore = TransactionStoreSelection.Resolve(builder.Configuration);

    // The collected claims store (SPEC-041 C37) is off unless switched on, and then it lives in the
    // relational transaction database: an enabled store without that database refuses the start here.
    var collectedClaimsStoreEnabled = ResolveCollectedClaimsStoreEnabled(builder.Configuration, transactionStore);

    // The event publisher is independent of the store: its connection string switches it on.
    var rabbitMqConnectionString = builder.Configuration[InfrastructureConfigConstants.RabbitMqConnectionStringKey];

    // With no store registered here the engine falls back to InMemory on its own.
    builder.Services.AddVeriqaTransactionEngine(builder.Configuration, engine =>
    {
        if (transactionStore?.Provider.RelationalProvider is { } transactionStoreProvider)
        {
            engine.UseEfCoreStore(transactionStoreProvider.BuildConfigurator(
                RelationalStore.Transactions,
                transactionStore.ConnectionString));

            // Same provider and connection string, the same migrations assembly, a history table of its own.
            if (collectedClaimsStoreEnabled)
            {
                engine.UseEfCoreCollectedClaimsStore(transactionStoreProvider.BuildConfigurator(
                    RelationalStore.CollectedClaims,
                    transactionStore.ConnectionString));
            }
        }
        else if (transactionStore is { } redisTransactionStore)
        {
            // Every option of the store comes from its section. The connection string is the host key, whose
            // name differs from the option it fills (Configuration), so it is assigned after the bind.
            engine.UseRedisStore(redis =>
            {
                builder.Configuration.GetSection(InfrastructureConfigConstants.TransactionStoreSectionName).Bind(redis);
                redis.Configuration = redisTransactionStore.ConnectionString;
            });
        }

        if (!string.IsNullOrWhiteSpace(rabbitMqConnectionString))
        {
            // The section is bound as a whole: the connection string and every other option of the publisher.
            engine.UseRabbitMqPublisher(rabbitMq =>
                builder.Configuration.GetSection(InfrastructureConfigConstants.RabbitMqSectionName).Bind(rabbitMq));
        }
    });

    // Wire up the OpenIddict integration (OIDC server, EF Core, Cookie auth, ClientSeeder).
    // The store provider is owned by the host (host-owned contract, SPEC-001 §10.2): the core stays
    // provider-agnostic and receives UseXxx + the migrations assembly as a delegate. The same resolved
    // decision is reused in health checks.
    var openIddictStore = ResolveOpenIddictStore(builder.Configuration);
    var openIddictConnectionString = builder.Configuration[InfrastructureConfigConstants.OpenIddictDatabaseConnectionStringKey];

    // Only a relational provider gets a configurator (built by its entry in the provider table); it is
    // guaranteed a non-empty connection string by the resolver above (fail-fast at startup).
    Action<DbContextOptionsBuilder>? configureOpenIddictDatabase = null;

    if (openIddictStore?.RelationalProvider is { } openIddictProvider)
    {
        configureOpenIddictDatabase = openIddictProvider.BuildConfigurator(
            RelationalStore.OpenIddict,
            openIddictConnectionString!);
    }

    // Provider=InMemory in the section is the spoken choice of the volatile store in its configuration
    // form (SPEC-012 CFG-118): the host says it to the core instead of leaving it to be guessed. An
    // unspoken choice (empty section) is passed on as it is — no delegate and no opt-in — and the core's
    // startup check refuses to start outside Development (CFG-119).
    if (openIddictStore is { IsInMemory: true })
    {
        builder.Services.UseInMemoryOpenIddictStore();
    }

    builder.Services.AddVeriqaOpenIddict(builder.Configuration, builder.Environment, configureOpenIddictDatabase);

    // Offline GeoIP of the initiator context check (SPEC-017 ICC-013), switched on by the path of the
    // database the deployment mounts; the image ships none. Without the path the null provider of the core
    // stays and the geo fields remain null (ICC-082). A path to a missing or unreadable file is the
    // provider's own concern: it logs a warning and stays unavailable, the start goes on.
    var geoIpDatabasePath = builder.Configuration[ConfigurationPath.Combine(
        InitiatorContextOptions.SectionName,
        nameof(InitiatorContextOptions.GeoIp),
        nameof(InitiatorGeoIpOptions.DatabasePath))];

    if (!string.IsNullOrWhiteSpace(geoIpDatabasePath))
    {
        builder.Services.AddMaxMindGeoIp();
    }

    // Wire up the audit trail (SPEC-011): the bus receiver gated by the resolved Logging.Mode, the
    // append-only journal and the retention sweep. The sink has keys of its own, Veriqa:Logging:Store,
    // each falling back to the same key of the relational transaction store (SPEC-012 CFG-252, SPEC-020
    // MODE-NET-3-014), so a development run without a database keeps the journal in memory. A transaction
    // store on Redis lends the sink nothing: a Redis connection string cannot be handed to an EF provider.
    //
    // Registered after the OpenIddict integration on purpose: bus handlers run one after another in
    // registration order on a single reader, so the real-time sign-in notification (registered inside
    // AddVeriqaOpenIddict) has to reach the browser before the journal insert, not after it. The
    // dependency is on the order of these two calls only — the audit registration itself needs nothing
    // from OpenIddict.
    var auditStore = ResolveAuditStore(builder.Configuration, transactionStore);

    builder.Services.AddVeriqaAuditTrail(audit =>
    {
        if (auditStore is not null)
        {
            audit.UseEfCoreSink(auditStore.Provider.BuildConfigurator(
                RelationalStore.Audit,
                auditStore.ConnectionString));
        }
        else
        {
            audit.UseInMemorySink();
        }
    });

    // The in-memory journal was picked by the fallback, not stated by anyone: whether that is acceptable
    // for the resolved Logging.Mode is decided at startup, when the environment is known (SPEC-011 E45,
    // SPEC-012 CFG-253). A durable sink needs no check, which is why the registration is conditional.
    if (auditStore is null)
    {
        // A transaction store on Redis has nothing to lend the sink, so the refusal names the sink key alone.
        // Read from the Provider value, not from the resolved store: with Redis named and no connection
        // string the store stays in memory, and the transaction connection string would cure nothing either.
        var transactionStoreOnRedis = TransactionStoreProvider.NamesRedis(builder.Configuration);

        builder.Services.AddHostedService(serviceProvider => new AuditStoreStartupCheckService(
            // The environment is optional by design: it may be absent outside a generic host.
            serviceProvider.GetService<IHostEnvironment>(),
            serviceProvider.GetRequiredService<IConfigurationResolver>(),
            serviceProvider.GetRequiredService<ILogger<AuditStoreStartupCheckService>>(),
            transactionStoreOnRedis));
    }

    // The channel stores shared by several replicas live in Redis once their connection string is set; with
    // the transaction store on Redis both share its connection, and a different string refuses the start.
    var channelStores = RedisChannelStoreSelection.Resolve(builder.Configuration, transactionStore);

    // Wire up the channel adapters, the HTTP channels and the web provider channels of the configuration.
    // The order of the calls is the order the channels are offered in when the integrator configures none.
    // The HTTP and the web provider channels are registered always: a configuration without such a channel
    // section registers none, and a channel is added by configuration and a restart, not by a rebuild.
    builder.Services.AddVeriqaChannelAdapters(builder.Configuration, adapters =>
    {
        // Every option of the stores comes from their section. The connection string is the host key, whose
        // name differs from the option it fills (Configuration), so it is assigned after the bind. The
        // satellite overrides the in-process stores whichever side of AddEmail() it is called on.
        if (channelStores is not null)
        {
            adapters.UseRedisChannelStores(redis =>
            {
                builder.Configuration.GetSection(InfrastructureConfigConstants.ChannelStoresRedisSectionName).Bind(redis);
                redis.Configuration = channelStores.SatelliteConfiguration;
            });
        }

        adapters.AddMax();
        adapters.AddTelegram();
        adapters.AddWhatsApp();
        adapters.AddMessenger();
        adapters.AddInstagram();
        adapters.AddWeChat();
        adapters.AddViber();
        adapters.AddEmail();
        adapters.AddHttpChannels();
        adapters.AddWebProviders();
    });

    // Request rate limiting: endpoint-level policies + per-user rate limiter (TASK-016, SPEC-007 §6.1)
    builder.Services.AddVeriqaRateLimiting();

    // SignalR backplane (Redis) — for multi-instance deployment
    // If the connection string is not set — the backplane is not enabled (single-instance mode)
    ConfigureSignalRBackplane(builder);

    // DataProtection — persisting keys in Redis or on disk
    ConfigureDataProtection(builder);

    // Health checks for infrastructure dependencies
    ConfigureHealthChecks(builder, transactionStore, auditStore, channelStores);

    // The path base this host is deployed under (empty — the root of the host name). Read and validated
    // before the application is built: a malformed value refuses the start.
    var pathBase = ResolvePathBase(builder.Configuration);

    var app = builder.Build();

    // A host can be asked what its composition DECLARES instead of being asked to serve: it states the
    // schema of the declared keys and exits, having started nothing. The answer is read out of the
    // container this host has just built, so it is the schema this very installation resolves by.
    if (ConfigSchemaDump.Requested(args))
    {
        ConfigSchemaDump.Write(Console.Out, app.Services.GetServices<ConfigKeyCatalog>());

        return;
    }

    // Middleware pipeline

    // Security headers: CSP (with nonce), X-Frame-Options, X-Content-Type-Options (SPEC-007 §6.3)
    // Placed BEFORE UseStaticFiles and UseRouting — covers all responses
    app.UseSecurityHeaders();

    app.UseForwardedHeaders();

    // Deployment under a path base. A host without one skips the block, so its pipeline stays as it was.
    if (pathBase.HasValue)
    {
        // The path base the server itself has already set (an IIS sub-application; Kestrel sets none, it
        // refuses an address with a path at start) is remembered before the configured one is applied: the
        // configured path base is nested into it, not put in its place.
        var serverPathBaseKey = new object();

        app.Use((context, next) =>
        {
            context.Items[serverPathBaseKey] = context.Request.PathBase;

            return next(context);
        });

        // The proxy passed the path intact: the path base is split off Request.Path.
        app.UsePathBase(pathBase);

        // The proxy trimmed the path base, or the request came straight to the instance (a health probe):
        // Request.Path is already the route and stays untouched, only the path base is stated. Assigned
        // for every request, so links, redirects and cookie paths carry it in the configured spelling
        // whichever way the request arrived.
        app.Use((context, next) =>
        {
            var serverPathBase = (PathString)context.Items[serverPathBaseKey]!;

            context.Request.PathBase = serverPathBase.Add(pathBase);

            return next(context);
        });

        // Routing is placed explicitly: the routing a WebApplication adds on its own runs at the very
        // start of the pipeline and would match the path before the path base is split off.
        app.UseRouting();
    }

    // Static file serving (wwwroot): SignalR JS client, localization (SPEC-007 §5, §12)
    app.UseStaticFiles();

    app.UseAuthentication();
    app.UseAuthorization();

    // Rate Limiting (SPEC-007 §6.1) — AFTER UseRouting, BEFORE MapEndpoints
    app.UseRateLimiter();

    // Register the standard Aspire endpoints (health, alive)
    app.MapDefaultEndpoints();

    // Additional health endpoints for liveness and readiness probes
    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains(InfrastructureConfigConstants.LivenessTag)
    });
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains(InfrastructureConfigConstants.ReadinessTag)
    });

    // Channel state — an endpoint of its own, deliberately outside readiness: an outage on a channel
    // platform is not a reason to take a working replica out of rotation, so an operator watching the
    // channels alerts on this endpoint rather than on the readiness probe.
    // The response writer is our own: the default one answers with the status word alone, and since the
    // channel check never reports worse than Degraded, the per-channel report is the only thing that
    // says which channel is down.
    app.MapHealthChecks("/health/channels", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains(ChannelHealthCheckNames.Tag),
        ResponseWriter = WriteChannelHealthResponseAsync
    });

    // Register the OIDC endpoints (authorize, callback, token, userinfo, polling status)
    app.MapVeriqaOidcEndpoints();

    // Register the endpoints of the registered channels (with rate limiting — SPEC-007 §6.1): their
    // webhooks plus whatever routes a channel maps for itself. A channel that is disabled in the
    // configuration contributes none, so the host exposes no route for it.
    app.MapChannelWebhookEndpoints(RateLimitOptions.ChannelEndpointPolicies);

    // Run the application
    app.Run();
});

/// <summary>
/// Configures the SignalR Redis backplane for multi-instance deployment.
/// If Veriqa:SignalR:RedisConnectionString is not set — the backplane is not enabled.
/// </summary>
static void ConfigureSignalRBackplane(WebApplicationBuilder builder)
{
    var redisConnectionString = builder.Configuration[InfrastructureConfigConstants.SignalRRedisConnectionStringKey];

    if (!string.IsNullOrWhiteSpace(redisConnectionString))
    {
        // Enable the Redis backplane for horizontal scaling of SignalR
        builder.Services
            .AddSignalR()
            .AddStackExchangeRedis(redisConnectionString, options =>
            {
                options.Configuration.ChannelPrefix = RedisChannel.Literal(InfrastructureConfigConstants.SignalRChannelPrefix);
            });
    }
}

/// <summary>
/// Configures DataProtection: where the key ring is stored and whether its keys are encrypted at rest.
/// Storage priority: Redis (Veriqa:DataProtection:RedisConnectionString) → file system
/// (Veriqa:DataProtection:KeysDirectory). Neither set — ASP.NET Core keeps the keys in its default location
/// for the account the host runs under; in the container image that is .aspnet/DataProtection-Keys in the
/// home directory of the app user inside the container, lost when the container is recreated.
/// A certificate (Veriqa:DataProtection:Certificate*) encrypts the new keys; it and the previous certificate
/// (Veriqa:DataProtection:PreviousCertificate*) decrypt the stored ones. Neither set — the keys are stored
/// unencrypted.
/// </summary>
static void ConfigureDataProtection(WebApplicationBuilder builder)
{
    var dpBuilder = builder.Services
        .AddDataProtection()
        .SetApplicationName(InfrastructureConfigConstants.DataProtectionApplicationName);

    var redisConnectionString = builder.Configuration[InfrastructureConfigConstants.DataProtectionRedisConnectionStringKey];
    var keysDirectory = builder.Configuration[InfrastructureConfigConstants.DataProtectionKeysDirectoryKey];

    if (!string.IsNullOrWhiteSpace(redisConnectionString))
    {
        // Create the multiplexer and register it as a keyed singleton to manage its lifecycle.
        // Using a keyed service ("dataprotection") avoids a conflict with the non-keyed
        // IConnectionMultiplexer of the Redis transaction store and channel stores (UseRedisStore(),
        // UseRedisChannelStores()).
        var redis = ConnectionMultiplexer.Connect(redisConnectionString);
        builder.Services.AddKeyedSingleton<IConnectionMultiplexer>("dataprotection", redis);

        // Persist keys in Redis — optimal for multi-instance
        dpBuilder.PersistKeysToStackExchangeRedis(
            redis,
            InfrastructureConfigConstants.DataProtectionRedisKey);
    }
    else if (!string.IsNullOrWhiteSpace(keysDirectory))
    {
        // Persist keys on the file system — for single-instance or a shared volume
        dpBuilder.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
    }
    // Otherwise ASP.NET Core picks its default location for the account (see the summary above)

    var certificate = LoadDataProtectionCertificate(
        builder.Configuration,
        InfrastructureConfigConstants.DataProtectionCertificateBase64Key,
        InfrastructureConfigConstants.DataProtectionCertificatePathKey,
        InfrastructureConfigConstants.DataProtectionCertificatePasswordKey);
    var previousCertificate = LoadDataProtectionCertificate(
        builder.Configuration,
        InfrastructureConfigConstants.DataProtectionPreviousCertificateBase64Key,
        InfrastructureConfigConstants.DataProtectionPreviousCertificatePathKey,
        InfrastructureConfigConstants.DataProtectionPreviousCertificatePasswordKey);

    // The certificate encrypts the new keys and is registered for their decryption by the same call.
    if (certificate is not null)
    {
        dpBuilder.ProtectKeysWithCertificate(certificate);
    }

    // Decryption looks any other certificate up in the X509 store, and a container has none there: the
    // previous certificate is handed over in memory, so the keys it encrypted stay readable while the
    // certificate is replaced.
    if (previousCertificate is not null)
    {
        dpBuilder.UnprotectKeysWithAnyCertificate(previousCertificate);
    }
}

/// <summary>
/// Loads a PFX certificate of the DataProtection key ring, the way the signing certificate of the server is
/// loaded: Base64 takes priority over the path. Neither set — null. A certificate that is set but cannot be
/// loaded (malformed Base64, missing file, wrong password) or carries no private key refuses the start, naming
/// the key it came from: without the private key the keys it encrypts could not be decrypted after a restart.
/// </summary>
static X509Certificate2? LoadDataProtectionCertificate(
    IConfiguration configuration,
    string base64Key,
    string pathKey,
    string passwordKey)
{
    var base64 = configuration[base64Key];
    var path = configuration[pathKey];
    var password = configuration[passwordKey];

    var fromBase64 = !string.IsNullOrEmpty(base64);

    if (!fromBase64 && string.IsNullOrEmpty(path))
    {
        return null;
    }

    var sourceKey = fromBase64 ? base64Key : pathKey;
    X509Certificate2 certificate;

    try
    {
        certificate = fromBase64
            ? X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(base64!), password)
            : X509CertificateLoader.LoadPkcs12FromFile(path!, password);
    }
    catch (Exception exception) when (exception is FormatException or CryptographicException or IOException or UnauthorizedAccessException)
    {
        throw new InvalidOperationException(
            $"The DataProtection certificate set by {sourceKey} could not be loaded.",
            exception);
    }

    if (!certificate.HasPrivateKey)
    {
        certificate.Dispose();

        throw new InvalidOperationException(
            $"The DataProtection certificate set by {sourceKey} has no private key.");
    }

    return certificate;
}

/// <summary>
/// Reads the path base of the deployment from Veriqa:PathBase. Not set or empty — no path base. A value
/// must start with a slash, must not end with one (so a lone slash is refused too) and must carry no
/// query, fragment, whitespace, percent-encoding, backslash or dot segment ("." and ".."); several
/// segments are allowed. Any other form is a configuration exception (fail-fast): a value the host
/// would apply half-way gives addresses no proxy rule matches. Percent-encoding and dot segments are
/// such values: the server decodes and normalizes the request path, so the path base written as it is
/// would never be found in it.
/// </summary>
static PathString ResolvePathBase(IConfiguration configuration)
{
    const char Separator = '/';
    const char QueryDelimiter = '?';
    const char FragmentDelimiter = '#';
    const char PercentEncodingMarker = '%';
    const char Backslash = '\\';
    const string CurrentSegment = ".";
    const string ParentSegment = "..";

    var value = configuration[InfrastructureConfigConstants.PathBaseKey];

    if (string.IsNullOrEmpty(value))
    {
        return PathString.Empty;
    }

    var wellFormed = value[0] == Separator
        && value[^1] != Separator
        && !value.Any(character =>
            character is QueryDelimiter or FragmentDelimiter or PercentEncodingMarker or Backslash
            || char.IsWhiteSpace(character))
        && !value.Split(Separator).Any(segment => segment is CurrentSegment or ParentSegment);

    if (!wellFormed)
    {
        throw new InvalidOperationException(
            $"Unsupported value '{InfrastructureConfigConstants.PathBaseKey}' = '{value}'. " +
            "The path base must start with '/', must not end with '/' and must not contain '?', '#', '%', " +
            "'\\', whitespace or a '.' or '..' segment, for example '/auth/veriqa'. Leave the key empty to " +
            "serve from the root.");
    }

    return new PathString(value);
}

/// <summary>
/// Reads the switch of the collected claims store, Veriqa:CollectedClaims:Store:Enabled (false by default).
/// The store borrows the provider and the connection string of the relational transaction store, so an
/// enabled store with Redis named in Veriqa:TransactionEngine:Store:Provider (with or without a connection
/// string), or without Veriqa:TransactionEngine:Store:ConnectionString, is a configuration exception
/// (fail-fast) naming the keys — a silent "no store" would ask the users for their claims again at every
/// sign-in while the operator believes they are kept.
/// </summary>
static bool ResolveCollectedClaimsStoreEnabled(IConfiguration configuration, TransactionStoreSelection? transactionStore)
{
    var enabled = configuration.GetValue<bool>(InfrastructureConfigConstants.CollectedClaimsStoreEnabledKey);

    if (!enabled)
    {
        return false;
    }

    // Redis is checked first and from the Provider value alone: with Redis named, the connection string the
    // next refusal asks for would only lead to this one.
    if (TransactionStoreProvider.NamesRedis(configuration))
    {
        throw new InvalidOperationException(
            $"'{InfrastructureConfigConstants.CollectedClaimsStoreEnabledKey}' = 'true' cannot be combined with " +
            $"'{InfrastructureConfigConstants.TransactionStoreProviderKey}' = '{TransactionStoreProvider.RedisName}': " +
            "the collected claims store lives in the relational transaction store database, and Redis has none. " +
            "Use a relational transaction store or switch the collected claims store off.");
    }

    if (transactionStore is null)
    {
        throw new InvalidOperationException(
            $"'{InfrastructureConfigConstants.CollectedClaimsStoreEnabledKey}' = 'true' requires " +
            $"'{InfrastructureConfigConstants.TransactionStoreConnectionStringKey}': the collected claims store lives in " +
            "the transaction store database. Set the connection string or switch the store off.");
    }

    return true;
}

/// <summary>
/// Resolves the OpenIddict store from Veriqa:OpenIddict:Database:Provider.
/// (SPEC-012 §5.2, CFG-117) — the host is the reader of this section in standalone mode.
/// Empty/unset — null: the choice was not spoken, and there is no default (SPEC-012 CFG-118).
/// The caller states nothing to the core in that case, and the core's startup check decides what
/// an unspoken choice means here (refusal outside Development, a warning inside it — CFG-119).
/// A relational provider requires a connection string, and an unrecognized value is a configuration
/// exception (fail-fast) — so a typo ("Postgres", an extra letter) does not lead to silently running
/// on InMemory.
/// </summary>
static OpenIddictStoreSelection? ResolveOpenIddictStore(IConfiguration configuration)
{
    var providerValue = configuration[InfrastructureConfigConstants.OpenIddictDatabaseProviderKey];

    if (string.IsNullOrWhiteSpace(providerValue))
    {
        return null;
    }

    if (string.Equals(providerValue.Trim(), RelationalStoreProviders.InMemoryName, StringComparison.OrdinalIgnoreCase))
    {
        return OpenIddictStoreSelection.InMemory;
    }

    var provider = RelationalStoreProviders.Find(providerValue)
        ?? throw new InvalidOperationException(
            $"Unsupported value '{InfrastructureConfigConstants.OpenIddictDatabaseProviderKey}' = '{providerValue}'. " +
            $"Allowed: '{RelationalStoreProviders.InMemoryName}', {RelationalStoreProviders.AllowedNames}.");

    if (string.IsNullOrWhiteSpace(configuration[InfrastructureConfigConstants.OpenIddictDatabaseConnectionStringKey]))
    {
        throw new InvalidOperationException(
            $"With '{InfrastructureConfigConstants.OpenIddictDatabaseProviderKey}' = '{provider.Name}' " +
            $"you must set '{InfrastructureConfigConstants.OpenIddictDatabaseConnectionStringKey}'.");
    }

    return new OpenIddictStoreSelection(provider);
}

/// <summary>
/// Resolves the audit sink from Veriqa:Logging:Store (SPEC-012 CFG-252). Each key falls back on its own
/// to the same key of the relational transaction store: no ConnectionString — the transaction connection
/// string; no Provider — the transaction provider (empty there too — PostgreSQL). A transaction store on
/// Redis lends neither: its connection string cannot be handed to an EF provider, so the sink then runs
/// only on a connection string of its own, on its own Provider or else PostgreSQL. An empty effective
/// connection string returns null: the journal stays in memory. A stated Provider is validated even then,
/// so an unrecognized value always refuses the start.
/// <para>
/// A stated sink Provider over a borrowed connection string must match the transaction provider: that
/// string belongs to the transaction database, and a different provider would silently point the
/// journal at a database of another kind.
/// </para>
/// </summary>
static AuditStoreSelection? ResolveAuditStore(IConfiguration configuration, TransactionStoreSelection? transactionStore)
{
    var ownProviderValue = configuration[InfrastructureConfigConstants.AuditStoreProviderKey];
    var ownProvider = string.IsNullOrWhiteSpace(ownProviderValue)
        ? null
        : RelationalStoreProviders.Resolve(InfrastructureConfigConstants.AuditStoreProviderKey, ownProviderValue);

    var ownConnectionString = configuration[InfrastructureConfigConstants.AuditStoreConnectionStringKey];

    if (!string.IsNullOrWhiteSpace(ownConnectionString))
    {
        // A connection string of its own: only the provider may be borrowed, and only a relational one.
        var effectiveProvider = ownProvider
            ?? TransactionStoreProvider.Resolve(configuration).RelationalProvider
            ?? RelationalStoreProviders.PostgreSql;

        return new AuditStoreSelection(effectiveProvider, ownConnectionString);
    }

    // Nothing to borrow — no durable transaction store, or one on Redis: the journal stays in memory.
    if (transactionStore?.Provider.RelationalProvider is not { } transactionProvider)
    {
        return null;
    }

    if (ownProvider is not null && !ReferenceEquals(ownProvider, transactionProvider))
    {
        throw new InvalidOperationException(
            $"'{InfrastructureConfigConstants.AuditStoreProviderKey}' = '{ownProviderValue}' differs from " +
            $"'{InfrastructureConfigConstants.TransactionStoreProviderKey}', which resolves to '{transactionProvider.Name}', " +
            $"while the audit store borrows '{InfrastructureConfigConstants.TransactionStoreConnectionStringKey}': " +
            $"set '{InfrastructureConfigConstants.AuditStoreConnectionStringKey}' or use the same provider.");
    }

    return new AuditStoreSelection(transactionProvider, transactionStore.ConnectionString);
}

/// <summary>
/// Registers health checks for infrastructure dependencies.
/// Each check is added conditionally — only when the corresponding dependency is configured.
/// OpenIddict DB: Veriqa:OpenIddict:Database:Provider (+ConnectionString) — the check is aligned
/// with the provider selected for the store, so InMemory registers no relational check.
/// Transaction DB: Veriqa:TransactionEngine:Store:ConnectionString (+Provider) — the check
/// is aligned with the provider selected for the store; a store on Redis gets a Redis check instead.
/// Audit DB: Veriqa:Logging:Store (with the fallback to the relational transaction store) — a separate
/// check only when the effective connection string differs from the transaction one (SPEC-012 CFG-252).
/// Check names are &lt;provider&gt;-&lt;store&gt; from the provider table (for example postgresql-transactions).
/// Redis (SignalR): Veriqa:SignalR:RedisConnectionString.
/// Redis (DataProtection): Veriqa:DataProtection:RedisConnectionString (a separate check when it differs from SignalR).
/// Redis (transaction store): a separate check when its connection string differs from both above.
/// Redis (channel stores): Veriqa:ChannelStores:Redis:ConnectionString — a separate check when it differs
/// from the three above.
/// RabbitMQ: Veriqa:Events:RabbitMq:ConnectionString (event publisher).
/// </summary>
static void ConfigureHealthChecks(
    WebApplicationBuilder builder,
    TransactionStoreSelection? transactionStore,
    AuditStoreSelection? auditStore,
    RedisChannelStoreSelection? channelStores)
{
    var checks = builder.Services.AddHealthChecks();

    // OpenIddict DB: the health check follows the same provider decision as the store itself
    // (single resolver with validation). A relational check is registered only for a relational
    // provider — with InMemory, or with a provider nobody spoke, there is nothing to probe.
    var openIddictStore = ResolveOpenIddictStore(builder.Configuration);
    var dbConnectionString = builder.Configuration[InfrastructureConfigConstants.OpenIddictDatabaseConnectionStringKey];

    if (openIddictStore?.RelationalProvider is { } openIddictProvider)
    {
        openIddictProvider.AddHealthCheck(checks, RelationalStore.OpenIddict, dbConnectionString!);
    }

    // Transaction store DB: the health check follows the same provider decision as the store itself
    // (Veriqa:TransactionEngine:Store:Provider). A store on Redis is probed with the Redis checks below.
    string? transactionDbConnectionString = null;

    if (transactionStore?.Provider.RelationalProvider is { } transactionStoreProvider)
    {
        transactionDbConnectionString = transactionStore.ConnectionString;
        transactionStoreProvider.AddHealthCheck(checks, RelationalStore.Transactions, transactionDbConnectionString);
    }

    // Audit DB: the journal on the transaction database is already probed by the check above, so a
    // check of its own is registered only when the sink points elsewhere (ordinal comparison, as the
    // Redis checks below do it).
    if (auditStore is not null
        && !string.Equals(auditStore.ConnectionString, transactionDbConnectionString, StringComparison.Ordinal))
    {
        auditStore.Provider.AddHealthCheck(checks, RelationalStore.Audit, auditStore.ConnectionString);
    }

    // Redis (SignalR backplane): verify Redis availability for SignalR
    var signalRRedis = builder.Configuration[InfrastructureConfigConstants.SignalRRedisConnectionStringKey];

    if (!string.IsNullOrWhiteSpace(signalRRedis))
    {
        checks.AddRedis(
            signalRRedis,
            name: InfrastructureHealthCheckNames.RedisSignalR,
            tags: [InfrastructureConfigConstants.ReadinessTag]);
    }

    // Redis (DataProtection): verify Redis availability for encryption key storage.
    // Register a separate check only when the connection string differs from the SignalR Redis
    // (to avoid duplicating an identical check when one Redis serves both services).
    var dpRedis = builder.Configuration[InfrastructureConfigConstants.DataProtectionRedisConnectionStringKey];

    if (!string.IsNullOrWhiteSpace(dpRedis) && dpRedis != signalRRedis)
    {
        checks.AddRedis(
            dpRedis,
            name: InfrastructureHealthCheckNames.RedisDataProtection,
            tags: [InfrastructureConfigConstants.ReadinessTag]);
    }

    // Redis (transaction store): a check of its own unless one of the checks above probes the same Redis.
    var transactionRedis = transactionStore is { Provider.IsRedis: true } ? transactionStore.ConnectionString : null;

    if (transactionRedis is not null && !IsRedisProbed(transactionRedis, signalRRedis, dpRedis))
    {
        checks.AddRedis(
            transactionRedis,
            name: InfrastructureHealthCheckNames.RedisTransactions,
            tags: [InfrastructureConfigConstants.ReadinessTag]);
    }

    // Redis (channel stores): a check of its own unless one of the checks above probes the same Redis — a
    // connection shared with the transaction store is always probed by now.
    if (channelStores is not null
        && !IsRedisProbed(channelStores.ConnectionString, signalRRedis, dpRedis, transactionRedis))
    {
        checks.AddRedis(
            channelStores.ConnectionString,
            name: InfrastructureHealthCheckNames.RedisChannelStores,
            tags: [InfrastructureConfigConstants.ReadinessTag]);
    }

    // RabbitMQ: verify RabbitMQ availability (event publisher)
    var rabbitConnectionString = builder.Configuration[InfrastructureConfigConstants.RabbitMqConnectionStringKey];

    if (!string.IsNullOrWhiteSpace(rabbitConnectionString))
    {
        var amqpUri = new Uri(rabbitConnectionString);
        checks.AddRabbitMQ(
            factory: _ =>
            {
                var connectionFactory = new RabbitMQ.Client.ConnectionFactory
                {
                    Uri = amqpUri,
                    AutomaticRecoveryEnabled = true
                };
                return connectionFactory.CreateConnectionAsync();
            },
            name: InfrastructureHealthCheckNames.RabbitMqEvents,
            tags: [InfrastructureConfigConstants.ReadinessTag]);
    }
}

/// <summary>
/// Tells whether a Redis connection string is already probed by a check registered for another
/// connection string (ordinal comparison: one Redis behind the same string gets one check).
/// </summary>
/// <param name="connectionString">Connection string of the dependency to probe.</param>
/// <param name="probedConnectionStrings">Connection strings already probed; null or empty entries probe nothing.</param>
/// <returns>true — the same connection string is already probed.</returns>
static bool IsRedisProbed(string connectionString, params string?[] probedConnectionStrings) =>
    probedConnectionStrings.Any(probed => string.Equals(connectionString, probed, StringComparison.Ordinal));

/// <summary>
/// Writes the channel health report as JSON: the overall status plus, for every check in it, its
/// name, status, description and per-channel data. The default writer answers with the status word
/// alone — and since the channel check never reports worse than Degraded, that word would leave an
/// operator with no way to tell which channel is down.
/// </summary>
/// <param name="context">HTTP context of the probe request.</param>
/// <param name="report">Report of the checks selected by the endpoint predicate.</param>
/// <returns>Task that completes once the response body is written.</returns>
static async Task WriteChannelHealthResponseAsync(
    HttpContext context,
    Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
{
    context.Response.ContentType = MediaTypeNames.Application.Json;

    // The document is written field by field rather than serialized from a model: the report values
    // are either a string of the check's closed value set or a number of milliseconds, and writing
    // them explicitly keeps anything else from leaking into a body an operator reads.
    await using var writer = new Utf8JsonWriter(context.Response.BodyWriter);

    writer.WriteStartObject();
    writer.WriteString("status", report.Status.ToString());
    writer.WriteStartArray("checks");

    foreach (var (name, entry) in report.Entries)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("status", entry.Status.ToString());
        writer.WriteString("description", entry.Description);
        writer.WriteStartObject("data");

        foreach (var (key, value) in entry.Data)
        {
            if (value is long milliseconds)
            {
                writer.WriteNumber(key, milliseconds);
            }
            else
            {
                // Null-safe on purpose: the writer runs after the status line and the headers are
                // already on the wire, so an exception here would leave a truncated body behind an
                // HTTP 200. HealthCheckResult.Data is a dictionary of object, and a check other
                // than this one may put a null in it.
                writer.WriteString(key, value?.ToString() ?? string.Empty);
            }
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();

    await writer.FlushAsync(context.RequestAborted);
}
