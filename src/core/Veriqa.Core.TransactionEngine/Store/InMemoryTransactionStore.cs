// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.TransactionEngine.Store;

/// <summary>
/// In-memory implementation of the transaction store.
/// Used for development and testing.
/// Data is kept in a ConcurrentDictionary and does not survive a process restart.
/// Stores copies of transactions (Detach / Materialize): primitive/value-type fields are copied by value,
/// AllowedChannelTypes — via ToFrozenSet (guaranteed immutable).
/// ClientContext is stored as a JSON string (string, immutable per the .NET contract).
/// Snapshot and context objects (sealed, init-only) are shared by reference; their mutable
/// collections (Claims, SlotValues, Expectations) must contain FrozenDictionary — this is ensured
/// by TransactionService when accepting data from external code.
/// Optimistic concurrency is implemented via an atomic compare-and-swap (TryUpdate).
/// The fields written only by conditional writes — hop observations, the surface, the outcomes and the
/// form token fingerprint of the claims completion — live in a cell shared by every version of a transaction's entry, so writing one
/// never replaces the entry UpdateAsync compares against. The gate of that cell also serializes the
/// replacement of the entry with the claims completion writes, whose condition reads the entry.
/// </summary>
internal sealed class InMemoryTransactionStore : ITransactionStore
{
    /// <summary>
    /// Storage: key — string representation of TransactionId, value — (Transaction, ConcurrencyToken,
    /// cell of the conditionally written fields). ConcurrencyToken is stored alongside the transaction
    /// copy for atomic compare-and-swap. The transaction copy carries neither hop observations nor the
    /// surface, outcomes and form token fingerprint of the claims completion: their only home is the cell, which UpdateAsync
    /// hands on from the replaced entry to the new one.
    /// </summary>
    private readonly ConcurrentDictionary<string, (Transaction Snapshot, string ConcurrencyToken, ConditionalFieldsCell Cell)> _entries = new();

    /// <summary>
    /// Idempotency index: tuple key (IdempotencyScope, IdempotencyKey) → TransactionId.
    /// The tuple key guarantees the absence of collisions, unlike string concatenation
    /// with a reserved separator (caller-controlled values may contain it).
    /// Provides an atomic uniqueness check in AddAsync via TryAdd,
    /// eliminating the TOCTOU race condition between GetByIdempotencyKeyAsync and AddAsync.
    /// Analogous to the UNIQUE INDEX (IdempotencyScope, IdempotencyKey) in EF Core / the Lua script in Redis.
    /// </summary>
    private readonly ConcurrentDictionary<(string Scope, string Key), string> _idempotencyIndex = new();

    /// <summary>
    /// Atomic counter of the number of entries.
    /// Used to check the limit without a race condition between Count and TryAdd.
    /// </summary>
    private int _count;

    /// <summary>
    /// Transaction Engine configuration monitor.
    /// </summary>
    private readonly IOptionsMonitor<TransactionEngineOptions> _options;

    /// <summary>
    /// Maximum batch size for batch operations (in-memory store).
    /// Matches the upper bound of CleanupBatchSize in TransactionEngineOptionsValidator.
    /// </summary>
    private const int MaxBatchSize = 1000;

