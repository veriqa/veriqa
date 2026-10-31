// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Veriqa.Core.TransactionEngine.Store;
using Veriqa.Core.TransactionEngine.Store.Redis;

namespace Veriqa.Core.TransactionEngine.DependencyInjection;

/// <summary>Redis store registration extensions for <see cref="TransactionEngineBuilder"/>.</summary>
public static class TransactionEngineBuilderRedisExtensions
{
    /// <summary>
    /// Uses the Redis store.
    /// Registers the ConnectionMultiplexer and RedisTransactionStore.
    /// Redis manages transaction expiration natively via TTL.
    /// </summary>
    /// <remarks>
    /// Supported extension point: the shipped standalone host wires it by configuration — the
    /// value <c>Redis</c> in <c>Veriqa:TransactionEngine:Store:Provider</c> together with a connection
    /// string; without them it stays on <see cref="TransactionEngineBuilder.UseInMemoryStore"/> or
    /// <c>UseEfCoreStore</c> of the EF Core satellite.
    /// CI does not run that host against a live Redis — validate the connection in the consuming host when
    /// you enable it.
    /// </remarks>
    /// <param name="builder">Transaction Engine builder.</param>
    /// <param name="configure">Action configuring the store parameters.</param>
    /// <returns>Builder for chaining.</returns>
    public static TransactionEngineBuilder UseRedisStore(
        this TransactionEngineBuilder builder,
        Action<RedisTransactionStoreOptions> configure)
    {
        // Register the Redis store parameters
        builder.Services.Configure(configure);

        // Register the Redis multiplexer as a Singleton (not keyed — the host's DataProtection
        // keyed lookup relies on a non-keyed IConnectionMultiplexer).
        builder.Services.AddSingleton<IConnectionMultiplexer>(serviceProvider =>
        {
            var storeOptions = serviceProvider
                .GetRequiredService<IOptions<RedisTransactionStoreOptions>>()
                .Value;

            return ConnectionMultiplexer.Connect(storeOptions.Configuration);
        });

        // Register the Redis store as a Singleton
        builder.Services.AddSingleton<ITransactionStore, RedisTransactionStore>();

        builder.StoreRegistered = true;

        return builder;
    }
}
