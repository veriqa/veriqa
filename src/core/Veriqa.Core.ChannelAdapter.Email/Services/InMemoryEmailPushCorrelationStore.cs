// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;

namespace Veriqa.Core.ChannelAdapter.Email.Services;

/// <summary>
/// In-memory implementation of the correlation token store for Email Push mode.
/// Thread-safe implementation built on <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Suitable for single-instance and dev environments. For multi-instance — use Redis.
/// </summary>
internal sealed class InMemoryEmailPushCorrelationStore : IEmailPushCorrelationStore
{
    /// <summary>
    /// Dictionary: correlation token → correlation data.
    /// </summary>
    private readonly ConcurrentDictionary<string, EmailPushCorrelation> _correlations =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Index: transaction id → the correlation token currently live for that transaction.
    /// Backs the idempotency of <see cref="StoreOrReuseAsync"/>: repeated renders of the sign-in page
    /// reuse the token already issued instead of minting a new one for every render.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _activeTokenByTransaction =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Dictionary of processed message-ids: message-id → registration timestamp (UTC).
    /// Used for idempotent deduplication of repeated email delivery.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _processedMessageIds =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Counter of StoreAsync operations for periodic cleanup of expired entries.
    /// </summary>
    private int _storeCallCount;

    /// <summary>
    /// Counter of message-id registrations for periodic cleanup of stale deduplication entries.
    /// The inbound path calls only <see cref="TryRegisterProcessedMessageAsync"/>, so
    /// cleanup must also be triggered from here — otherwise the dictionary would grow unbounded (review feedback).
    /// </summary>
    private int _registerCallCount;

    /// <summary>
    /// Guard flag (0/1) ensuring that at most one <see cref="RemoveExpiredEntries"/> cleanup
    /// runs at any given time. Both paths (Store/Register) may satisfy the modulo condition
    /// simultaneously, so concurrent cleanups are discarded (review feedback).
    /// </summary>
    private int _cleanupInProgress;

    /// <summary>
    /// Run cleanup of expired entries every N StoreAsync calls.
    /// </summary>
    private const int CleanupEveryNCalls = 50;

    /// <summary>
    /// Retention duration of processed message-id records for deduplication.
    /// Once this window elapses, a record is considered stale and removed during cleanup.
    /// </summary>
    private static readonly TimeSpan ProcessedMessageRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// Clock the correlation expiry and the deduplication retention are judged against.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an in-memory correlation store.
    /// </summary>
    /// <param name="timeProvider">Time provider.</param>
    public InMemoryEmailPushCorrelationStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task StoreAsync(string token, EmailPushCorrelation correlation, CancellationToken cancellationToken = default)
    {
        // Honor cancellation up front: on RequestAborted we don't pollute the dictionary with an entry living until TTL (review feedback)
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        // The method stores the correlation in the dictionary (random 256-bit token — collisions are excluded)
        _correlations[token] = correlation;

        // Periodically clean up expired entries to prevent unbounded memory growth
        var count = Interlocked.Increment(ref _storeCallCount);
        if (count % CleanupEveryNCalls is 0)
        {
            TryRemoveExpiredEntries();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> StoreOrReuseAsync(string token, EmailPushCorrelation correlation, CancellationToken cancellationToken = default)
    {
        // Honor cancellation up front, same as StoreAsync: on RequestAborted we don't pollute the dictionary
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<string>(cancellationToken);
        }

        // The method registers the token for its transaction idempotently. The order of operations matters:
        // the correlation is written first (provisionally) and only then the index is captured atomically —
        // so a token reachable through the index is always present in _correlations, and the loser of a race
        // withdraws its own provisional entry. Otherwise two parallel renders would leave two live tokens.
        var effectiveToken = StoreOrReuseCore(token, correlation);

        // The Push sign-in registers its tokens through this method and never calls StoreAsync, so the
        // periodic cleanup has to be triggered from here as well — otherwise expired entries would never
        // be swept on that path
        var count = Interlocked.Increment(ref _storeCallCount);
        if (count % CleanupEveryNCalls is 0)
        {
            TryRemoveExpiredEntries();
        }

        return Task.FromResult(effectiveToken);
    }

    /// <summary>
    /// Returns the token live for the transaction of <paramref name="correlation"/>: either an already
    /// registered one, or <paramref name="token"/> once it has won the index.
    /// </summary>
    /// <param name="token">Freshly minted candidate token.</param>
    /// <param name="correlation">Correlation data to register under the candidate token.</param>
    /// <returns>The token that must be used by the caller.</returns>
    private string StoreOrReuseCore(string token, EmailPushCorrelation correlation)
    {
        var transactionId = correlation.TransactionId;

        while (true)
        {
            var hasIndexEntry = _activeTokenByTransaction.TryGetValue(transactionId, out var indexedToken);
            EmailPushCorrelation? indexed = null;

            // Step 1: the transaction already has a live token — return it and write nothing.
            // The TTL of the reused token is deliberately not extended (see the SPI contract)
            if (hasIndexEntry
                && _correlations.TryGetValue(indexedToken!, out indexed)
                && !indexed.IsExpired(_timeProvider.GetUtcNow())
                && !indexed.IsConsumed)
            {
                return indexedToken!;
            }

            // Step 2: provisional write, then an atomic capture of the index — either the first entry
            // for the transaction, or a replacement of exactly the stale value that was read above
            _correlations[token] = correlation;

            var captured = hasIndexEntry
                ? _activeTokenByTransaction.TryUpdate(transactionId, token, indexedToken!)
                : _activeTokenByTransaction.TryAdd(transactionId, token);

            if (captured)
            {
                // The replaced token is expired (a consumed one is removed by TryConsumeAsync): drop its
                // record right away instead of waiting for the periodic sweep, so the transaction never
                // holds more than one record. Removal is by pair — the exact value read above — so a
                // concurrent consumption of that very entry is not overwritten by this cleanup
                if (indexed is not null)
                {
                    _correlations.TryRemove(
                        new KeyValuePair<string, EmailPushCorrelation>(indexedToken!, indexed));
                }

                return token;
            }

            // Step 3: race lost — withdraw the provisional entry so no orphaned live token is left behind,
            // then re-read the winner
            _correlations.TryRemove(token, out _);
        }
    }

    /// <inheritdoc />
    public Task<EmailPushCorrelation?> GetAsync(string token, CancellationToken cancellationToken = default)
    {
        // The method looks up the correlation in the dictionary and, if the entry is still present, returns its data
        // (the caller checks the IsExpired/IsConsumed flags itself). Expired entries are periodically
        // removed in TryRemoveExpiredEntries(), so null means "not found OR already cleaned up" —
        // "expired" cannot be distinguished from "absent" via GetAsync. The caller must treat
        // null the same way in both cases (as the absence of a valid correlation) and must not rely on
        // null meaning "never existed".
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<EmailPushCorrelation?>(cancellationToken);
        }

        if (!_correlations.TryGetValue(token, out var data))
        {
            return Task.FromResult<EmailPushCorrelation?>(null);
        }

        return Task.FromResult<EmailPushCorrelation?>(data);
    }

