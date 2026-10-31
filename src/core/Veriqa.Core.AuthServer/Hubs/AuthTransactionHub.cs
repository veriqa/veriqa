// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.SignalR;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.AuthServer.Hubs;

/// <summary>
/// SignalR hub for real-time notifications about authentication transaction status (SPEC-007 §5.2).
/// The client connects and subscribes to a group keyed by the external session_id.
/// Access model (SPEC-007 UI-095): the session_id is a bearer capability. It travels in the QR code,
/// the deep link and the page URL, so whoever holds the sign-in entry may subscribe — the hub does not
/// authenticate the connection and rejects only an unparsable identifier or a transaction that does not
/// exist. The boundary is held by what the hub sends, not by who listens: its messages carry the
/// transaction's process state only, never the channel user's identity.
/// </summary>
public sealed class AuthTransactionHub : Hub
{
    /// <summary>
    /// Transaction store used to validate the subscription.
    /// </summary>
    private readonly ITransactionStore _transactionStore;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<AuthTransactionHub> _logger;

    /// <summary>
    /// Clock the transaction deadline is read by.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Services of the invocation scope the internal services of the hop mode are taken from.
    /// </summary>
    private readonly IServiceProvider _services;

    /// <summary>
    /// Initializes the authentication hub.
    /// </summary>
    /// <param name="transactionStore">Transaction store.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">
    /// Clock the transaction deadline is read by — the same one the engine measures the TTL with, so
    /// that the replayed status and the transaction's own expiry never disagree. Supplied by the
    /// container, which every wiring of this hub has: the store it takes above comes from the
    /// Transaction Engine, and the engine registers the clock alongside it.
    /// </param>
    /// <param name="services">
    /// Services of the scope of the invocation. The re-issue of hop QR codes is an internal service of the
    /// library and cannot be a parameter of this public constructor, so it is taken from here — only by the
    /// method that needs it.
    /// </param>
    public AuthTransactionHub(
        ITransactionStore transactionStore,
        ILogger<AuthTransactionHub> logger,
        TimeProvider timeProvider,
        IServiceProvider services)
    {
        _transactionStore = transactionStore;
        _logger = logger;
        _timeProvider = timeProvider;
        _services = services;
    }

