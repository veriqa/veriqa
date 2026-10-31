// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Constants;

namespace Veriqa.Core.AuthServer.Host;

/// <summary>
/// Provider of the transaction store spoken in Veriqa:TransactionEngine:Store:Provider: a relational entry
/// of the host table (<see cref="RelationalStoreProviders"/>) or Redis.
/// </summary>
/// <param name="RelationalProvider">Relational provider, or null for Redis.</param>
internal sealed record TransactionStoreProvider(RelationalStoreProvider? RelationalProvider)
{
    /// <summary>
    /// Value of Veriqa:TransactionEngine:Store:Provider that selects the Redis store. It is not a relational
    /// provider and has no entry in the provider table, so the OpenIddict store and the audit sink do not
    /// accept it.
    /// </summary>
    public const string RedisName = "Redis";

    /// <summary>
    /// The Redis store was selected.
    /// </summary>
    public static TransactionStoreProvider Redis { get; } = new(RelationalProvider: null);

    /// <summary>
    /// Whether the Redis store was selected.
    /// </summary>
    public bool IsRedis => RelationalProvider is null;

    /// <summary>
    /// Whether Veriqa:TransactionEngine:Store:Provider names Redis (trimmed, ignoring case), read without
    /// validating the value and without a connection string. A refusal text picks its advice by it: with
    /// Redis named, setting the transaction connection string cures nothing, since the store then runs on
    /// Redis and lends no relational database.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>True when the value names Redis.</returns>
    public static bool NamesRedis(IConfiguration configuration) =>
        NamesRedis(configuration[InfrastructureConfigConstants.TransactionStoreProviderKey]);

    /// <summary>
    /// Resolves the provider from Veriqa:TransactionEngine:Store:Provider. Empty/unset — PostgreSQL (the
    /// default). The value is trimmed and compared ignoring case. An unrecognized value is a configuration
    /// exception (fail-fast), so a typo ("Postgres", "Redsi") does not silently pick another store.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The selected provider.</returns>
    public static TransactionStoreProvider Resolve(IConfiguration configuration)
    {
        var value = configuration[InfrastructureConfigConstants.TransactionStoreProviderKey];

        if (string.IsNullOrWhiteSpace(value))
        {
            return new TransactionStoreProvider(RelationalStoreProviders.PostgreSql);
        }

        if (NamesRedis(value))
        {
            return Redis;
        }

        var relationalProvider = RelationalStoreProviders.Find(value)
            ?? throw new InvalidOperationException(
                $"Unsupported value '{InfrastructureConfigConstants.TransactionStoreProviderKey}' = '{value}'. " +
                $"Allowed: {RelationalStoreProviders.AllowedNames}, '{RedisName}'.");

        return new TransactionStoreProvider(relationalProvider);
    }

    /// <summary>
    /// Whether a raw Provider value names Redis: trimmed and compared ignoring case.
    /// </summary>
    /// <param name="value">Raw value of Veriqa:TransactionEngine:Store:Provider.</param>
    /// <returns>True when the value names Redis.</returns>
    private static bool NamesRedis(string? value) =>
        value is not null && string.Equals(value.Trim(), RedisName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Durable transaction store resolved from Veriqa:TransactionEngine:Store: the provider and the non-empty
/// connection string it runs on. The resolver returns null when the connection string is empty — the store
/// then stays in memory whatever the provider says, so a development run needs no database and no Redis.
/// </summary>
/// <param name="Provider">Provider of the store.</param>
/// <param name="ConnectionString">Non-empty connection string of the store (a secret).</param>
internal sealed record TransactionStoreSelection(TransactionStoreProvider Provider, string ConnectionString)
{
    /// <summary>
    /// Resolves the durable transaction store. The provider is validated only when there is a connection
    /// string to run it on.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The selected store, or null for the in-memory store.</returns>
    public static TransactionStoreSelection? Resolve(IConfiguration configuration)
    {
        var connectionString = configuration[InfrastructureConfigConstants.TransactionStoreConnectionStringKey];

        return string.IsNullOrWhiteSpace(connectionString)
            ? null
            : new TransactionStoreSelection(TransactionStoreProvider.Resolve(configuration), connectionString);
    }
}
