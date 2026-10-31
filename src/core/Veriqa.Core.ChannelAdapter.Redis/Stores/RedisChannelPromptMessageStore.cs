// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;

using Microsoft.Extensions.Options;

using StackExchange.Redis;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Redis.Stores;

/// <summary>
/// Redis implementation of <see cref="IChannelPromptMessageStore"/> (SPEC-003 §4.5). Behaves like
/// the in-process default in everything but distribution: the record is visible to every replica, so
/// the TTL-expiry event reaches the chat even when it lands on an instance other than the one that
/// wrote the record.
/// </summary>
/// <remarks>
/// <para>
/// Remove-on-read is a single <c>GETDEL</c>, so concurrent takes across replicas resolve the same
/// way a <c>ConcurrentDictionary.TryRemove</c> resolves in one process: exactly one caller receives
/// the coordinates, everyone else receives null.
/// </para>
/// <para>
/// The take of one channel's coordinates compares and removes in one Redis transaction conditioned
/// on the value read, the way the in-process store removes the very entry it compared. The write of
/// the phone number request reference is the same kind of transaction: a <c>SET</c> with
/// <c>KEEPTTL</c> applied only while the key still holds the value read, so the record keeps the
/// lifetime it was saved with.
/// </para>
/// <para>
/// A prompt record has no expiry of its own, and <c>SaveAsync</c> is not given the expiry of the
/// transaction either, so its lifetime is derived from the longest life a transaction can have — the
/// engine's own upper bound on the transaction TTL — plus a margin for the delay between a
/// transaction expiring and the expiry event that consumes the record being published. The
/// configured default TTL is deliberately not used — a single transaction may be created with a
/// longer <c>TtlSeconds</c> of its own, and a record cut to the default would be gone before the
/// expiry event arrives. Neither term is a constant of this package: both come from the engine.
/// </para>
/// </remarks>
internal sealed class RedisChannelPromptMessageStore : IChannelPromptMessageStore
{
    /// <summary>
    /// Store options (key prefix).
    /// </summary>
    private readonly RedisChannelStoreOptions _options;

    /// <summary>
    /// Transaction Engine configuration — the source of the record lifetime.
    /// </summary>
    private readonly IOptionsMonitor<TransactionEngineOptions> _engineOptions;

    /// <summary>
    /// Logical Redis database handle.
    /// </summary>
    private readonly IDatabase _db;

    /// <summary>
    /// Creates the Redis prompt coordinate store.
    /// </summary>
    /// <param name="connectionMultiplexer">Redis connection multiplexer.</param>
    /// <param name="options">Channel store options.</param>
    /// <param name="engineOptions">Transaction Engine configuration.</param>
    public RedisChannelPromptMessageStore(
        IConnectionMultiplexer connectionMultiplexer,
        IOptions<RedisChannelStoreOptions> options,
        IOptionsMonitor<TransactionEngineOptions> engineOptions)
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _engineOptions = engineOptions;
        _db = connectionMultiplexer.GetDatabase();
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        TransactionId transactionId,
        ChannelPromptMessageRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();

        // A plain SET overwrites a prior entry for the same transaction: a resent prompt supersedes
        // the previous message, exactly as in the in-process store.
        var json = JsonSerializer.Serialize(reference, RedisChannelStoreSerialization.Options);

