// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// When a transaction is waiting for its email address from the email completion (SPEC-041 C96): one
/// statement for the block of the web step, which shows and acts on it, and for the mail side, whose link
/// page offers the confirmation only while the address is still awaited.
/// </summary>
internal static class ClaimEmailItem
{
    /// <summary>
    /// The item of the email the web step obtains, when it is awaited: the transaction is in the claims
    /// completion, its current step is the web step and the email of that step has no outcome yet.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <returns>The open email item of the web step; null — the email is not awaited.</returns>
    public static ClaimCompletionItem? Open(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // The current step is known only while the transaction is in the completion, so the state is there.
        if (transaction.GetCurrentClaimCompletionStep() is not ClaimCompletionStep.WebStep
            || transaction.ClaimCompletion is not { } completion)
        {
            return null;
        }

        return completion.OpenItems().FirstOrDefault(item =>
            item.Step is ClaimCompletionStep.WebStep
            && VeriqaClaimTypes.NameComparer.Equals(item.Claim, VeriqaClaimTypes.Email));
    }
}
