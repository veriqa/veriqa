// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// Configuration constants for infrastructure components: SignalR backplane, DataProtection, health checks.
/// TASK-012.
/// </summary>
public static class InfrastructureConfigConstants
{
    /// <summary>
    /// Configuration section name for the SignalR backplane.
    /// Example: Veriqa:SignalR:RedisConnectionString = "localhost:6379"
    /// </summary>
    public const string SignalRSectionName = "Veriqa:SignalR";

    /// <summary>
    /// Redis connection string key for the SignalR backplane.
    /// Full path: Veriqa:SignalR:RedisConnectionString
    /// If not set — the backplane is not enabled (suitable for single-instance).
    /// </summary>
    public const string SignalRRedisConnectionStringKey = "Veriqa:SignalR:RedisConnectionString";

    /// <summary>
    /// Configuration section name for DataProtection.
    /// </summary>
    public const string DataProtectionSectionName = "Veriqa:DataProtection";

    /// <summary>
    /// Redis connection string key for storing DataProtection keys.
    /// Full path: Veriqa:DataProtection:RedisConnectionString
    /// If not set — KeysDirectory is checked.
    /// </summary>
    public const string DataProtectionRedisConnectionStringKey = "Veriqa:DataProtection:RedisConnectionString";

    /// <summary>
    /// Key for the directory storing DataProtection keys on disk.
    /// Full path: Veriqa:DataProtection:KeysDirectory
    /// Used as a fallback if Redis is not configured.
    /// </summary>
    public const string DataProtectionKeysDirectoryKey = "Veriqa:DataProtection:KeysDirectory";

    /// <summary>
    /// Application name for DataProtection (key isolation between applications).
    /// </summary>
    public const string DataProtectionApplicationName = "veriqa-authserver";

    /// <summary>
    /// Redis key name for storing DataProtection keys.
    /// </summary>
    public const string DataProtectionRedisKey = "veriqa:dataprotection:keys";

    /// <summary>
    /// Redis channel prefix for the SignalR backplane (used with AddStackExchangeRedis).
    /// Isolates Veriqa messages from other SignalR applications on a shared Redis server.
    /// </summary>
    public const string SignalRChannelPrefix = "veriqa-signalr";

    /// <summary>
    /// Configuration section name for health checks.
    /// </summary>
    public const string HealthChecksSectionName = "Veriqa:HealthChecks";

    /// <summary>
    /// Database connection string key for OpenIddict (relational provider).
    /// Full path: Veriqa:OpenIddict:Database:ConnectionString
    /// Used in the health check to verify database availability.
    /// </summary>
    public const string OpenIddictDatabaseConnectionStringKey = "Veriqa:OpenIddict:Database:ConnectionString";

    /// <summary>
    /// Database provider key for OpenIddict (InMemory or a relational provider of the host; the shipped
    /// host accepts PostgreSQL / SqlServer).
    /// Full path: Veriqa:OpenIddict:Database:Provider
    /// Used to select the health check type.
    /// </summary>
    public const string OpenIddictDatabaseProviderKey = "Veriqa:OpenIddict:Database:Provider";

    /// <summary>
    /// Configuration section of the Redis channel stores, read by the standalone host.
    /// Full path: Veriqa:ChannelStores:Redis
    /// Bound as a whole to the channel store options (the key prefixes and ProcessedMessageRetention) when
    /// the stores are on; the connection string comes from <see cref="ChannelStoresRedisConnectionStringKey"/>.
    /// </summary>
    public const string ChannelStoresRedisSectionName = "Veriqa:ChannelStores:Redis";

    /// <summary>
    /// Redis connection string key of the channel stores (secret), read by the standalone host.
    /// Full path: Veriqa:ChannelStores:Redis:ConnectionString
    /// Set — the channel stores (prompt coordinates, Email tokens and correlations, link handles, start codes,
    /// phone number requests) live in Redis and are shared by every replica. Not set — they stay in process.
    /// With the transaction store on Redis the value must equal Veriqa:TransactionEngine:Store:ConnectionString:
    /// the container holds a single Redis multiplexer, and a different value refuses the start.
    /// </summary>
    public const string ChannelStoresRedisConnectionStringKey = "Veriqa:ChannelStores:Redis:ConnectionString";

