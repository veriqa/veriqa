// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Store;

/// <summary>
/// Decorator over the store the engine composed: refuses a write that would move a transaction
/// along a transition the state machine does not allow, and passes every other call straight
/// through.
/// </summary>
/// <remarks>
/// It lives in the composition rather than inside the shipped stores on purpose. A check written
/// into <c>InMemoryTransactionStore</c>, <c>EfCoreTransactionStore</c> and the Redis store would
/// leave an integrator who plugged in a store of their own without it, and that is the case the
/// guard exists for: a transaction can reach a forbidden state without the state machine ever being
/// touched — <see cref="Transaction.Restore"/> rebuilds one from a snapshot carrying any state at
/// all.
/// </remarks>
internal class StateTransitionGuardingTransactionStore : ITransactionStore, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The store the engine composed; every call ends up here.
    /// </summary>
    private readonly ITransactionStore _inner;

    /// <summary>
    /// Whether disposing the guard must dispose the inner store as well.
    /// </summary>
    private readonly bool _ownsInner;

    /// <summary>
    /// Guards against disposing the inner store twice.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Creates the guard over a store.
    /// </summary>
    /// <param name="inner">Store to guard.</param>
    /// <param name="ownsInner">Whether disposing the guard must dispose <paramref name="inner"/>.</param>
    protected StateTransitionGuardingTransactionStore(ITransactionStore inner, bool ownsInner)
    {
        _inner = inner;
        _ownsInner = ownsInner;
    }

    /// <summary>
    /// Wraps a store with the guard, preserving the statements the store makes about itself.
    /// </summary>
    /// <remarks>
    /// <see cref="ISupportsNativeExpiry"/> is read off the resolved instance by the cleanup service,
    /// so a wrapper that dropped it would make the cleanup service start deleting records a backend
    /// with its own TTL already takes care of. An already guarded store is returned as it is: the
    /// engine registered twice must not produce two nested guards.
    /// </remarks>
    /// <param name="inner">Store to guard.</param>
    /// <param name="ownsInner">
    /// Whether the guard is responsible for disposing <paramref name="inner"/>. True for a
    /// registration whose result the container would have released itself — it now realizes the
    /// guard instead, and without this the inner store would lose that release. False for a store
    /// handed to the container as a ready instance: that one belongs to whoever created it, and the
    /// container never released it before either. The guard stands in for the ONE registration the
    /// engine replaced, no more: a store also registered under a concrete type of its own is still
    /// released by that registration too, exactly as it was before the guard was put in front of it.
    /// </param>
    /// <returns>The guarded store.</returns>
    internal static ITransactionStore Wrap(ITransactionStore inner, bool ownsInner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (inner is StateTransitionGuardingTransactionStore)
        {
            return inner;
        }

        return inner is ISupportsNativeExpiry
            ? new NativeExpiryGuard(inner, ownsInner)
            : new StateTransitionGuardingTransactionStore(inner, ownsInner);
    }

    /// <inheritdoc />
    public Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
        => _inner.AddAsync(transaction, cancellationToken);

    /// <inheritdoc />
    public Task<Transaction?> GetByIdAsync(TransactionId id, CancellationToken cancellationToken = default)
        => _inner.GetByIdAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<Transaction?> GetByIdempotencyKeyAsync(string scope, string key, CancellationToken cancellationToken = default)
        => _inner.GetByIdempotencyKeyAsync(scope, key, cancellationToken);

    /// <inheritdoc />
    /// <exception cref="InvalidStateTransitionException">
    /// The stored transaction cannot reach the state carried by <paramref name="transaction"/>.
    /// </exception>
    public async Task<bool> UpdateAsync(
        Transaction transaction,
        string expectedConcurrencyToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // The state the store holds right now is the only honest source of "where the transaction is
        // coming from": the caller's copy may have been transitioned, restored from a snapshot or
        // built by outside code, and none of that says what was actually persisted.
        var stored = await _inner.GetByIdAsync(transaction.Id, cancellationToken);

        // Two cases the guard has nothing to say about, both answered by the inner store with false:
        // nothing stored (a race with deletion or cleanup, not a broken invariant), and a caller
        // holding a stale copy. In the second one the "transition" the guard would see is an artefact
        // of the copy being out of date — the write is about to lose the optimistic lock anyway, and
        // the caller's answer to that, re-read and retry, terminates: the re-read copy carries the
        // state actually stored, and the state machine refuses an impossible move on it up front.
        // Only a caller whose token still matches is genuinely writing a forbidden transition, and
        // that is the one refused ahead of the optimistic-lock check.
        if (stored is not null
            && string.Equals(stored.ConcurrencyToken, expectedConcurrencyToken, StringComparison.Ordinal)
            && stored.State != transaction.State
            && !TransactionStateMachine.CanTransition(stored.State, transaction.State))
        {
            throw new InvalidStateTransitionException(transaction.Id, stored.State, transaction.State);
        }

        return await _inner.UpdateAsync(transaction, expectedConcurrencyToken, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(TransactionId id, CancellationToken cancellationToken = default)
        => _inner.DeleteAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Transaction>> GetExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default)
        => _inner.GetExpiredAsync(now, batchSize, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Transaction>> GetStaleTerminalAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken = default)
        => _inner.GetStaleTerminalAsync(olderThan, batchSize, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Transaction>> GetStalledConfirmedAsync(DateTimeOffset confirmedBefore, int batchSize, CancellationToken cancellationToken = default)
        => _inner.GetStalledConfirmedAsync(confirmedBefore, batchSize, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Transaction>> GetPendingSignInsAsync(SignInSubjectScope scope, CancellationToken cancellationToken = default)
        => _inner.GetPendingSignInsAsync(scope, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryMarkHopObservationAsync(TransactionId id, HopObservationMark mark, DateTimeOffset markedAt, CancellationToken cancellationToken = default)
        => _inner.TryMarkHopObservationAsync(id, mark, markedAt, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryAttachScannerContextAsync(TransactionId id, ScannerContextSnapshot snapshot, CancellationToken cancellationToken = default)
        => _inner.TryAttachScannerContextAsync(id, snapshot, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryFillScannerFingerprintAsync(TransactionId id, string fingerprint, CancellationToken cancellationToken = default)
        => _inner.TryFillScannerFingerprintAsync(id, fingerprint, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TrySetWebStepSurfaceAsync(TransactionId id, WebStepSurface? expected, WebStepSurface surface, CancellationToken cancellationToken = default)
        => _inner.TrySetWebStepSurfaceAsync(id, expected, surface, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TrySetClaimFormTokenHashAsync(TransactionId id, string tokenHash, CancellationToken cancellationToken = default)
        => _inner.TrySetClaimFormTokenHashAsync(id, tokenHash, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryRecordClaimResultAsync(TransactionId id, ClaimCompletionResult result, CancellationToken cancellationToken = default)
        => _inner.TryRecordClaimResultAsync(id, result, cancellationToken);

    /// <summary>
    /// Disposes the inner store the guard owns, mirroring what the container would have done to it
    /// had the registration not been replaced by one returning this wrapper.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_ownsInner)
        {
            return;
        }

        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
            return;
        }

        // Same refusal the container itself raises for a service that can only be disposed
        // asynchronously: blocking on DisposeAsync here would deadlock as readily as it does there.
        if (_inner is IAsyncDisposable)
        {
            throw new InvalidOperationException(
                $"'{_inner.GetType()}' type only implements IAsyncDisposable. Use DisposeAsync to dispose the container.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_ownsInner)
        {
            return;
        }

        if (_inner is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
            return;
        }

        (_inner as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The same guard over a store whose backend removes terminal records itself — it carries the
    /// marker onwards so the cleanup service keeps seeing the store's own statement.
    /// </summary>
    private sealed class NativeExpiryGuard : StateTransitionGuardingTransactionStore, ISupportsNativeExpiry
    {
        /// <summary>
        /// Creates the guard over a store with native expiry.
        /// </summary>
        /// <param name="inner">Store to guard.</param>
        /// <param name="ownsInner">Whether disposing the guard must dispose <paramref name="inner"/>.</param>
        public NativeExpiryGuard(ITransactionStore inner, bool ownsInner)
            : base(inner, ownsInner)
        {
        }
    }
}
