// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;

using Veriqa.Core.ChannelAdapter.Abstractions;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// In-process implementation of <see cref="ILinkHandleStore"/> — the default of a single instance. A
/// record answers until its <see cref="LinkHandleRecord.ExpiresAt"/>; records nothing took before that
/// are reclaimed by an opportunistic sweep, so the dictionary cannot grow without bound.
/// </summary>
internal sealed class InMemoryLinkHandleStore : ILinkHandleStore
{
    /// <summary>
    /// Run the sweep of expired records every N <see cref="SaveAsync"/> calls.
    /// </summary>
    private const int CleanupEveryNCalls = 50;

    /// <summary>
    /// Records by the fingerprint of the handle.
    /// </summary>
    private readonly ConcurrentDictionary<string, LinkHandleRecord> _records = new(StringComparer.Ordinal);

    /// <summary>
    /// Clock the deadline of a record is read against.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Counter of <see cref="SaveAsync"/> calls that triggers the sweep.
    /// </summary>
    private int _saveCallCount;

    /// <summary>
    /// Guard flag (0/1) ensuring at most one sweep runs at a time.
    /// </summary>
    private int _cleanupInProgress;

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="timeProvider">Clock the deadline of a record is read against.</param>
    public InMemoryLinkHandleStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public Task SaveAsync(LinkHandleRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(record.HandleHash, nameof(record));
        ArgumentException.ThrowIfNullOrEmpty(record.ApplicationId, nameof(record));

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        _records[record.HandleHash] = record;

        if (Interlocked.Increment(ref _saveCallCount) % CleanupEveryNCalls is 0)
        {
            TryRemoveExpiredRecords();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LinkHandleRecord?> TakeAsync(
        string handleHash,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(handleHash);
        ArgumentException.ThrowIfNullOrEmpty(applicationId);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<LinkHandleRecord?>(cancellationToken);
        }

        if (!_records.TryGetValue(handleHash, out var record))
        {
            return Task.FromResult<LinkHandleRecord?>(null);
        }

        if (IsExpired(record))
        {
            // Removed only while the slot still holds this very record.
            _records.TryRemove(KeyValuePair.Create(handleHash, record));
            return Task.FromResult<LinkHandleRecord?>(null);
        }

        // Another client's call leaves the record in place for the client it names.
        if (!string.Equals(record.ApplicationId, applicationId, StringComparison.Ordinal))
        {
            return Task.FromResult<LinkHandleRecord?>(null);
        }

        // The removal names the very record that was compared, and it is the removal that hands the
        // record out: of several concurrent calls only the one whose removal succeeded receives it.
        return Task.FromResult<LinkHandleRecord?>(
            _records.TryRemove(KeyValuePair.Create(handleHash, record)) ? record : null);
    }

    /// <summary>
    /// Whether a record is past its deadline.
    /// </summary>
    /// <param name="record">Record to check.</param>
    /// <returns>true — the record no longer answers.</returns>
    private bool IsExpired(LinkHandleRecord record) => _timeProvider.GetUtcNow() >= record.ExpiresAt;

    /// <summary>
    /// Removes the expired records, but runs no more than one sweep at a time.
    /// </summary>
    private void TryRemoveExpiredRecords()
    {
        if (Interlocked.CompareExchange(ref _cleanupInProgress, 1, 0) is not 0)
        {
            return;
        }

        try
        {
            foreach (var entry in _records)
            {
                if (IsExpired(entry.Value))
                {
                    _records.TryRemove(entry);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _cleanupInProgress, 0);
        }
    }
}