        await _db.StringSetAsync(GetKey(transactionId), json, GetRecordLifetime());
    }

    /// <inheritdoc />
    public async Task<ChannelPromptMessageRef?> FindAsync(
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A plain GET: the record stays for the take that follows.
        var value = await _db.StringGetAsync(GetKey(transactionId));

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize<ChannelPromptMessageRef>(
            value.ToString(),
            RedisChannelStoreSerialization.Options);
    }

    /// <inheritdoc />
    public async Task<ChannelPromptMessageRef?> TakeAsync(
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // GETDEL is a single atomic read-and-remove: two replicas racing for the same transaction
        // cannot both edit the message.
        var value = await _db.StringGetDeleteAsync(GetKey(transactionId));

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize<ChannelPromptMessageRef>(
            value.ToString(),
            RedisChannelStoreSerialization.Options);
    }

    /// <inheritdoc />
    public async Task<ChannelPromptMessageRef?> TakeForChannelAsync(
        TransactionId transactionId,
        string channelType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        cancellationToken.ThrowIfCancellationRequested();

        var key = GetKey(transactionId);
        var value = await _db.StringGetAsync(key);

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        var reference = JsonSerializer.Deserialize<ChannelPromptMessageRef>(
            value.ToString(),
            RedisChannelStoreSerialization.Options);

        if (reference is null || !string.Equals(reference.ChannelType, channelType, StringComparison.Ordinal))
        {
            return null;
        }

        // The removal is applied only while the key still holds the very value compared above
        // (SPEC-042 L62): an entry another channel saved in between fails the condition, the
        // transaction is not applied and that entry stays. Nothing is retried — the entry is no
        // longer this channel's to take.
        var transaction = _db.CreateTransaction();
        transaction.AddCondition(Condition.StringEqual(key, value));
        var deleteTask = transaction.KeyDeleteAsync(key);

        if (!await transaction.ExecuteAsync())
        {
            return null;
        }

        await deleteTask;
        return reference;
    }

    /// <inheritdoc />
    public async Task SetPhoneRequestMessageAsync(
        TransactionId transactionId,
        string channelType,
        ChannelMessageRef requestMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(requestMessage);
        cancellationToken.ThrowIfCancellationRequested();

        var key = GetKey(transactionId);
        var value = await _db.StringGetAsync(key);

        if (value.IsNullOrEmpty)
        {
            return;
        }

        var reference = JsonSerializer.Deserialize<ChannelPromptMessageRef>(
            value.ToString(),
            RedisChannelStoreSerialization.Options);

        if (reference is null || !string.Equals(reference.ChannelType, channelType, StringComparison.Ordinal))
        {
            return;
        }

        var json = JsonSerializer.Serialize(
            reference with { PhoneRequestMessage = requestMessage },
            RedisChannelStoreSerialization.Options);

        // The write is applied only while the key still holds the very value compared above: a
        // record saved or taken in between fails the condition, the transaction is not applied and
        // nothing is retried — that record is not the one the reference was meant for. KEEPTTL
        // leaves the lifetime the record was saved with.
        var transaction = _db.CreateTransaction();
        transaction.AddCondition(Condition.StringEqual(key, value));
        var setTask = transaction.StringSetAsync(key, json, keepTtl: true);

        if (await transaction.ExecuteAsync())
        {
            await setTask;
        }
    }

    /// <summary>
    /// Builds the Redis key of the prompt coordinates of a transaction.
    /// </summary>
    /// <param name="transactionId">Transaction the prompt belongs to.</param>
    /// <returns>Redis key.</returns>
    private string GetKey(TransactionId transactionId)
    {
        return string.Concat(_options.PromptKeyPrefix, transactionId.ToString());
    }

    /// <summary>
    /// Lifetime of a stored prompt record: the engine's maximum transaction TTL plus the margin in
    /// which the expiry event that consumes the record is published. The upper bound is used rather
    /// than the configured default, because a transaction may be created with its own
    /// <c>TtlSeconds</c> up to that bound.
    /// </summary>
    /// <returns>Record lifetime.</returns>
    private TimeSpan GetRecordLifetime()
    {
        var engineOptions = _engineOptions.CurrentValue;

        // The record is consumed by the expiry event, and when no late decision publishes it first,
        // that event is published by the engine's cleanup sweep: a transaction that expires just after
        // one sweep waits a whole CleanupIntervalSeconds for the next. One sweep is only the lower bound of the wait — it
        // takes at most CleanupBatchSize transactions, so a backlog spreads publication over several
        // passes — and the completed-retention window is taken whenever it is the larger of the two,
        // as the scale on which this engine is already configured to keep an expired transaction
        // reachable. Neither value is a constant of this package: both come from the engine.
        var publicationMargin = Math.Max(
            engineOptions.CleanupIntervalSeconds,
            engineOptions.CompletedRetentionSeconds);

        return TimeSpan.FromSeconds(
            TransactionEngineOptions.MaxTransactionTtlSeconds + publicationMargin);
    }
}
