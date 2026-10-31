// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// In-process implementation of <see cref="IChannelStartCodeStore"/> — the default of a single
/// instance. A record answers until its <see cref="ChannelStartCode.ExpiresAt"/>; records no start
/// took before that are reclaimed by an opportunistic sweep, so the store cannot grow without bound.
/// </summary>
/// <remarks>
/// A record is reachable by two keys — its code and its transaction — and both have to change
/// together, so the two indexes live under one lock rather than in two concurrent dictionaries: a
/// write that is atomic per dictionary would still let a reader see a code whose transaction index
/// is not there yet.
/// </remarks>
internal sealed class InMemoryChannelStartCodeStore : IChannelStartCodeStore
{
    /// <summary>
    /// Run the sweep of expired records every N <see cref="TryAddAsync"/> calls.
    /// </summary>
    private const int CleanupEveryNCalls = 50;

    /// <summary>
    /// Guards both indexes and the sweep counter.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Records by code, compared ordinally.
    /// </summary>
    private readonly Dictionary<string, ChannelStartCode> _byCode = new(StringComparer.Ordinal);

    /// <summary>
    /// Records by transaction. Holds the very instances of <see cref="_byCode"/>.
    /// </summary>
    private readonly Dictionary<TransactionId, ChannelStartCode> _byTransaction = new();

    /// <summary>
    /// Clock the deadline of a record is read against.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Counter of <see cref="TryAddAsync"/> calls since the last sweep.
    /// </summary>
    private int _addCallCount;

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="timeProvider">Clock the deadline of a record is read against.</param>
    public InMemoryChannelStartCodeStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public Task<bool> TryAddAsync(ChannelStartCode record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        ArgumentException.ThrowIfNullOrEmpty(record.Code);

        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            _addCallCount++;
            if (_addCallCount >= CleanupEveryNCalls)
            {
                _addCallCount = 0;
                RemoveExpiredRecords(now);
            }

            // An expired record under either key no longer answers, so it does not stand in the way:
            // it is dropped from both indexes before the slot is judged.
            if (FindLive(_byCode, record.Code, now) is not null
                || FindLive(_byTransaction, record.TransactionId, now) is not null)
            {
                return Task.FromResult(false);
            }

            _byCode[record.Code] = record;
            _byTransaction[record.TransactionId] = record;

            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<ChannelStartCode?> FindByTransactionAsync(
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ChannelStartCode?>(cancellationToken);
        }

        lock (_gate)
        {
            return Task.FromResult(FindLive(_byTransaction, transactionId, _timeProvider.GetUtcNow()));
        }
    }

    /// <inheritdoc />
    public Task<ChannelStartCode?> TakeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ChannelStartCode?>(cancellationToken);
        }

        ArgumentException.ThrowIfNullOrEmpty(code);

        lock (_gate)
        {
            var record = FindLive(_byCode, code, _timeProvider.GetUtcNow());
            if (record is not null)
            {
                Remove(record);
            }

            return Task.FromResult(record);
        }
    }

    /// <summary>
    /// Reads a record by one of its keys, dropping it from both indexes when it is past its deadline.
    /// Called under <see cref="_gate"/>.
    /// </summary>
    /// <typeparam name="TKey">Type of the key.</typeparam>
    /// <param name="index">Index to read.</param>
    /// <param name="key">Key of the record.</param>
    /// <param name="now">Current moment.</param>
    /// <returns>The live record; null — there is none.</returns>
    private ChannelStartCode? FindLive<TKey>(
        Dictionary<TKey, ChannelStartCode> index,
        TKey key,
        DateTimeOffset now)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var record))
        {
            return null;
        }

        if (now >= record.ExpiresAt)
        {
            Remove(record);
            return null;
        }

        return record;
    }

    /// <summary>
    /// Removes a record from both indexes. Called under <see cref="_gate"/>.
    /// </summary>
    /// <param name="record">Record to remove.</param>
    private void Remove(ChannelStartCode record)
    {
        _byCode.Remove(record.Code);
        _byTransaction.Remove(record.TransactionId);
    }

    /// <summary>
    /// Removes the records past their deadline. Called under <see cref="_gate"/>.
    /// </summary>
    /// <param name="now">Current moment.</param>
    private void RemoveExpiredRecords(DateTimeOffset now)
    {
        var expired = new List<ChannelStartCode>();
        foreach (var record in _byCode.Values)
        {
            if (now >= record.ExpiresAt)
            {
                expired.Add(record);
            }
        }

        foreach (var record in expired)
        {
            Remove(record);
        }
    }
}
