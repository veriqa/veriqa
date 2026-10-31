// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.WebProviders.State;

/// <summary>
/// The state of the web provider channels that the store of transactions does not hold: the sessions
/// of the pages, the question put to an account, the feed of the messages of an account and the
/// outcome of a transaction. The page of a channel stands in for the chat of a messenger, and this is
/// what that chat would hold.
/// <para>
/// Every record is addressed by its channel. The question and the outcome belong to a transaction;
/// the feed belongs to an account, as the chat of one account does: a message of the core carries no
/// transaction, and every live session of the page of that account shows it. Every record ends at the
/// moment the caller states, which is never later than the end of its transaction plus the retention of
/// a completed one (<see cref="WebProviderStateLifetime"/>): the store has no lifetime of its own.
/// </para>
/// <para>
/// The default store lives in the process; the Redis channel stores replace it, so the page of a
/// sign-in answered on one replica shows on another. A failure of the storage surfaces as an exception
/// of the call.
/// </para>
/// </summary>
internal interface IWebProviderStateStore
{
    /// <summary>
    /// Stores the session of a page under its mark and makes the account it belongs to reachable for
    /// the messages of the core until the session ends.
    /// </summary>
    /// <param name="mark">The random value the browser holds; the store keeps no more of it than it
    /// needs to find the session again.</param>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task completed once the session is stored.</returns>
    Task SaveSessionAsync(string mark, WebProviderPageSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the session of a page by the mark the browser holds.
    /// </summary>
    /// <param name="mark">The mark.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session; <c>null</c> — no live session holds this mark.</returns>
    Task<WebProviderPageSession?> FindSessionAsync(string mark, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the question put to an account of a transaction, in place of any earlier one, and not yet
    /// answered.
    /// </summary>
    /// <param name="channelType">Channel of the transaction.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="prompt">The question.</param>
    /// <param name="expiresAt">Moment the record ends.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task completed once the question is stored.</returns>
    Task SavePromptAsync(
        string channelType,
        TransactionId transactionId,
        WebProviderPrompt prompt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the question of a transaction.
    /// </summary>
    /// <param name="channelType">Channel of the transaction.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The question; <c>null</c> — none was put.</returns>
    Task<WebProviderPrompt?> FindPromptAsync(
        string channelType,
        TransactionId transactionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the question of a transaction answered; the record keeps its end.
    /// </summary>
    /// <param name="channelType">Channel of the transaction.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task completed once the mark is stored; a transaction without a question is left as
    /// it is.</returns>
    Task MarkPromptAnsweredAsync(
        string channelType,
        TransactionId transactionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a message of the core to the feed of an account — only while a session of the page of that
    /// account is live. The feed lives as long as the latest session of the account.
    /// </summary>
    /// <param name="channelType">Channel of the account.</param>
    /// <param name="channelUserId">The account.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the message is in the feed; <c>false</c> — the account has no live
    /// session, and the message is not kept.</returns>
    Task<bool> TryAppendMessageAsync(
        string channelType,
        string channelUserId,
        WebProviderFeedMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the feed of an account, oldest message first.
    /// </summary>
    /// <param name="channelType">Channel of the account.</param>
    /// <param name="channelUserId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The messages; empty — none is kept.</returns>
    Task<IReadOnlyList<WebProviderFeedMessage>> ListMessagesAsync(
        string channelType,
        string channelUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the status text of a transaction, in place of any earlier one: a later notice of the same
    /// transaction replaces the earlier.
    /// </summary>
    /// <param name="channelType">Channel of the transaction.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="statusText">The status text, as the core rendered it.</param>
    /// <param name="expiresAt">Moment the record ends.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task completed once the outcome is stored.</returns>
    Task SaveOutcomeAsync(
        string channelType,
        TransactionId transactionId,
        string statusText,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the status text of a transaction.
    /// </summary>
    /// <param name="channelType">Channel of the transaction.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status text; <c>null</c> — no outcome was reported.</returns>
    Task<string?> FindOutcomeAsync(
        string channelType,
        TransactionId transactionId,
        CancellationToken cancellationToken = default);
}