    /// <summary>
    /// Creates an instance of the in-memory transaction store.
    /// </summary>
    /// <param name="options">Transaction Engine configuration monitor.</param>
    public InMemoryTransactionStore(IOptionsMonitor<TransactionEngineOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        // Validate the input BEFORE incrementing the counter to avoid polluting _count
        ArgumentNullException.ThrowIfNull(transaction);

        // Atomic uniqueness check of the idempotency key.
        // Eliminates a TOCTOU race condition: without this check, two parallel requests
        // with the same (IdempotencyScope, IdempotencyKey) could both pass
        // GetByIdempotencyKeyAsync → AddAsync, since the linear scan over _entries
        // does not guarantee insertion atomicity.
        // Analogous to the UNIQUE INDEX in EF Core and the idem_exists Lua script in Redis.
        if (transaction.IdempotencyScope is not null && transaction.IdempotencyKey is not null)
        {
            var idempotencyCompositeKey = (transaction.IdempotencyScope, transaction.IdempotencyKey);
            if (!_idempotencyIndex.TryAdd(idempotencyCompositeKey, transaction.Id.ToString()))
            {
                throw new DuplicateIdempotencyKeyException(
                    transaction.IdempotencyScope,
                    transaction.IdempotencyKey);
            }
        }

        // Atomically reserve a slot via Interlocked.Increment —
        // eliminates the race condition between the limit check and TryAdd.
        var maxEntries = _options.CurrentValue.MaxInMemoryEntries;
        var newCount = Interlocked.Increment(ref _count);
        if (newCount > maxEntries)
        {
            Interlocked.Decrement(ref _count);
            // Roll back the entry in the idempotency index
            if (transaction.IdempotencyScope is not null && transaction.IdempotencyKey is not null)
            {
                _idempotencyIndex.TryRemove((transaction.IdempotencyScope, transaction.IdempotencyKey), out _);
            }
            throw new StoreCapacityExceededException(maxEntries);
        }

        // Store a copy of the transaction.
        // Use TryAdd to honor add semantics — do not overwrite an already existing entry.
        var key = transaction.Id.ToString();
        var snapshot = Detach(transaction);

        var cell = new ConditionalFieldsCell(transaction.HopObservations, transaction.ClaimCompletionProgress);

        if (!_entries.TryAdd(key, (snapshot, snapshot.ConcurrencyToken, cell)))
        {
            Interlocked.Decrement(ref _count);
            // Roll back the entry in the idempotency index
            if (transaction.IdempotencyScope is not null && transaction.IdempotencyKey is not null)
            {
                _idempotencyIndex.TryRemove((transaction.IdempotencyScope, transaction.IdempotencyKey), out _);
            }
            throw new InvalidOperationException(
                $"Transaction with ID '{key}' already exists in the store");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Transaction?> GetByIdAsync(TransactionId id, CancellationToken cancellationToken = default)
    {
        // Return a copy so that external mutation does not affect the store
        var key = id.ToString();
        if (_entries.TryGetValue(key, out var entry))
        {
            return Task.FromResult<Transaction?>(Materialize(entry));
        }

        return Task.FromResult<Transaction?>(null);
    }

    /// <inheritdoc />
    public async Task<Transaction?> GetByIdempotencyKeyAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        // First look up the index — it is reserved atomically in AddAsync BEFORE the insert into _entries.
        // This eliminates the TOCTOU: if a concurrent thread's AddAsync has already reserved the key,
        // we will see the index entry and wait for the transaction to appear in _entries.
        if (_idempotencyIndex.TryGetValue((scope, key), out var transactionId)
            && _entries.TryGetValue(transactionId, out var indexedEntry))
        {
            return Materialize(indexedEntry);
        }

        if (transactionId is not null)
        {
            // The index already contains an entry, but _entries is still empty —
            // a concurrent AddAsync is between the index TryAdd and the _entries TryAdd.
            // Briefly wait for the transaction to appear (bounded spin-wait).
            // At most 50 attempts × 1ms = 50ms — comfortably more than the synchronous
            // code between the two TryAdd calls inside AddAsync needs.
            for (var i = 0; i < 50; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                if (_entries.TryGetValue(transactionId, out var entry))
                {
                    return Materialize(entry);
                }
            }
            // The entry is missing — the concurrent AddAsync failed between the reservation and the insert
            // (e.g., StoreCapacityExceededException). The index rollback has already happened
            // or will happen; return null here — the caller will get ConcurrencyConflict.
        }

        return null;
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(Transaction transaction, string expectedConcurrencyToken, CancellationToken cancellationToken = default)
    {
        // Atomic compare-and-swap via ConcurrentDictionary.TryUpdate
        var key = transaction.Id.ToString();

        if (!_entries.TryGetValue(key, out var existing))
        {
            return Task.FromResult(false);
        }

        // Check optimistic concurrency
        if (!string.Equals(existing.ConcurrencyToken, expectedConcurrencyToken, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        // Create a new entry with a copy of the transaction. The conditionally written fields are not
        // taken from the passed transaction: the new entry keeps the cell of the replaced one.
        var newSnapshot = Detach(transaction);
        var newEntry = (newSnapshot, newSnapshot.ConcurrencyToken, existing.Cell);

        // Atomic CAS: update only if the current value matches the expected one. Under the gate of the
        // cell: a claims completion write reads the entry under the same gate, so the state it checks
        // cannot change between its check and its write.
        bool swapped;
        lock (existing.Cell.Gate)
        {
            swapped = _entries.TryUpdate(key, newEntry, existing);
        }

        return Task.FromResult(swapped);
    }

    /// <inheritdoc />
    public Task DeleteAsync(TransactionId id, CancellationToken cancellationToken = default)
    {
        // Remove the transaction from the store and the idempotency index
        var key = id.ToString();
        if (_entries.TryRemove(key, out var removed))
        {
            Interlocked.Decrement(ref _count);

            // Clear the idempotency index for the removed transaction
            if (removed.Snapshot.IdempotencyScope is not null && removed.Snapshot.IdempotencyKey is not null)
            {
                _idempotencyIndex.TryRemove((removed.Snapshot.IdempotencyScope, removed.Snapshot.IdempotencyKey), out _);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Transaction>> GetExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default)
    {
        // Look for the transactions the passing of their deadline ends. Asking the transaction
        // itself rather than "not terminal": a confirmed one is neither terminal nor ended by its
        // deadline, and selecting it here would spend a slot of the batch on a record the caller
        // cannot move.
        var effectiveBatchSize = Math.Min(batchSize, MaxBatchSize);

        var expired = _entries.Values
            .Where(e => e.Snapshot.IsSubjectToExpiry() && e.Snapshot.IsExpired(now))
            .Take(effectiveBatchSize)
            .Select(Materialize)
            .ToList();

        return Task.FromResult<IReadOnlyList<Transaction>>(expired);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Transaction>> GetStaleTerminalAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken = default)
    {
        // Look for terminal transactions updated earlier than olderThan
        var effectiveBatchSize = Math.Min(batchSize, MaxBatchSize);

        var stale = _entries.Values
            .Where(e => e.Snapshot.IsTerminal() && e.Snapshot.UpdatedAt < olderThan)
            .Take(effectiveBatchSize)
            .Select(Materialize)
            .ToList();

        return Task.FromResult<IReadOnlyList<Transaction>>(stale);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Transaction>> GetStalledConfirmedAsync(DateTimeOffset confirmedBefore, int batchSize, CancellationToken cancellationToken = default)
    {
        // Look for Confirmed transactions confirmed earlier than confirmedBefore
        var effectiveBatchSize = Math.Min(batchSize, MaxBatchSize);

        var stalled = _entries.Values
            .Where(e => e.Snapshot.State is TransactionState.Confirmed && e.Snapshot.UpdatedAt < confirmedBefore)
            .Take(effectiveBatchSize)
            .Select(Materialize)
            .ToList();

        return Task.FromResult<IReadOnlyList<Transaction>>(stalled);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Transaction>> GetPendingSignInsAsync(SignInSubjectScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // The state and everything the scope is read from live on the stored copy itself, not in
        // the cell of the conditionally written fields, so the filter runs before materialization.
        var pending = _entries.Values
            .Where(e => e.Snapshot.State is TransactionState.Pending && SignInSubjectScope.Of(e.Snapshot) == scope)
            .Select(Materialize)
            .ToList();

        return Task.FromResult<IReadOnlyList<Transaction>>(pending);
    }

    /// <inheritdoc />
    public Task<bool> TryMarkHopObservationAsync(TransactionId id, HopObservationMark mark, DateTimeOffset markedAt, CancellationToken cancellationToken = default)
    {
        HopObservationArguments.ThrowIfUndefined(mark);

        return Task.FromResult(
            _entries.TryGetValue(id.ToString(), out var entry)
            && entry.Cell.TryMark(mark, markedAt));
    }

    /// <inheritdoc />
    public Task<bool> TryAttachScannerContextAsync(TransactionId id, ScannerContextSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        HopObservationArguments.ThrowIfInvalid(snapshot);

        return Task.FromResult(
            _entries.TryGetValue(id.ToString(), out var entry)
            && entry.Cell.TryAttachScannerContext(snapshot));
    }

    /// <inheritdoc />
    public Task<bool> TryFillScannerFingerprintAsync(TransactionId id, string fingerprint, CancellationToken cancellationToken = default)
    {
        HopObservationArguments.ThrowIfInvalidFingerprint(fingerprint);

        return Task.FromResult(
            _entries.TryGetValue(id.ToString(), out var entry)
            && entry.Cell.TryFillScannerFingerprint(fingerprint));
    }

    /// <inheritdoc />
    public Task<bool> TrySetWebStepSurfaceAsync(
        TransactionId id,
        WebStepSurface? expected,
        WebStepSurface surface,
        CancellationToken cancellationToken = default)
    {
        ClaimCompletionWrites.ThrowIfUndefined(expected, surface);

        if (!ClaimCompletionWrites.IsSurfaceMove(expected, surface))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(WriteClaimCompletion(id, (transaction, progress) =>
            transaction.ClaimCompletion is not null && progress.Surface == expected
                ? progress with { Surface = surface }
                : null));
    }

    /// <inheritdoc />
    public Task<bool> TrySetClaimFormTokenHashAsync(
        TransactionId id,
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        ClaimCompletionWrites.ThrowIfInvalidFormTokenHash(tokenHash);

        return Task.FromResult(WriteClaimCompletion(id, (transaction, progress) =>
            transaction.ClaimCompletion is not null && progress.FormTokenHash is null
                ? progress with { FormTokenHash = tokenHash }
                : null));
    }

    /// <inheritdoc />
    public Task<bool> TryRecordClaimResultAsync(
        TransactionId id,
        ClaimCompletionResult result,
        CancellationToken cancellationToken = default)
    {
        ClaimCompletionWrites.ThrowIfInvalid(result);

        return Task.FromResult(WriteClaimCompletion(id, (transaction, progress) =>
            ClaimCompletionWrites.CanRecord(
                transaction.State,
                transaction.ClaimCompletion?.Items,
                progress.Results,
                result.Claim)
                ? progress with { Results = ClaimCompletionWrites.Append(progress.Results, result) }
                : null));
    }

    /// <summary>
    /// Writes the surface, the outcomes or the form token fingerprint of the claims completion of a Pending
    /// transaction under the gate of its cell. The entry is read again under the gate: <see cref="UpdateAsync"/> replaces
    /// entries only while holding that same gate, so the state checked here stays the stored one until
    /// the write is done.
    /// </summary>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="write">
    /// The progress after the write, or null when the condition of the write does not hold — over the
    /// stored transaction (without its progress) and the current progress.
    /// </param>
    /// <returns>true — written by this call; false — the condition does not hold or nothing is stored.</returns>
    private bool WriteClaimCompletion(
        TransactionId id,
        Func<Transaction, ClaimCompletionProgress, ClaimCompletionProgress?> write)
    {
        var key = id.ToString();

        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        lock (entry.Cell.Gate)
        {
            if (!_entries.TryGetValue(key, out var current)
                || !ReferenceEquals(current.Cell, entry.Cell)
                || current.Snapshot.State is not TransactionState.Pending)
            {
                return false;
            }

            return entry.Cell.TryWriteProgressLocked(progress => write(current.Snapshot, progress));
        }
    }

    /// <summary>
    /// Copies a transaction into the store, leaving its conditionally written fields behind — the cell
    /// of the entry is their only home, and a copy carrying them too would be a second, stale one.
    /// </summary>
    /// <remarks>
    /// The copy isolates the store from external mutation. Primitive/value-type fields are copied by
    /// value, AllowedChannelTypes — via ToFrozenSet (inside <see cref="Transaction.Restore"/>),
    /// ClientContext is a string (immutable). Snapshot and context objects (sealed, init-only) are
    /// shared by reference; their collections (Claims, SlotValues, Expectations) are guaranteed
    /// immutable (FrozenDictionary, ensured by TransactionService).
    /// <para>
    /// The copy goes through the published snapshot path — the same one a store written outside the
    /// Veriqa assemblies uses — instead of a hand-written list of assignments. That list was the one
    /// place in the engine where a field newly added to <see cref="Transaction"/> was lost without a
    /// compilation error and without a failure at run time: this store calls no mapper, so nothing
    /// else covered it. With <c>required</c> members on the snapshot the same omission now stops the
    /// build.
    /// </para>
    /// </remarks>
    /// <param name="source">Source transaction.</param>
    /// <returns>Copy of the transaction without the conditionally written fields.</returns>
    private static Transaction Detach(Transaction source)
        => Transaction.Restore(source.ToSnapshot(default(HopObservations), ClaimCompletionProgress.None));

    /// <summary>
    /// The one path a stored entry leaves the store by: a fresh copy of the transaction with the
    /// current values of its conditionally written fields laid over it.
    /// </summary>
    /// <param name="entry">Stored entry.</param>
    /// <returns>Copy of the transaction as it stands now.</returns>
    private static Transaction Materialize(
        (Transaction Snapshot, string ConcurrencyToken, ConditionalFieldsCell Cell) entry)
    {
        var (observations, progress) = entry.Cell.Read();

        return Transaction.Restore(entry.Snapshot.ToSnapshot(observations, progress));
    }

    /// <summary>
    /// Fields of one transaction written only by conditional writes — the hop observations, the surface,
    /// the outcomes and the form token fingerprint of the claims completion — shared by every version of its entry. Each write
    /// checks its condition and writes under one lock, so the condition and the write are one step and
    /// of two concurrent writes to a field exactly one succeeds.
    /// </summary>
    private sealed class ConditionalFieldsCell
    {
        /// <summary>
        /// Guards <see cref="_values"/> and <see cref="_progress"/>; held by the store while it replaces
        /// the entry the cell belongs to.
        /// </summary>
        public Lock Gate { get; } = new();

        /// <summary>
        /// Current observation values.
        /// </summary>
        private HopObservations _values;

        /// <summary>
        /// Current surface, outcomes and form token fingerprint of the claims completion.
        /// </summary>
        private ClaimCompletionProgress _progress;

        /// <summary>
        /// Creates the cell with the values the transaction was added with.
        /// </summary>
        /// <param name="initial">Initial observation values.</param>
        /// <param name="progress">Initial surface, outcomes and form token fingerprint of the claims completion.</param>
        public ConditionalFieldsCell(HopObservations initial, ClaimCompletionProgress progress)
        {
            _values = initial;
            _progress = progress;
        }

        /// <summary>
        /// Reads the current values as one consistent value.
        /// </summary>
        /// <returns>Current observation values and claims completion progress.</returns>
        public (HopObservations Observations, ClaimCompletionProgress Progress) Read()
        {
            lock (Gate)
            {
                return (_values, _progress);
            }
        }

        /// <summary>
        /// Replaces the claims completion progress when the write accepts the current one. The caller
        /// holds <see cref="Gate"/>.
        /// </summary>
        /// <param name="write">The progress after the write, or null when its condition does not hold.</param>
        /// <returns>true — written; false — the condition does not hold.</returns>
        public bool TryWriteProgressLocked(Func<ClaimCompletionProgress, ClaimCompletionProgress?> write)
        {
            if (write(_progress) is not { } written)
            {
                return false;
            }

            _progress = written;
            return true;
        }

        /// <summary>
        /// Writes a mark if it is still empty.
        /// </summary>
        /// <param name="mark">Mark to write (a defined value).</param>
        /// <param name="markedAt">Moment the mark carries.</param>
        /// <returns>true — written by this call; false — already there.</returns>
        public bool TryMark(HopObservationMark mark, DateTimeOffset markedAt)
        {
            lock (Gate)
            {
                switch (mark)
                {
                    case HopObservationMark.FirstResolve when _values.FirstResolvedAt is null:
                        _values = _values with { FirstResolvedAt = markedAt };
                        return true;
                    case HopObservationMark.InterstitialOpened when _values.InterstitialOpenedAt is null:
                        _values = _values with { InterstitialOpenedAt = markedAt };
                        return true;
                    case HopObservationMark.ChannelLaunched when _values.ChannelLaunchedAt is null:
                        _values = _values with { ChannelLaunchedAt = markedAt };
                        return true;
                    case HopObservationMark.RepeatResolve when _values.RepeatResolvedAt is null:
                        _values = _values with { RepeatResolvedAt = markedAt };
                        return true;
                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// Attaches the scanner snapshot if none is attached yet.
        /// </summary>
        /// <param name="snapshot">Snapshot to attach (without a fingerprint).</param>
        /// <returns>true — attached by this call; false — a snapshot is already there.</returns>
        public bool TryAttachScannerContext(ScannerContextSnapshot snapshot)
        {
            lock (Gate)
            {
                if (_values.ScannerContextSnapshot is not null)
                {
                    return false;
                }

                _values = _values with { ScannerContextSnapshot = snapshot };
                return true;
            }
        }

        /// <summary>
        /// Fills the fingerprint of the attached snapshot if it is still empty.
        /// </summary>
        /// <param name="fingerprint">Fingerprint to write.</param>
        /// <returns>true — written by this call; false — no snapshot, or a fingerprint is already there.</returns>
        public bool TryFillScannerFingerprint(string fingerprint)
        {
            lock (Gate)
            {
                var snapshot = _values.ScannerContextSnapshot;
                if (snapshot is null || snapshot.Fingerprint is not null)
                {
                    return false;
                }

                _values = _values with { ScannerContextSnapshot = snapshot.WithFingerprint(fingerprint) };
                return true;
            }
        }
    }
}
