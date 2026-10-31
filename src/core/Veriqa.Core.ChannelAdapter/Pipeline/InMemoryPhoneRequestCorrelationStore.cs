// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// In-process implementation of <see cref="IPhoneRequestCorrelationStore"/> — the default of a single
/// instance. A record answers until its <see cref="PhoneRequestCorrelation.ExpiresAt"/>; records
/// nothing removed before that are reclaimed by an opportunistic sweep, so the dictionary cannot grow
/// without bound.
/// </summary>
internal sealed class InMemoryPhoneRequestCorrelationStore : IPhoneRequestCorrelationStore
{
    /// <summary>
    /// Run the sweep of expired records every N <see cref="SaveAsync"/> calls.
    /// </summary>
    private const int CleanupEveryNCalls = 50;

    /// <summary>
    /// Records by chat.
    /// </summary>
    private readonly ConcurrentDictionary<ChatKey, PhoneRequestCorrelation> _records = new();

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
    public InMemoryPhoneRequestCorrelationStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public Task SaveAsync(PhoneRequestCorrelation correlation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        // The last request into a chat wins: it replaces whatever the chat was answering before.
        _records[ChatKey.Of(correlation.TenantId, correlation.ChannelType, correlation.ChannelUserId)] = correlation;

        if (Interlocked.Increment(ref _saveCallCount) % CleanupEveryNCalls is 0)
        {
            TryRemoveExpiredRecords();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PhoneRequestCorrelation?> FindAsync(
        string? tenantId,
        string channelType,
        string channelUserId,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<PhoneRequestCorrelation?>(cancellationToken);
        }

        var key = ChatKey.Of(tenantId, channelType, channelUserId);
        if (!_records.TryGetValue(key, out var record))
        {
            return Task.FromResult<PhoneRequestCorrelation?>(null);
        }

        if (IsExpired(record))
        {
            // Removed only while the slot still holds this very record: a request saved in between
            // for the same chat is a different value and stays.
            _records.TryRemove(KeyValuePair.Create(key, record));
            return Task.FromResult<PhoneRequestCorrelation?>(null);
        }

        return Task.FromResult<PhoneRequestCorrelation?>(record);
    }

    /// <inheritdoc />
    public Task RemoveAsync(
        string? tenantId,
        string channelType,
        string channelUserId,
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var key = ChatKey.Of(tenantId, channelType, channelUserId);

        // The removal names the very record that was compared, not just its key: a record a later
        // request wrote between the read and the removal is a different value, so it is not removed.
        if (_records.TryGetValue(key, out var record) && record.TransactionId == transactionId)
        {
            _records.TryRemove(KeyValuePair.Create(key, record));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether a record is past its deadline.
    /// </summary>
    /// <param name="record">Record to check.</param>
    /// <returns>true — the record no longer answers.</returns>
    private bool IsExpired(PhoneRequestCorrelation record) => _timeProvider.GetUtcNow() >= record.ExpiresAt;

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

    /// <summary>
    /// Key of a chat: the tenant in its single key spelling, the channel type and the channel user,
    /// all compared ordinally.
    /// </summary>
    /// <param name="Tenant">Tenant key segment.</param>
    /// <param name="ChannelType">Channel type.</param>
    /// <param name="ChannelUserId">Channel user.</param>
    private readonly record struct ChatKey(string Tenant, string ChannelType, string ChannelUserId)
    {
        /// <summary>
        /// Builds the key of a chat.
        /// </summary>
        /// <param name="tenantId">Tenant; null — the default implicit tenant.</param>
        /// <param name="channelType">Channel type.</param>
        /// <param name="channelUserId">Channel user.</param>
        /// <returns>The key.</returns>
        public static ChatKey Of(string? tenantId, string channelType, string channelUserId)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelType);
            ArgumentException.ThrowIfNullOrEmpty(channelUserId);

            return new ChatKey(TenantKey.Segment(tenantId), channelType, channelUserId);
        }
    }
}
