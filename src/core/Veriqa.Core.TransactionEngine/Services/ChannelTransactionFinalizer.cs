// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Constants;
using Veriqa.Core.TransactionEngine.Diagnostics;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Identity;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// The one place that finalizes a transaction from channel data: confirm → resolve identity →
/// complete, with compensation when either of the two steps after the confirmation fails.
/// </summary>
/// <remarks>
/// The sequence touches no channel: it drives the transaction state machine and the identity
/// resolution, both of which belong to the engine, so it lives with them rather than in the channel
/// assembly its callers happen to come from. That keeps every path that finalizes a sign-in — the
/// webhook path (auto-confirmation and the in-channel button) and the core confirmation page, which
/// answers for a transaction whose channel data arrived earlier — on one implementation, and lets a
/// future path reach it without depending on the channel assembly. A second copy of this sequence
/// would be a second place to keep the compensation correct. Static with explicit dependencies: the
/// webhook path is itself static and already carries the services (its public entry points take them
/// as parameters), so a DI service would have to be threaded through signatures that must not change.
/// </remarks>
public static class ChannelTransactionFinalizer
{
    /// <summary>
    /// Confirms and completes the transaction (Pending → Confirmed → Completed).
    /// </summary>
    /// <param name="identity">Channel identity snapshot of the confirming user.</param>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="identityResolutionService">Channel identity resolution service.</param>
    /// <param name="concurrencyToken">Concurrency token of the current transaction.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The completed transaction — with <c>ResolvedIdentitySnapshot</c> and
    /// <c>CompletionSnapshot</c> populated, ready to be signed in from — or the error of the step
    /// that failed. Callers must use the returned instance: the one they held before the call is the
    /// pre-finalization state and carries neither snapshot.
    /// </returns>
    /// <remarks>
    /// What an error leaves behind differs by step, and callers that must not leave a transaction
    /// hanging have to know which: a refusal at the confirmation step leaves nothing to compensate here —
    /// it is not Confirmed, and whatever that step itself wrote is listed in the remarks of
    /// <see cref="ITransactionService.ConfirmTransactionAsync"/>: a refusal by the deadline
    /// (<see cref="TransactionErrorCodes.TransactionExpired"/>) has already moved a Created or Pending
    /// transaction into Expired and published its <c>TransactionExpiredEvent</c>, a required claim nothing
    /// can obtain (<see cref="TransactionErrorCodes.RequiredClaimUnobtainable"/>) has already failed it, and
    /// a refusal while missing claims are still open
    /// (<see cref="TransactionErrorCodes.ClaimCompletionInProgress"/>) keeps it Pending, though the claims
    /// completion may have written it first — unless taking a vanished stored value off found its required
    /// claim unobtainable and failed the transaction (SPEC-041 E86); a refusal at the identity
    /// resolution step and a failure at the completion step are both compensated here, because by
    /// then the transaction is already Confirmed and would otherwise stall. The two compensated
    /// steps differ only in the code the transaction is failed with — <c>identity_not_resolved</c>
    /// after a refused resolution, <c>downstream_finalize_failed</c> after a failed completion —
    /// while the error returned to the caller stays the one the failing step produced.
    /// <para>
    /// Claims completion (SPEC-041 L91): when the confirmation leaves the transaction waiting for
    /// missing claims (<see cref="Transaction.IsCompletingClaims"/>), the sequence stops there and
    /// returns that transaction — it is not confirmed, and resolving or completing it would sign in
    /// without the claims. The identity is resolved from the snapshot of the CONFIRMED transaction, not
    /// from <paramref name="identity"/>: the confirmation that ends a claims completion confirms with the
    /// snapshot the transaction froze.
    /// </para>
    /// </remarks>
    public static async Task<Result<Transaction>> ConfirmAndCompleteAsync(
        ChannelIdentitySnapshot identity,
        TransactionId transactionId,
        ITransactionService transactionService,
        IIdentityResolutionService identityResolutionService,
        string concurrencyToken,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Span of the whole finalization sequence — the step where the sign-in actually completes.
        // It is an operation, not a state transition: a repeated delivery of the same channel event
        // legitimately produces a second span, while the lifecycle counters stay single-count.
        using var activity = TransactionActivitySource.Source.StartActivity(TransactionTelemetry.CompleteActivityName);
        activity?.SetTag(TransactionTelemetry.TransactionIdTag, transactionId.ToString());

        // Confirm the transaction (Pending → Confirmed)
        var confirmResult = await transactionService.ConfirmTransactionAsync(
            transactionId,
            identity,
            concurrencyToken,
            cancellationToken);

        if (confirmResult.IsFailure)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(ChannelTelemetry.ErrorCodeTag, confirmResult.Error.Code);

            logger.LogWarning(
                "Failed to confirm the transaction. TransactionId: {TransactionId}, Error: {ErrorCode}",
                transactionId,
                confirmResult.Error.Code);
            return confirmResult;
        }

        var confirmedTx = confirmResult.Value;

