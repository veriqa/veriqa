// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Constants;

namespace Veriqa.Core.AuthServer.Host;

/// <summary>
/// Redis channel stores switched on by Veriqa:ChannelStores:Redis:ConnectionString. The resolver returns
/// null when the key is empty: the channel stores then stay in process, as without the satellite.
/// </summary>
/// <param name="ConnectionString">Non-empty connection string of the channel stores (a secret).</param>
/// <param name="ReusesTransactionStoreConnection">
/// Whether the channel stores reuse the connection of the Redis transaction store: the container holds a
/// single non-keyed Redis multiplexer, and the transaction store registers it.
/// </param>
internal sealed record RedisChannelStoreSelection(string ConnectionString, bool ReusesTransactionStoreConnection)
{
    /// <summary>
    /// Connection string handed to the satellite options: empty when the connection of the transaction store
    /// is reused — the satellite then takes the multiplexer already registered in the container instead of
    /// refusing the start over a second connection string.
    /// </summary>
    public string SatelliteConfiguration => ReusesTransactionStoreConnection ? string.Empty : ConnectionString;

    /// <summary>
    /// Resolves the Redis channel stores. With the transaction store on Redis both stores share one
    /// connection: the same connection string (ordinal comparison) reuses it, a different one is a
    /// configuration exception (fail-fast) that names the two keys and never the connection strings, which
    /// carry credentials.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="transactionStore">Resolved transaction store, or null for the in-memory store.</param>
    /// <returns>The channel stores on Redis, or null for the in-process stores.</returns>
    public static RedisChannelStoreSelection? Resolve(
        IConfiguration configuration,
        TransactionStoreSelection? transactionStore)
    {
        var connectionString = configuration[InfrastructureConfigConstants.ChannelStoresRedisConnectionStringKey];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        if (transactionStore is not { Provider.IsRedis: true })
        {
            return new RedisChannelStoreSelection(connectionString, ReusesTransactionStoreConnection: false);
        }

        if (!string.Equals(connectionString, transactionStore.ConnectionString, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{InfrastructureConfigConstants.ChannelStoresRedisConnectionStringKey}' differs from " +
                $"'{InfrastructureConfigConstants.TransactionStoreConnectionStringKey}' while " +
                $"'{InfrastructureConfigConstants.TransactionStoreProviderKey}' = '{TransactionStoreProvider.RedisName}': " +
                "the container holds a single Redis connection multiplexer, which the transaction store registers, " +
                "so the channel stores cannot connect to a Redis of their own. Set both keys to the same connection " +
                "string.");
        }

        return new RedisChannelStoreSelection(connectionString, ReusesTransactionStoreConnection: true);
    }
}
