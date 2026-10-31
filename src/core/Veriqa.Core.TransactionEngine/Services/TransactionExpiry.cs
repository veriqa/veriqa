// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Constants;
using Veriqa.Core.TransactionEngine.Diagnostics;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// The one step that ends a transaction by its deadline: the transition into Expired, the write with
/// the concurrency token, the lifecycle record and <see cref="TransactionExpiredEvent"/>.
/// </summary>
/// <remarks>
/// Two writers take this step: the cleanup pass, which finds overdue transactions on its interval, and
/// the transaction service, which meets one when a decision arrives after the deadline (SPEC-001 §8.3).
/// Both go through this class, so an expiry looks the same to every subscriber whoever noticed it.
/// The class holds no state of its own and is created by each owner over the collaborators the owner
/// already has, so it takes on whatever lifetime the owner is registered with.
/// </remarks>
internal sealed class TransactionExpiry
{
    /// <summary>
    /// Transaction store.
    /// </summary>
    private readonly ITransactionStore _store;

    /// <summary>
    /// Event publisher.
    /// </summary>
    private readonly ITransactionEventPublisher _eventPublisher;

    /// <summary>
    /// Clock of the transition and of the event moment.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Lifecycle metrics.
    /// </summary>
    private readonly TransactionMetrics _metrics;

    /// <summary>
    /// Creates the expiry step over the collaborators of its owner.
    /// </summary>
    /// <param name="store">Transaction store.</param>
    /// <param name="eventPublisher">Event publisher.</param>
    /// <param name="timeProvider">Clock of the engine.</param>
    /// <param name="metrics">Lifecycle metrics.</param>
    public TransactionExpiry(
        ITransactionStore store,
        ITransactionEventPublisher eventPublisher,
        TimeProvider timeProvider,
        TransactionMetrics metrics)
    {
        _store = store;
        _eventPublisher = eventPublisher;
        _timeProvider = timeProvider;
        _metrics = metrics;
    }

    /// <summary>
    /// Moves an overdue transaction into Expired and announces it — when this call's write is the one
    /// that lands.
    /// </summary>
    /// <remarks>
    /// Nothing is written for a transaction its deadline does not end: one still inside its TTL, or one
    /// in a state the transition table does not expire (Confirmed among them — SPEC-001 §4.4 item 4).
    /// A write that loses the concurrency token is not an error either: another writer — the cleanup
    /// pass, a parallel decision — got there first, and announcing the expiry is the business of the
    /// writer whose record stands. So the event goes out exactly once per expiry, after the write and
    /// never before it.
    /// </remarks>
    /// <param name="transaction">Transaction as it was read from the store; its token is the one the
    /// write checks. The instance is updated in place on success.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true when this call wrote the expiry and published its event.</returns>
    public async Task<bool> TryExpireAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (!transaction.IsSubjectToExpiry() || !transaction.IsExpired(now))
        {
            return false;
        }

        var previousToken = transaction.ConcurrencyToken;
        var transitionResult = TransactionStateMachine.TryTransition(
            transaction, TransactionState.Expired, now, TransactionErrorCodes.TransactionExpired);

        if (transitionResult.IsFailure)
        {
            return false;
        }

        if (!await _store.UpdateAsync(transaction, previousToken, cancellationToken))
        {
            return false;
        }

        // After the transition actually landed in the store, not before: a refused transition or a
        // lost concurrency race above must not show up as an expiry that happened.
        _metrics.RecordTerminated(transaction, TransactionOutcomes.Expired, _timeProvider.GetUtcNow());

        await _eventPublisher.PublishAsync(
            new TransactionExpiredEvent
            {
                TransactionId = transaction.Id,
                OccurredAt = _timeProvider.GetUtcNow(),

                // The deadline the transaction ended at, not the clock of this write: whoever noticed
                // the expiry noticed it after the fact, and a subscriber that reports WHEN the
                // transaction ended must not be given the moment the writer happened to reach it. The
                // transition above leaves ExpiresAt as it was.
                ExpiresAt = transaction.ExpiresAt,
                ReasonCode = TransactionErrorCodes.TransactionExpired,
                Context = TransactionEventContext.FromTransaction(transaction)
            },
            cancellationToken);

        return true;
    }
}
