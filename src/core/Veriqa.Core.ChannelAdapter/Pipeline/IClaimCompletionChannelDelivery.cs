// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The one way claim completion puts a message into the chat of the user who confirmed the
/// transaction (SPEC-041 L44): the adapter of the channel of the transaction's identity snapshot, the
/// tenant of that snapshot, the outbound seam of the core with its content-kind gate, and the retries
/// of SPEC-003 §12.2 for a failed send. A step of its own is a new <see cref="ChannelMessageKind"/>
/// with a content property of <see cref="ChannelMessage"/>, delivered through this seam — never a
/// second delivery mechanism.
/// </summary>
internal interface IClaimCompletionChannelDelivery
{
    /// <summary>
    /// Delivers the message into the chat of the transaction's identity snapshot, retrying a failed
    /// send; a refusal of the content-kind gate is not retried.
    /// </summary>
    /// <param name="transaction">Transaction the message belongs to.</param>
    /// <param name="message">Message to deliver; its recipient is the user of the snapshot.</param>
    /// <param name="cancellationToken">Cancellation token — of the retries' pauses as well.</param>
    /// <returns>true — the adapter reported the message sent; false — it was not sent after the
    /// retries, the channel does not accept the content kind, or no adapter serves the channel.</returns>
    /// <exception cref="ArgumentException">The transaction has no channel identity snapshot, the
    /// message is addressed to somebody other than the user of the snapshot, or the content property
    /// of the message does not match its kind.</exception>
    Task<bool> DeliverAsync(Transaction transaction, ChannelMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <see cref="DeliverAsync"/> in the background — off the caller's thread, outside the path of
    /// the request and outside the queue of the transaction events — and returns at once. The
    /// operation is cancelled when the host stops.
    /// </summary>
    /// <remarks>
    /// An operation launched while the pipeline handles a channel press begins after that press has
    /// been answered, so its message follows the answer in the chat (SPEC-041 L91); launched anywhere
    /// else, it begins at once. A host that stops during the wait ends it as quietly as during the
    /// delivery.
    /// </remarks>
    /// <param name="transaction">Transaction the message belongs to.</param>
    /// <param name="message">Message to deliver.</param>
    /// <param name="onOutcome">Called with the outcome of the delivery and the stopping token of the
    /// host; an exception of the delivery (other than the cancellation by a stopping host) is logged as
    /// a warning and reported as false. Its own exception is logged as a warning. The cancellation by
    /// a stopping host ends the operation quietly — no warning, and no call once the delivery itself
    /// was cancelled. Nothing is thrown to the caller.</param>
    void DeliverInBackground(
        Transaction transaction,
        ChannelMessage message,
        Func<bool, CancellationToken, Task>? onOutcome = null);

    /// <summary>
    /// Runs <paramref name="work"/> on the same background executor as <see cref="DeliverInBackground"/>
    /// — off the caller's thread, outside the path of the request and outside the queue of the
    /// transaction events — and returns at once. It is the way for an outcome of claim completion that
    /// has no message to deliver to be carried out outside the caller, as an outcome after a delivery
    /// is carried out in <c>onOutcome</c>.
    /// </summary>
    /// <remarks>
    /// Like a delivery, work launched while the pipeline handles a channel press begins after that
    /// press has been answered (SPEC-041 L91); launched anywhere else, it begins at once.
    /// </remarks>
    /// <param name="work">Work to run, given the stopping token of the host. Its exception (other than
    /// the cancellation by a stopping host) is logged as a warning; the cancellation by a stopping host
    /// ends it quietly. Nothing is thrown to the caller.</param>
    void RunInBackground(Func<CancellationToken, Task> work);
}