        // The confirmation set the claims completion mark instead of confirming: nothing to resolve or
        // complete yet — the completion check finalizes the transaction once the claims are in.
        if (confirmedTx.IsCompletingClaims())
        {
            logger.LogInformation(
                "Transaction waits for missing claims; finalization deferred. TransactionId: {TransactionId}",
                transactionId);
            return confirmResult;
        }

        // Resolve the identity via IIdentityResolutionService (TASK-002):
        // it stores the ChannelIdentity and builds the ResolvedIdentitySnapshot. The snapshot is the one
        // the confirmation stored — the passed one is not it when a claims completion ends.
        var resolveResult = await identityResolutionService.ResolveAsync(
            confirmedTx.ChannelIdentitySnapshot ?? identity,
            cancellationToken);

        if (resolveResult.IsFailure)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(ChannelTelemetry.ErrorCodeTag, resolveResult.Error.Code);

            logger.LogWarning(
                "Failed to resolve the identity. TransactionId: {TransactionId}, Error: {ErrorCode}",
                transactionId,
                resolveResult.Error.Code);

            // The transaction is already Confirmed at this point, so a refusal here stalls it exactly
            // the way a failed CompleteTransaction does — and is compensated by the same step. The
            // code it is failed with is the one that names this step: the identity could not be
            // resolved, not the downstream system could not finalize.
            await CompensateToFailedAsync(
                transactionId,
                transactionService,
                TransactionErrorCodes.IdentityNotResolved,
                confirmedTx.ConcurrencyToken,
                logger);

            // The resolver's own error travels out: it is the only party that knows why the identity
            // was refused, whereas the code the compensation just wrote states something else — why
            // the transaction was moved to Failed.
            return Result<Transaction>.Failure(resolveResult.Error);
        }

        var resolvedIdentity = resolveResult.Value;

        // Complete the transaction (Confirmed → Completed)
        var completeResult = await transactionService.CompleteTransactionAsync(
            transactionId,
            resolvedIdentity,
            confirmedTx.ConcurrencyToken,
            cancellationToken);

        if (completeResult.IsFailure)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag(ChannelTelemetry.ErrorCodeTag, completeResult.Error.Code);

            logger.LogWarning(
                "Failed to complete the transaction. TransactionId: {TransactionId}, Error: {ErrorCode}",
                transactionId,
                completeResult.Error.Code);

            // Compensating action: move the transaction from Confirmed to Failed
            // so it is not left in a stalled state.
            // CancellationToken.None — cleanup runs even if the request is cancelled.
            await CompensateToFailedAsync(
                transactionId,
                transactionService,
                TransactionErrorCodes.DownstreamFinalizeFailed,
                confirmedTx.ConcurrencyToken,
                logger);

            return completeResult;
        }

        logger.LogInformation(
            "Transaction completed. TransactionId: {TransactionId}, SubjectHash: {SubjectHash}",
            transactionId,
            LogMasking.Fingerprint(resolvedIdentity.Subject));

        return completeResult;
    }

    /// <summary>
    /// Compensates a stalled transaction by moving it to Failed after a step that already found it
    /// Confirmed could not carry it further.
    /// If the initial attempt failed because of a stale concurrency token (the failing step lost a
    /// race — the token had already moved), re-reads the current state and retries
    /// the compensation once with a fresh token while the transaction is in a non-terminal state.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="errorCode">
    /// Code the transaction is failed with — it names the step that stalled, which is what the audit
    /// trail and the telemetry of the failed transaction are read for.
    /// </param>
    /// <param name="concurrencyToken">Concurrency token at the time of the first attempt.</param>
    /// <param name="logger">Logger.</param>
    private static async Task CompensateToFailedAsync(
        TransactionId transactionId,
        ITransactionService transactionService,
        string errorCode,
        string concurrencyToken,
        ILogger logger)
    {
        var failResult = await transactionService.FailTransactionAsync(
            transactionId,
            errorCode,
            concurrencyToken,
            CancellationToken.None);

        if (failResult.IsSuccess)
        {
            return;
        }

        // A concurrency conflict is possible: the token is stale. Re-read and retry once.
        var rereadResult = await transactionService.GetTransactionAsync(transactionId, CancellationToken.None);
        if (rereadResult.IsSuccess
            && rereadResult.Value.State is TransactionState.Pending or TransactionState.Confirmed)
        {
            var retryFail = await transactionService.FailTransactionAsync(
                transactionId,
                errorCode,
                rereadResult.Value.ConcurrencyToken,
                CancellationToken.None);

            if (retryFail.IsSuccess)
            {
                return;
            }

            logger.LogError(
                "Failed to roll back the stalled transaction to Failed (retry with a fresh token). " +
                "TransactionId: {TransactionId}, Error: {ErrorCode}",
                transactionId,
                retryFail.Error.Code);
            return;
        }

        logger.LogError(
            "Failed to roll back the stalled transaction to Failed. " +
            "TransactionId: {TransactionId}, CompensationCode: {CompensationCode}, Error: {ErrorCode}",
            transactionId,
            errorCode,
            failResult.Error.Code);
    }
}