    /// <inheritdoc />
    public async Task<bool> TryConsumeAsync(string token, CancellationToken cancellationToken = default)
    {
        // The method atomically marks the correlation as consumed via the shared CAS helper,
        // then removes it — a single-use correlation token is no longer needed after consumption
        var consumed = await ConcurrentCasHelper.TryUpdateWithRetryAsync(
            _correlations,
            token,
            canUpdate: current => !current.IsExpired(_timeProvider.GetUtcNow()) && !current.IsConsumed,
            update: current => current with { ConsumedAt = _timeProvider.GetUtcNow() },
            cancellationToken);

        if (consumed)
        {
            // Correlation consumed — remove it from the store (single use)
            _correlations.TryRemove(token, out var removed);

            // Drop the index entry as a pair, so that a token already issued for the same transaction
            // by a later render is not wiped out along with the consumed one
            if (removed is not null)
            {
                _activeTokenByTransaction.TryRemove(
                    new KeyValuePair<string, string>(removed.TransactionId, token));
            }
        }

        return consumed;
    }

    /// <inheritdoc />
    public Task<bool> IsMessageProcessedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        // Read-only deduplication check: does not register the message-id, only reports
        // whether it was processed before. Used for early short-circuiting of repeated delivery.
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        return Task.FromResult(_processedMessageIds.ContainsKey(messageId));
    }

    /// <inheritdoc />
    public Task<bool> TryRegisterProcessedMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        // Honor cancellation up front: during mass cancellations we don't spawn "extra" deduplication entries (review feedback)
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        // The method atomically registers the message-id: TryAdd returns false if the email was already processed
        var added = _processedMessageIds.TryAdd(messageId, _timeProvider.GetUtcNow());

        // Periodically clean up stale entries: the inbound path calls only this method,
        // so without cleanup here the _processedMessageIds dictionary would grow unbounded (review feedback)
        var count = Interlocked.Increment(ref _registerCallCount);
        if (count % CleanupEveryNCalls is 0)
        {
            TryRemoveExpiredEntries();
        }

        return Task.FromResult(added);
    }

    /// <summary>
    /// Runs <see cref="RemoveExpiredEntries"/>, but no more than one cleanup at a time.
    /// If a cleanup is already running on another thread, the current call is simply discarded —
    /// stale entries will be removed on the next scheduled run.
    /// </summary>
    private void TryRemoveExpiredEntries()
    {
        // Atomically "acquire" the right to clean up; 0→1 means we are the sole executor
        if (Interlocked.CompareExchange(ref _cleanupInProgress, 1, 0) is not 0)
        {
            return;
        }

        try
        {
            RemoveExpiredEntries();
        }
        finally
        {
            Volatile.Write(ref _cleanupInProgress, 0);
        }
    }

    /// <summary>
    /// Removes all expired correlation tokens and stale message-id records from the dictionaries.
    /// </summary>
    private void RemoveExpiredEntries()
    {
        // The method iterates over all entries and removes expired correlation tokens
        var now = _timeProvider.GetUtcNow();
        foreach (var (key, value) in _correlations)
        {
            if (value.IsExpired(now))
            {
                _correlations.TryRemove(key, out _);
            }
        }

        // Drop index entries whose token is gone (consumed and removed) or expired. Removal is done by
        // pair, so a token registered concurrently for the same transaction survives the sweep
        foreach (var (transactionId, token) in _activeTokenByTransaction)
        {
            if (!_correlations.TryGetValue(token, out var correlation) || correlation.IsExpired(now))
            {
                _activeTokenByTransaction.TryRemove(new KeyValuePair<string, string>(transactionId, token));
            }
        }

        // Remove stale records of processed message-ids
        var threshold = now - ProcessedMessageRetention;
        foreach (var (key, registeredAt) in _processedMessageIds)
        {
            if (registeredAt < threshold)
            {
                _processedMessageIds.TryRemove(key, out _);
            }
        }
    }
}
