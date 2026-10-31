// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Shows the channel the terminal outcome of a sign-in whose claim completion ended outside the
/// processing of a channel event (SPEC-003 CA-142, SPEC-041 L91): on the completion form, by the link
/// or the mail of the email completion, by the background end of the web step, by "Cancel sign-in" of
/// the sign-in window. The processing of a channel event shows its outcomes itself.
/// </summary>
/// <remarks>
/// Every call stands at the write that ended the transaction, where the post-completion action is
/// performed as well — and before that action, which follows the receipt (SPEC-012 §4.9). That makes
/// one report per ended sign-in the regular case, not a guarantee: the completion check hands a
/// transaction another writer has just ended to the writer that reads it next, so two writes racing
/// at the very end may each report it — the second receipt then repeats the first.
/// </remarks>
internal interface IClaimCompletionOutcomeReporter
{
    /// <summary>
    /// Sends the receipt of the outcome into the chat of the user of the transaction's channel
    /// snapshot, as a new message; never throws, never changes the sign-in result. A transaction that
    /// has not ended, or ended by running out of time, is not reported here — an expiry is shown by
    /// its own handler. Neither is one that never entered the claim completion: a caller whose path
    /// also ends sign-ins outside it — a refusal that may come before the confirmation — calls
    /// unconditionally, and such a sign-in is left to <see cref="ISignInCancelChannelReporter"/>, which
    /// shows a sign-in window cancel in the chat where the question still stands, and to
    /// <see cref="IWebConfirmationReceiptReporter"/>, which shows the "No" of the web confirmation page.
    /// </summary>
    /// <param name="ended">The transaction as the write that ended it returned it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed operation.</returns>
    Task ReportAsync(Transaction ended, CancellationToken cancellationToken = default);
}
