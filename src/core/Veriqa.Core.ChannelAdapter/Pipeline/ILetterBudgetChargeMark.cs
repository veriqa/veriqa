// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The marks by which a letter costs its sender one attempt of the budget of the confirmation operation
/// however many times it reaches the gate — two parallel sendings of the page of one link, a redelivery of
/// the same inbound mail, a retry of a mail its source was asked to hand in again.
/// </summary>
/// <remarks>
/// Two marks per pair "transaction × letter": the claim of the pass that takes the budget, and the
/// outcome "the budget let the letter through", set only after the limiter allowed it. A later pass goes
/// on without the budget only on the outcome: a claim without one is a pass still in progress, or one the
/// budget refused, and must not let the letter through. Implemented by the channel whose letters these
/// are: it knows what identifies a letter and keeps the marks in a store of its own. Both marks are set
/// atomically — out of concurrent calls for one pair exactly one claims — and neither is ever lifted.
/// </remarks>
internal interface ILetterBudgetChargeMark
{
    /// <summary>
    /// Claims the budget of the pair "transaction × letter" for this pass, unless another pass has
    /// claimed it already.
    /// </summary>
    /// <param name="transactionId">Transaction the letter answers.</param>
    /// <param name="letterKey">What identifies the letter within its channel; not kept in clear text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true — this pass is the first of the letter and takes the budget; false — another pass of
    /// the letter has claimed it.</returns>
    Task<bool> TryClaimChargeAsync(
        TransactionId transactionId,
        string letterKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records that the budget let the letter through: its later passes go on without the budget.
    /// </summary>
    /// <param name="transactionId">Transaction the letter answers.</param>
    /// <param name="letterKey">What identifies the letter within its channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed recording.</returns>
    Task MarkAllowedAsync(
        TransactionId transactionId,
        string letterKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the budget has let the letter through on the pass that claimed it.
    /// </summary>
    /// <param name="transactionId">Transaction the letter answers.</param>
    /// <param name="letterKey">What identifies the letter within its channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true — the letter was let through; false — its first pass is still in progress, or the
    /// budget refused it.</returns>
    Task<bool> IsAllowedAsync(
        TransactionId transactionId,
        string letterKey,
        CancellationToken cancellationToken);
}