    /// <summary>
    /// Configuration section of the RabbitMQ transaction event publisher, read by the standalone host.
    /// Full path: Veriqa:Events:RabbitMq
    /// Bound as a whole to the publisher options (ConnectionString, ExchangeName, QueueCapacity,
    /// MaxReconnectAttempts, ReconnectBaseDelaySeconds) when the publisher is on.
    /// </summary>
    public const string RabbitMqSectionName = "Veriqa:Events:RabbitMq";

    /// <summary>
    /// RabbitMQ connection string key for the event publisher (secret).
    /// Full path: Veriqa:Events:RabbitMq:ConnectionString
    /// Set — the standalone host publishes transaction events to the broker and probes it in readiness.
    /// Not set — no publisher and no RabbitMQ health check.
    /// </summary>
    public const string RabbitMqConnectionStringKey = "Veriqa:Events:RabbitMq:ConnectionString";

    /// <summary>
    /// Configuration section of the transaction store, read by the standalone host.
    /// Full path: Veriqa:TransactionEngine:Store
    /// On the Redis store the section is bound as a whole to the Redis store options (KeyPrefix); the
    /// connection string comes from <see cref="TransactionStoreConnectionStringKey"/>.
    /// </summary>
    public const string TransactionStoreSectionName = "Veriqa:TransactionEngine:Store";

    /// <summary>
    /// Transaction store connection string key (secret): a relational connection string, or a Redis one
    /// when the provider is Redis.
    /// Full path: Veriqa:TransactionEngine:Store:ConnectionString
    /// If not set — the InMemory store is used, whatever the provider says.
    /// </summary>
    public const string TransactionStoreConnectionStringKey = "Veriqa:TransactionEngine:Store:ConnectionString";

    /// <summary>
    /// Transaction store provider key (the shipped host accepts PostgreSQL / SqlServer / Redis; empty —
    /// PostgreSQL).
    /// Full path: Veriqa:TransactionEngine:Store:Provider
    /// The provider is chosen by configuration from the set baked into the image at build time (SPEC-020).
    /// </summary>
    public const string TransactionStoreProviderKey = "Veriqa:TransactionEngine:Store:Provider";

    /// <summary>
    /// Audit sink connection string key (secret), read by the standalone host (SPEC-012 CFG-252).
    /// Full path: Veriqa:Logging:Store:ConnectionString
    /// If not set — Veriqa:TransactionEngine:Store:ConnectionString is used, unless the transaction store
    /// is on Redis: a Redis connection string is never handed to the sink. Empty in the end — the journal
    /// stays in memory (SPEC-012 CFG-253).
    /// </summary>
    public const string AuditStoreConnectionStringKey = "Veriqa:Logging:Store:ConnectionString";

    /// <summary>
    /// Audit sink provider key, read by the standalone host (SPEC-012 CFG-252).
    /// Full path: Veriqa:Logging:Store:Provider
    /// If not set — Veriqa:TransactionEngine:Store:Provider is used, unless the transaction store is on
    /// Redis; empty there, or Redis — PostgreSQL.
    /// </summary>
    public const string AuditStoreProviderKey = "Veriqa:Logging:Store:Provider";

    /// <summary>
    /// Switch of the collected claims store in the standalone host (SPEC-041 C37), bool, false by default.
    /// Full path: Veriqa:CollectedClaims:Store:Enabled
    /// True — the EF Core store on the provider and the connection string of the transaction store
    /// (Veriqa:TransactionEngine:Store), with a migration history table of its own; the host refuses to start
    /// when that connection string is empty. False — no store: nothing collected is kept.
    /// </summary>
    public const string CollectedClaimsStoreEnabledKey = "Veriqa:CollectedClaims:Store:Enabled";

    /// <summary>
    /// Key of the path base the standalone host is deployed under, for example <c>/auth/veriqa</c>.
    /// Full path: Veriqa:PathBase
    /// Not set or empty — the host serves from the root of the host name. A value starts with a slash, does
    /// not end with one and carries no query, fragment, whitespace, percent-encoding, backslash or dot
    /// segment ("." and ".."); any other form refuses the start.
    /// The path base applies to every request, whether the proxy passes the path intact or trims it. It is
    /// nested into the path base the server itself has set, when there is one.
    /// </summary>
    public const string PathBaseKey = "Veriqa:PathBase";

    /// <summary>
    /// Liveness check tag (alive only — the process is running).
    /// </summary>
    public const string LivenessTag = "live";

    /// <summary>
    /// Readiness check tag (ready to serve traffic — dependencies are available).
    /// </summary>
    public const string ReadinessTag = "ready";
}
