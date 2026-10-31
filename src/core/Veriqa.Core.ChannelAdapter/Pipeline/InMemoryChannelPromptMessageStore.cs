// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// In-memory implementation of <see cref="IChannelPromptMessageStore"/> (SPEC-003 §4.5).
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>. Suitable for single-instance,
/// self-hosted and demo scenarios (mirrors the engine's default in-memory transaction store). For a
/// multi-instance deployment a distributed implementation (e.g. Redis) can be layered on later.
///
/// Entries are removed on read (<see cref="TakeAsync"/>). Entries of transactions that never emit a
/// TTL-expiry event (confirmed/declined) are reclaimed by an opportunistic retention sweep so the
/// dictionary cannot grow unbounded.
/// </summary>
internal sealed class InMemoryChannelPromptMessageStore : IChannelPromptMessageStore
{
    /// <summary>
    /// Dictionary: transaction id (string form) → stored prompt entry.
    /// </summary>
    private readonly ConcurrentDictionary<string, StoredEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Clock the retention window is measured against.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an in-memory prompt message store.
    /// </summary>
    /// <param name="timeProvider">Time provider.</param>
    public InMemoryChannelPromptMessageStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Counter of <see cref="SaveAsync"/> calls that triggers a periodic retention sweep.
    /// </summary>
    private int _saveCallCount;

    /// <summary>
    /// Guard flag (0/1) ensuring at most one retention sweep runs at a time.
    /// </summary>
    private int _cleanupInProgress;

    /// <summary>
    /// Run the retention sweep every N <see cref="SaveAsync"/> calls.
    /// </summary>
    private const int CleanupEveryNCalls = 50;

    /// <summary>
    /// How long a stored entry is kept before the sweep treats it as stale. Set well above any
    /// realistic transaction TTL so a genuine expiry event always finds its entry.
    /// </summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public Task SaveAsync(TransactionId transactionId, ChannelPromptMessageRef reference, CancellationToken cancellationToken = default)
    {
        // Honor cancellation up front: don't leave an entry living until the retention sweep.
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        // A resent prompt supersedes the previous message for the same transaction.
        _entries[transactionId.ToString()] = new StoredEntry(reference, _timeProvider.GetUtcNow());

        // Periodically reclaim entries of transactions that never expired (confirmed/declined).
        var count = Interlocked.Increment(ref _saveCallCount);
        if (count % CleanupEveryNCalls is 0)
        {
            TryRemoveStaleEntries();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ChannelPromptMessageRef?> FindAsync(TransactionId transactionId, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ChannelPromptMessageRef?>(cancellationToken);
        }

        // A stale (over-retention) entry is treated as absent, as on a take; it is left for the sweep.
        if (_entries.TryGetValue(transactionId.ToString(), out var entry) && !IsStale(entry))
        {
            return Task.FromResult<ChannelPromptMessageRef?>(entry.Reference);
        }

        return Task.FromResult<ChannelPromptMessageRef?>(null);
    }

    /// <inheritdoc />
    public Task<ChannelPromptMessageRef?> TakeAsync(TransactionId transactionId, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ChannelPromptMessageRef?>(cancellationToken);
        }

        // Remove-on-read guarantees a single edit of the message; a stale (over-retention) entry is
        // treated as absent.
        if (_entries.TryRemove(transactionId.ToString(), out var entry) && !IsStale(entry))
        {
            return Task.FromResult<ChannelPromptMessageRef?>(entry.Reference);
        }

        return Task.FromResult<ChannelPromptMessageRef?>(null);
    }

    /// <inheritdoc />
    public Task<ChannelPromptMessageRef?> TakeForChannelAsync(
        TransactionId transactionId,
        string channelType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ChannelPromptMessageRef?>(cancellationToken);
        }

        var key = transactionId.ToString();

        // The removal names the very entry that was compared, not just its key: an entry another
        // channel saved between the read and the removal is a different value, so it is not removed
        // (SPEC-042 L62). A stale entry of this channel is removed and treated as absent, as on TakeAsync.
        if (_entries.TryGetValue(key, out var entry)
            && string.Equals(entry.Reference.ChannelType, channelType, StringComparison.Ordinal)
            && _entries.TryRemove(KeyValuePair.Create(key, entry))
            && !IsStale(entry))
        {
            return Task.FromResult<ChannelPromptMessageRef?>(entry.Reference);
        }

        return Task.FromResult<ChannelPromptMessageRef?>(null);
    }

    /// <inheritdoc />
    public Task SetPhoneRequestMessageAsync(
        TransactionId transactionId,
        string channelType,
        ChannelMessageRef requestMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(requestMessage);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var key = transactionId.ToString();

        // The slot is replaced only while it still holds the very entry that was compared: an entry
        // saved or taken in between is a different value, the replacement fails and nothing is
        // retried — the record is no longer the one the reference was meant for. The moment the
        // entry was stored travels over unchanged, so its retention does not start again. A stale
        // entry is not written to: no read would return it.
        if (_entries.TryGetValue(key, out var entry)
            && string.Equals(entry.Reference.ChannelType, channelType, StringComparison.Ordinal)
            && !IsStale(entry))
        {
            _entries.TryUpdate(
                key,
                entry with { Reference = entry.Reference with { PhoneRequestMessage = requestMessage } },
                entry);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns true when the entry is older than <see cref="Retention"/>.
    /// </summary>
    private bool IsStale(StoredEntry entry) => _timeProvider.GetUtcNow() - entry.StoredAt > Retention;

    /// <summary>
    /// Runs the retention sweep, but no more than one at a time (concurrent calls are discarded).
    /// </summary>
    private void TryRemoveStaleEntries()
    {
        if (Interlocked.CompareExchange(ref _cleanupInProgress, 1, 0) is not 0)
        {
            return;
        }

        try
        {
            foreach (var (key, entry) in _entries)
            {
                if (IsStale(entry))
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _cleanupInProgress, 0);
        }
    }

    /// <summary>
    /// Stored entry: the record plus the moment it was recorded (for retention).
    /// </summary>
    private readonly record struct StoredEntry(ChannelPromptMessageRef Reference, DateTimeOffset StoredAt);
}
