// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// The one place an address the email completion obtained goes to the transaction — for the block of the web
/// step, which confirms it by a code or saves it without verification, and for the mail side, whose link page
/// and inbound mail confirm it elsewhere. An address obtained while other fields of the web step are still open
/// is held by the mail side, and the transaction records it when the form is finished: until then the user may
/// change it, and the recorded outcomes of the transaction never shrink. An address that is the last thing the
/// web step waits for is recorded at once, and its write ends the step.
/// </summary>
internal static class ClaimEmailWrites
{
    /// <summary>
    /// Records an obtained address, or holds it while other fields of the web step are open.
    /// </summary>
    /// <remarks>
    /// The address is recorded at once when the email is the last open item of the web step, when there is
    /// no mail side to hold it, when the transaction no longer awaits the email (the write then gives the
    /// refusal), and when holding it fails — the user then cannot change it, but the sign-in goes on. The
    /// decision is made on the transaction the caller read, which the form may have changed since: the fields
    /// written, the email skipped, the sign-in ended. So a held address is checked against the transaction
    /// read again after it — one the transaction no longer awaits is let go and the write gives the refusal,
    /// and one that became the last open item is recorded at once.
    /// </remarks>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="email">Mail side of the email completion; null — none.</param>
    /// <param name="transaction">The transaction as read by the caller.</param>
    /// <param name="obtained">The address and how it was obtained.</param>
    /// <param name="now">Current moment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The write of the address, or the transaction as read with the address held.</returns>
    public static async Task<ClaimEmailWrite> RecordOrHoldAsync(
        ITransactionService transactionService,
        IClaimCompletionEmail? email,
        Transaction transaction,
        ClaimEmailHeldAddress obtained,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transactionService);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(obtained);

        if (email is null
            || ClaimEmailItem.Open(transaction) is null
            || IsLastOpen(transaction)
            || transaction.HasRunOutOfTime(now)
            || !await email.HoldAddressAsync(transaction, obtained, cancellationToken))
        {
            return new ClaimEmailWrite(await RecordAsync(transactionService, transaction, obtained, cancellationToken), Held: false);
        }

        var reread = await transactionService.GetTransactionAsync(transaction.Id, cancellationToken);

        // Not read again: the address stays held, and the form records it when it is finished.
        if (reread.IsFailure)
        {
            return new ClaimEmailWrite(Result<Transaction>.Success(transaction), Held: true);
        }

        var current = reread.Value;

        if (ClaimEmailItem.Open(current) is null)
        {
            await email.HoldAddressAsync(current, held: null, cancellationToken);
            return new ClaimEmailWrite(await RecordAsync(transactionService, current, obtained, cancellationToken), Held: false);
        }

        return IsLastOpen(current)
            ? new ClaimEmailWrite(await RecordAsync(transactionService, current, obtained, cancellationToken), Held: false)
            : new ClaimEmailWrite(Result<Transaction>.Success(current), Held: true);
    }

    /// <summary>
    /// Records the address held for the transaction: the form is finished, or nothing else is left open.
    /// </summary>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="held">The held address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The write of the address.</returns>
    public static Task<Result<Transaction>> RecordAsync(
        ITransactionService transactionService,
        Transaction transaction,
        ClaimEmailHeldAddress held,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transactionService);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(held);

        return transactionService.RecordClaimValueAsync(
            transaction.Id,
            ClaimEmailItem.Open(transaction)?.Claim ?? VeriqaClaimTypes.Email,
            held.Address,
            held.Verified,
            EmailCompletionMethods.ToCollectionMethod(held.Method),
            cancellationToken);
    }

    /// <summary>
    /// Whether the email is the last item the web step of the transaction still waits for, so its write ends
    /// the step.
    /// </summary>
    /// <param name="transaction">The transaction, its email awaited.</param>
    /// <returns><c>true</c> when no other item of the web step is open.</returns>
    public static bool IsLastOpen(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return transaction.ClaimCompletion?.OpenItems().All(item =>
            item.Step is not ClaimCompletionStep.WebStep
            || VeriqaClaimTypes.NameComparer.Equals(item.Claim, VeriqaClaimTypes.Email)) is not false;
    }
}

/// <summary>
/// Outcome of <see cref="ClaimEmailWrites.RecordOrHoldAsync"/>.
/// </summary>
/// <param name="Outcome">The write of the address; when <paramref name="Held"/>, the transaction as read.</param>
/// <param name="Held">Whether the address is held instead of recorded.</param>
internal readonly record struct ClaimEmailWrite(Result<Transaction> Outcome, bool Held);
