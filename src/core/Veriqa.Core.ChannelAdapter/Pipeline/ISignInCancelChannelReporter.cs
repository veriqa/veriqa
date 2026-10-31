// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Shows the refusal recorded by the cancel button of a page — "Cancel sign-in" of the sign-in window,
/// "Cancel confirmation" of the entry page of a confirmation, and "Cancel sign-in" or "Cancel confirmation"
/// of the scanner page of the unified hop QR code (SPEC-003 CA-235) — in the chat where the
/// question of that transaction still stands before the user: the terminal <c>declined</c> notice, worded
/// by the type of the transaction and sent through the same seam the refusal in the channel and the expiry
/// are shown through, so the adapter takes the buttons of the question away where its platform can. The
/// receipt states the refusal only, never a sign-in (SPEC-039 R51); a press in the chat that comes later is
/// still answered by the receipt of the recorded refusal (SPEC-003 CA-192).
/// </summary>
/// <remarks>
/// The reporter decides itself whether there is anything to show; both cancel paths — the one of the
/// sign-in window and the one of the pages held by the session identifier — only call it, for either type.
/// The question stands before the user when the transaction, as read before the refusal was written, still
/// waited for the answer in the channel (<see cref="Transaction.IsAwaitingAnswerInChannel"/>), and the
/// record of the chat states that the question was shown there. A sign-in in the claims completion is
/// reported by <see cref="IClaimCompletionOutcomeReporter"/>; a refusal on the question page of the core
/// never passes the first condition and is shown by <see cref="IWebConfirmationReceiptReporter"/>.
/// </remarks>
internal interface ISignInCancelChannelReporter
{
    /// <summary>
    /// Sends the receipt of the refusal into the chat of the record of the transaction, when the
    /// question stood there; never throws, never changes the answer to the client.
    /// </summary>
    /// <param name="beforeRefusal">The transaction the cancel path decided on, read before the refusal
    /// was written.</param>
    /// <param name="refused">The transaction as the successful write of the refusal returned it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed operation.</returns>
    Task ReportAsync(Transaction beforeRefusal, Transaction refused, CancellationToken cancellationToken = default);
}
