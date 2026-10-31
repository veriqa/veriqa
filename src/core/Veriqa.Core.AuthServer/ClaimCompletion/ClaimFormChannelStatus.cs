// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;

namespace Veriqa.Core.AuthServer.ClaimCompletion;

/// <summary>
/// The one point the channel status <see cref="ClaimCompletionChannelStatusKeys.FormOnPhone"/> is published
/// through (SPEC-041 L77): the existing <see cref="TransactionChannelStatusChangedEvent"/>, which the existing
/// handler relays to the sign-in window of the transaction.
/// </summary>
/// <remarks>
/// The status describes a fact that has already happened, so it is published with
/// <see cref="CancellationToken.None"/>: an aborted request or a stopping host must not lose it. A failure of
/// the publication is logged as a warning and never changes the flow that published it — the status is a
/// hint the page does not rely on.
/// </remarks>
internal static class ClaimFormChannelStatus
{
    /// <summary>
    /// Publishes that the form of the web step is open on the device that confirmed the sign-in.
    /// </summary>
    /// <param name="publisher">Publisher of transaction events.</param>
    /// <param name="timeProvider">Clock the moment of the event is taken from.</param>
    /// <param name="transaction">The transaction the status is about.</param>
    /// <param name="channelType">Channel type of the transaction's identity snapshot.</param>
    /// <param name="logger">Logger of the caller.</param>
    /// <returns>The completed operation.</returns>
    public static async Task PublishFormOnPhoneAsync(
        ITransactionEventPublisher publisher,
        TimeProvider timeProvider,
        Transaction transaction,
        string channelType,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelType);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await publisher.PublishAsync(
                new TransactionChannelStatusChangedEvent
                {
                    TransactionId = transaction.Id,
                    OccurredAt = timeProvider.GetUtcNow(),
                    ChannelType = channelType,
                    ChannelStatus = ClaimCompletionChannelStatusKeys.FormOnPhone,
                    Context = TransactionEventContext.FromTransaction(transaction)
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to publish the claims completion channel status {ChannelStatus}. TransactionId: {TransactionId}",
                ClaimCompletionChannelStatusKeys.FormOnPhone,
                transaction.Id.ToString());
        }
    }
}