    /// <summary>
    /// Subscribes the client to updates for a specific transaction (SPEC-007 UI-028).
    /// Validates the session_id format, verifies that the transaction exists in the store, and
    /// replays the transaction's current status to the caller so that a subscriber is never left
    /// waiting for a broadcast that has already gone out (SPEC-007 UI-087). The answer of the bot of a
    /// channel, while it is still awaited in the chat, is replayed the same way.
    /// </summary>
    /// <param name="sessionId">External session identifier (session_id).</param>
    /// <returns>Group subscription task.</returns>
    public async Task JoinTransactionAsync(string sessionId)
    {
        // The method validates the session_id, adds the connection to the group and replays the
        // transaction's current status to it

        // Validate the session_id → TransactionId format
        var parsedId = SessionIdMapper.ToTransactionId(sessionId);
        if (parsedId is null)
        {
            _logger.LogWarning(
                "Client {ConnectionId} attempted to subscribe to an invalid sessionId: {SessionId}",
                Context.ConnectionId,
                sessionId);
            throw new HubException("Invalid session identifier");
        }

        // The only admission check is existence: the session_id itself is the capability (SPEC-007 UI-095)
        var transaction = await _transactionStore.GetByIdAsync(parsedId.Value, Context.ConnectionAborted);
        if (transaction is null)
        {
            _logger.LogWarning(
                "Client {ConnectionId} attempted to subscribe to a non-existent session {SessionId}",
                Context.ConnectionId,
                sessionId);
            throw new HubException("Session not found");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId, Context.ConnectionAborted);

        // A broadcast is addressed to the group AS IT STOOD at the moment it was sent, and it is
        // never repeated. So a status that changed before this connection was in the group reached
        // nobody — the page opened after the user had already answered, or reconnected and
        // resubscribed after the socket dropped — and waiting for it would last until the TTL ran
        // out, which is the one ending SPEC-007 UI-087 forbids. The current status is therefore
        // replayed to the caller alone, over the same method and the same model a broadcast uses:
        // the client needs no second path, and a status it already handled is idempotent on it.
        //
        // Read again, rather than reusing the snapshot taken for the admission check above: only a
        // snapshot taken AFTER the connection joined the group leaves no gap. A change between the
        // two reads is carried by the broadcast the group now receives; a change before the second
        // read is in the snapshot itself. The two can therefore overlap — and an overlap costs one
        // repeated message, while the gap between them would cost the status altogether. The second
        // read is a read per subscription, not per status change. A transaction no longer in the
        // store by that second read — swept between the two — leaves the admission snapshot as the
        // last thing known about it, and the deadline on that snapshot is what the caller hears.
        var current = await _transactionStore.GetByIdAsync(parsedId.Value, Context.ConnectionAborted)
            ?? transaction;

        var currentStatus = TransactionStatusMessageFactory.FromTransaction(
            current,
            _timeProvider.GetUtcNow());
        if (currentStatus is not null)
        {
            await Clients.Caller.SendAsync(
                SignalRConstants.OnStatusChangedMethod,
                currentStatus,
                Context.ConnectionAborted);
        }

        // The answer of the bot is a fact on the transaction as well — the sender entered it and the
        // answer is awaited in the channel — and its broadcast is as unrepeated as that of a status: a
        // page that left for the chat on the same device and came back subscribes after it. So it is
        // replayed to the caller alone from the same second read, by the predicate the broadcast is
        // published under, over the method and the model of a channel status. The message names the
        // channel the sender entered by and nothing of the sender (SPEC-007 UI-095).
        if (current.IsAwaitingAnswerInChannel() && current.SubjectBinding is { } subjectBinding)
        {
            await Clients.Caller.SendAsync(
                SignalRConstants.OnChannelStatusChangedMethod,
                new ChannelStatusMessage
                {
                    SessionId = sessionId,
                    ChannelType = subjectBinding.ChannelType,
                    ChannelStatus = ChannelAnswerStatuses.ChannelAnswered
                },
                Context.ConnectionAborted);
        }

        _logger.LogInformation(
            "Client {ConnectionId} subscribed to session {SessionId}, replayed status: {Status}",
            Context.ConnectionId,
            sessionId,
            currentStatus?.Status);
    }

    /// <summary>
    /// Replaces the hop QR codes of a page before their tokens expire (SPEC-025 HOP-152): a new token and
    /// its picture per entry the page names. The answer goes to the caller alone; the method only reads —
    /// it writes neither the transaction nor the audit, publishes no status, carries no code of the hop
    /// family and has no bearing on the completion of the transaction. The session_id is the same bearer
    /// capability as for the subscription (SPEC-007 UI-095).
    /// </summary>
    /// <remarks>
    /// An entry left out of the answer is one the page stops re-issuing: the transaction is gone or past
    /// entering a channel, the entry names no channel of its set or no hop for it, or this connection asks
    /// again before the token it was given for the entry is due for replacement. An entry the replacement of
    /// which failed is answered as failed, and the page asks again later. No failure leaves the method as an
    /// error: the call has no outcome the page shows.
    /// </remarks>
    /// <param name="sessionId">External session identifier (session_id).</param>
    /// <param name="entries">
    /// The entries of the page: the channel types of its per-channel hop QR codes, in any letter case, or an
    /// empty string for its unified hop QR code.
    /// </param>
    /// <returns>An item per replaced or failed QR code; empty — nothing to replace.</returns>
    public Task<IReadOnlyList<HopQrReissueItem>> ReissueHopQrAsync(string sessionId, IReadOnlyList<string> entries) =>
        _services.GetRequiredService<HopQrReissue>().ReissueAsync(
            sessionId,
            entries,
            Context.Items,
            Context.ConnectionAborted);

    /// <summary>
    /// Unsubscribes the client from transaction updates (SPEC-007 UI-029).
    /// </summary>
    /// <param name="sessionId">External session identifier (session_id).</param>
    /// <returns>Group unsubscription task.</returns>
    public async Task LeaveTransactionAsync(string sessionId)
    {
        // The method removes the connection from the group keyed by session_id
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId, Context.ConnectionAborted);
        _logger.LogInformation(
            "Client {ConnectionId} unsubscribed from session {SessionId}",
            Context.ConnectionId,
            sessionId);
    }
}
