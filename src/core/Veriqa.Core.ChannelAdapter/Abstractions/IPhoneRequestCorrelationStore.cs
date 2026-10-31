// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Store of the phone number requests sent into chats, keyed by the chat — tenant, channel type and
/// channel user (SPEC-041 L44). One record per chat: a later request into the same chat replaces the
/// earlier one, so an answer always goes to the transaction of the last request. A record lives until
/// the terminal outcome receipt of its sign-in removes it, or until its
/// <see cref="PhoneRequestCorrelation.ExpiresAt"/>. It carries no reference to the request message:
/// that reference lives in the record of the chat of the transaction
/// (<see cref="ChannelPromptMessageRef.PhoneRequestMessage"/>), which outlives the deadline.
/// </summary>
/// <remarks>
/// The default implementation is in-process. The answer to a request can arrive on another instance
/// than the one that sent it, so a multi-instance deployment registers a shared implementation
/// (<c>UseRedisChannelStores</c>).
/// </remarks>
public interface IPhoneRequestCorrelationStore
{
    /// <summary>
    /// Records the request, replacing the record of the same chat.
    /// </summary>
    /// <param name="correlation">Record of the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(PhoneRequestCorrelation correlation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the record of a chat.
    /// </summary>
    /// <param name="tenantId">Tenant of the chat; null — the default implicit tenant.</param>
    /// <param name="channelType">Channel type of the chat.</param>
    /// <param name="channelUserId">User of the chat within the channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record; null — there is none, or it is past its <see cref="PhoneRequestCorrelation.ExpiresAt"/>.</returns>
    Task<PhoneRequestCorrelation?> FindAsync(
        string? tenantId,
        string channelType,
        string channelUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the record of a chat, but only while it still names the given transaction: a record a
    /// later request wrote for another transaction stays. The comparison and the removal are one
    /// operation of the store.
    /// </summary>
    /// <param name="tenantId">Tenant of the chat; null — the default implicit tenant.</param>
    /// <param name="channelType">Channel type of the chat.</param>
    /// <param name="channelUserId">User of the chat within the channel.</param>
    /// <param name="transactionId">Transaction the record must name to be removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveAsync(
        string? tenantId,
        string channelType,
        string channelUserId,
        TransactionId transactionId,
        CancellationToken cancellationToken = default);
}
