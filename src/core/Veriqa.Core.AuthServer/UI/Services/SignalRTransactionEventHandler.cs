// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.SignalR;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Hubs;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.TransactionEngine.Events;

namespace Veriqa.Core.AuthServer.UI.Services;

/// <summary>
/// Transaction event handler for broadcasting notifications via SignalR (SPEC-007 §5).
/// Receives transaction lifecycle events from TransactionEventPublisher
/// and broadcasts notifications to subscribed clients via AuthTransactionHub.
/// </summary>
internal sealed class SignalRTransactionEventHandler : ITransactionEventHandler
{
    /// <summary>
    /// SignalR hub context for sending messages.
    /// </summary>
    private readonly IHubContext<AuthTransactionHub> _hubContext;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<SignalRTransactionEventHandler> _logger;

    /// <summary>
    /// Creates the SignalR event handler.
    /// </summary>
    /// <param name="hubContext">SignalR hub context.</param>
    /// <param name="logger">Logger.</param>
    public SignalRTransactionEventHandler(
        IHubContext<AuthTransactionHub> hubContext,
        ILogger<SignalRTransactionEventHandler> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task HandleAsync(TransactionEvent transactionEvent, CancellationToken cancellationToken = default)
    {
        // The method maps a transaction event to a SignalR message and broadcasts it to subscribers

        // Channel-specific intermediate statuses (SPEC-016 §10.1) are broadcast
        // via a separate additive method and are not mixed with lifecycle messages
        if (transactionEvent is TransactionChannelStatusChangedEvent channelStatusEvent)
        {
            await SendChannelStatusAsync(channelStatusEvent);
            return;
        }

        var message = MapToStatusMessage(transactionEvent);
        if (message is null)
        {
            return;
        }

        var sessionId = SessionIdMapper.ToSessionId(transactionEvent.TransactionId);

        // Use CancellationToken.None so the UI notification is guaranteed to be delivered,
        // even if the HTTP request that triggered the state change was cancelled (SPEC-007 §5)
        await _hubContext.Clients
            .Group(sessionId)
            .SendAsync(
                SignalRConstants.OnStatusChangedMethod,
                message,
                CancellationToken.None);

        _logger.LogDebug(
            "SignalR notification sent for session {SessionId}, status: {Status}",
            sessionId,
            message.Status);
    }

    /// <summary>
    /// Broadcasts a channel-specific intermediate status to the session's subscribers
    /// via the separate <see cref="SignalRConstants.OnChannelStatusChangedMethod"/> method.
    /// </summary>
    /// <param name="channelStatusEvent">Channel-specific status event.</param>
    /// <returns>Task representing the broadcast completion.</returns>
    private async Task SendChannelStatusAsync(TransactionChannelStatusChangedEvent channelStatusEvent)
    {
        // The method builds an additive message about the channel's intermediate step
        var sessionId = SessionIdMapper.ToSessionId(channelStatusEvent.TransactionId);

        var channelMessage = new ChannelStatusMessage
        {
            SessionId = sessionId,
            ChannelType = channelStatusEvent.ChannelType,
            ChannelStatus = channelStatusEvent.ChannelStatus
        };

        // CancellationToken.None — the notification must be delivered even if the request was cancelled (SPEC-007 §5)
        await _hubContext.Clients
            .Group(sessionId)
            .SendAsync(
                SignalRConstants.OnChannelStatusChangedMethod,
                channelMessage,
                CancellationToken.None);

        _logger.LogDebug(
            "SignalR channel status sent for session {SessionId}: {ChannelType}/{ChannelStatus}",
            sessionId,
            channelStatusEvent.ChannelType,
            channelStatusEvent.ChannelStatus);
    }

    /// <summary>
    /// Maps a transaction event to a SignalR message.
    /// </summary>
    /// <param name="transactionEvent">Transaction event.</param>
    /// <returns>Message, or null if the event requires no notification.</returns>
    private static TransactionStatusMessage? MapToStatusMessage(TransactionEvent transactionEvent)
    {
        // The method builds the message depending on the event type

        var sessionId = SessionIdMapper.ToSessionId(transactionEvent.TransactionId);

        return transactionEvent switch
        {
            TransactionConfirmedEvent => new TransactionStatusMessage
            {
                SessionId = sessionId,
                Status = TransactionStatusNames.Confirmed
            },

            // The core has taken the question onto its own page: the browser is sent to the callback
            // exactly the same way as on completion — the same deterministic session URL — because
            // the transaction will not reach a terminal status until the user answers there.
            TransactionChannelIdentityAttachedEvent attached => new TransactionStatusMessage
            {
                SessionId = sessionId,
                Status = TransactionStatusNames.AwaitingWebConfirmation,
                RedirectUrl = TransactionStatusMessageFactory.BuildCallbackUrl(sessionId),
                AnswerBrowserBound = attached.AnswerBrowserBound
            },

            // A step of the claims completion began (SPEC-041 L91): the page learns which one, and
            // stays where it is. Step and surface come off the event, which read them off the stored
            // transaction.
            TransactionClaimCompletionStepEvent step => TransactionStatusMessageFactory.ForClaimCompletionStep(
                sessionId, step.Step, step.Surface),

            TransactionCompletedEvent => new TransactionStatusMessage
            {
                SessionId = sessionId,
                Status = TransactionStatusNames.Completed,
                RedirectUrl = TransactionStatusMessageFactory.BuildCallbackUrl(sessionId)
            },

            TransactionExpiredEvent => new TransactionStatusMessage
            {
                SessionId = sessionId,
                Status = TransactionStatusNames.Expired
            },

            TransactionFailedEvent failed => new TransactionStatusMessage
            {
                SessionId = sessionId,
                Status = TransactionStatusNames.Failed,
                ErrorCode = failed.ReasonCode
            },

            // For Created and Activated events — no notification is required
            _ => null
        };
    }
}
