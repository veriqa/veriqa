// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Store of the chat a transaction was started from, keyed by transaction (SPEC-003 §4.5): the
/// recipient, the channel, and the optional references to the confirmation prompt and to the phone
/// number request sent into that chat. Lets a background lifecycle handler (TTL expiry) tell the
/// user the transaction expired, and the cancel button of the sign-in window or of the entry page of a
/// confirmation tell the user, whose question still stands in the chat, the transaction was declined —
/// each addressing the exact messages
/// that need it: replacing the prompt, taking away what the phone number request offered.
///
/// The core writes a record for every channel that reports outcomes or can update its own message,
/// whether the question was asked in the chat or not; a channel that does neither leaves none. A
/// missing entry means "nobody to tell in a channel" and the reader is a graceful no-op.
///
/// The default implementation is in-process (single instance / self-hosted / demo). In a
/// multi-instance deployment the expiry event may be handled on an instance other than the one
/// that wrote the record; then the notice is skipped (graceful). A shared implementation
/// (<c>UseRedisChannelStores</c>) makes the record visible to every instance.
/// </summary>
public interface IChannelPromptMessageStore
{
    /// <summary>
    /// Records the chat of the transaction and the references it has so far. Overwrites a prior entry
    /// for the same transaction as a whole (a resent prompt supersedes the previous message).
    /// </summary>
    /// <param name="transactionId">Transaction the record belongs to.</param>
    /// <param name="reference">The record to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(TransactionId transactionId, ChannelPromptMessageRef reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the stored prompt coordinates for the transaction and leaves them in the store: the
    /// read of a notice that is not the last word about the transaction, whose coordinates a later
    /// take still has to find. Returns null when nothing is stored.
    /// </summary>
    /// <param name="transactionId">Transaction to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored coordinates, or null.</returns>
    Task<ChannelPromptMessageRef?> FindAsync(TransactionId transactionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically retrieves and removes the stored prompt coordinates for the transaction.
    /// Returns null when nothing was stored (no record was written for the transaction, the entry
    /// already consumed, or it was evicted). Remove-on-read prevents a second edit of the same message.
    /// </summary>
    /// <param name="transactionId">Transaction to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored coordinates, or null.</returns>
    Task<ChannelPromptMessageRef?> TakeAsync(TransactionId transactionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically retrieves and removes the stored prompt coordinates for the transaction only when
    /// they were stored by the given channel type (<see cref="ChannelPromptMessageRef.ChannelType"/>,
    /// compared ordinally). Coordinates stored by another channel are left in place, untouched, and
    /// the call returns null — as it does when nothing is stored. The comparison and the removal are
    /// one operation of the store: an entry another channel writes in between is never removed
    /// (SPEC-042 L62). There is deliberately no default body — a take followed by a put-back would
    /// open exactly that window.
    /// </summary>
    /// <param name="transactionId">Transaction to look up.</param>
    /// <param name="channelType">Channel type the coordinates must have been stored by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored coordinates of that channel, or null.</returns>
    Task<ChannelPromptMessageRef?> TakeForChannelAsync(
        TransactionId transactionId,
        string channelType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the reference to the sent phone number request into the stored record of the
    /// transaction, but only when the record was stored by the given channel type
    /// (<see cref="ChannelPromptMessageRef.ChannelType"/>, compared ordinally). Every other field of
    /// the record and its lifetime stay as they are; a repeated call replaces the reference. No
    /// record, a record of another channel, or a record that changed between the comparison and the
    /// write — nothing is written, no record is created and nothing is retried. The comparison and
    /// the write are one operation of the store; there is deliberately no default body — a read
    /// followed by a save would overwrite whatever was stored in between.
    /// </summary>
    /// <param name="transactionId">Transaction the request was sent for.</param>
    /// <param name="channelType">Channel type the record must have been stored by.</param>
    /// <param name="requestMessage">Reference to the request message the platform returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetPhoneRequestMessageAsync(
        TransactionId transactionId,
        string channelType,
        ChannelMessageRef requestMessage,
        CancellationToken cancellationToken = default);
}
