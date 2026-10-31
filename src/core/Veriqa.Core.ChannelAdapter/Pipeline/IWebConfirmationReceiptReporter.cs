// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Shows the answer given on a web confirmation page of the core in the chat the transaction was
/// entered from — the chat the notice of the hand-over to that page went to (SPEC-012 CFG-266): the
/// terminal <c>confirmed</c> receipt of a "Yes", the <c>declined</c> receipt of a "No". A sign-in and a
/// confirmation are answered alike; the address of the receipt names the type, so only the words
/// differ. The receipt is a new message — the core keeps no reference to the notice, and no question
/// stood in the chat — and it names no value of the calling party, since the question was never asked
/// there (SPEC-039 R92).
/// </summary>
/// <remarks>
/// The caller is the write of the answer, and it calls only after that write succeeded on a transaction
/// that, read before the write, awaited the answer of the page
/// (<see cref="Transaction.IsAwaitingWebConfirmation"/>): a store is free to hand out the very instance
/// it keeps, so that fact cannot be read off the transaction afterwards. The cancel button of the sign-in
/// window or of the entry page of a confirmation is not such an answer and does not call. The reporter decides the rest itself: an outcome
/// other than <c>confirmed</c> / <c>declined</c> and a channel that shows no outcome notices — Email,
/// which gets no notice either — are told nothing. A "Yes" that starts the claims completion of a
/// sign-in is not reported here: the sign-in has not ended, and its outcome is shown by
/// <see cref="IClaimCompletionOutcomeReporter"/> once it does.
/// </remarks>
internal interface IWebConfirmationReceiptReporter
{
    /// <summary>
    /// Sends the receipt of the answer into the chat of the channel snapshot of the transaction; never
    /// throws, never changes the answer to the browser.
    /// </summary>
    /// <param name="decided">The transaction as the successful write of the answer returned it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed operation.</returns>
    Task ReportAsync(Transaction decided, CancellationToken cancellationToken = default);
}
