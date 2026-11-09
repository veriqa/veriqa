// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The chats the record of a phone number request is kept for (SPEC-041 L44). The request goes out under the
/// tenant of the channel snapshot, through the bot that tenant resolves to — its own or the one of the
/// installation. The answer arrives on the route of that bot: the one with the tenant segment, whose events
/// carry the tenant, or the one without it, whose events carry none until a transaction is read (SPEC-003
/// CA-164). The record is always kept for the chat of the tenant of the snapshot. It is kept for the chat
/// without a tenant as well only for a transaction started on a route without a tenant — the chat record of
/// the transaction (<see cref="ChannelPromptMessageRef.TenantId"/>), written with the tenant of the route of
/// the start, says so: its answer arrives on that route too. A transaction started on the route of its tenant
/// is answered there, so a record without a tenant would only take the chat of the installation bot away from
/// a transaction that waits in it.
/// </summary>
internal static class PhoneRequestCorrelationChats
{
    /// <summary>
    /// Records the request for every chat its answer may arrive in.
    /// </summary>
    /// <remarks>
    /// The core writes the chat record of the transaction at every start in a channel that can ask for the
    /// phone number, so the record names the channel of the snapshot and the route of its last start. The
    /// request is kept for the chat of the tenant alone only when the route of the answer is unknown — that
    /// record is missing or names another chat: its write or its read failed, or the transaction was
    /// confirmed in a channel it was not last started from — or when the write of the request for the chat
    /// without a tenant fails. The answer arriving on a route without a tenant then finds nothing, and the step
    /// waits as for an unanswered request; the request itself still goes out.
    /// </remarks>
    /// <param name="store">Store of the phone number requests sent into chats.</param>
    /// <param name="chatStore">Store of the chat each transaction was started from.</param>
    /// <param name="correlation">Record of the request, keyed by the tenant of the channel snapshot.</param>
    /// <param name="logger">
    /// Logger a failed read of the chat record, or a failed write of the request for the chat without a tenant,
    /// is reported to.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed write.</returns>
    internal static async Task SaveAsync(
        IPhoneRequestCorrelationStore store,
        IChannelPromptMessageStore chatStore,
        PhoneRequestCorrelation correlation,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await store.SaveAsync(correlation, cancellationToken);

        if (correlation.TenantId is null)
        {
            return;
        }

        ChannelPromptMessageRef? chat;
        try
        {
            chat = await chatStore.FindAsync(correlation.TransactionId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Failed to read the chat record of the transaction — the phone number request is kept for the chat of its tenant only. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                correlation.TransactionId.ToString(),
                correlation.ChannelType);
            return;
        }

        if (chat is { TenantId: null }
            && string.Equals(chat.ChannelType, correlation.ChannelType, StringComparison.Ordinal)
            && string.Equals(chat.ChannelUserId, correlation.ChannelUserId, StringComparison.Ordinal))
        {
            try
            {
                await store.SaveAsync(correlation with { TenantId = null }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "Failed to keep the phone number request for the chat without a tenant — it is kept for the chat of its tenant only. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                    correlation.TransactionId.ToString(),
                    correlation.ChannelType);
            }
        }
    }

    /// <summary>
    /// Removes the record of the request of a transaction from every chat it may have been kept for — in each
    /// only while it still names that transaction.
    /// </summary>
    /// <remarks>
    /// The removal does not ask the chat record whether the record without a tenant was written: by the time
    /// of the receipt the chat record may already be taken, and a removal bound to the transaction leaves the
    /// record of another transaction in place.
    /// </remarks>
    /// <param name="store">Store of the phone number requests sent into chats.</param>
    /// <param name="snapshot">Channel snapshot of the transaction.</param>
    /// <param name="transactionId">Transaction whose request is over.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed removal.</returns>
    internal static async Task RemoveAsync(
        IPhoneRequestCorrelationStore store,
        ChannelIdentitySnapshot snapshot,
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        await store.RemoveAsync(
            snapshot.TenantId, snapshot.ChannelType, snapshot.ChannelUserId, transactionId, cancellationToken);

        if (snapshot.TenantId is not null)
        {
            await store.RemoveAsync(
                tenantId: null, snapshot.ChannelType, snapshot.ChannelUserId, transactionId, cancellationToken);
        }
    }
}
