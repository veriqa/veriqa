// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Host;

/// <summary>
/// Names of the readiness health checks the host registers for its non-relational dependencies. The
/// relational checks are named by the provider table (<see cref="RelationalStoreProvider.AddHealthCheck"/>).
/// </summary>
internal static class InfrastructureHealthCheckNames
{
    /// <summary>Redis of the SignalR backplane.</summary>
    public const string RedisSignalR = "redis-signalr";

    /// <summary>Redis of the DataProtection key ring, when it differs from the SignalR one.</summary>
    public const string RedisDataProtection = "redis-dataprotection";

    /// <summary>Redis of the transaction store, unless another check probes the same Redis.</summary>
    public const string RedisTransactions = "redis-transactions";

    /// <summary>Redis of the channel stores, unless another check probes the same Redis.</summary>
    public const string RedisChannelStores = "redis-channel-stores";

    /// <summary>RabbitMQ broker of the transaction event publisher.</summary>
    public const string RabbitMqEvents = "rabbitmq-events";
}
