// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Veriqa.Core.ChannelAdapter.WebProviders.State;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Pages;

/// <summary>
/// What the page of one session shows: the main block and, above it, the current message of the feed
/// (<see cref="CurrentFeed"/>), unless the end of the request closed it. The state is derived only from what the adapter can see — its own
/// store and the state of the transaction in the core; the confirmation mode of the transaction is not
/// among them, and nothing here depends on the type of the transaction.
/// </summary>
/// <param name="Block">Kind of the main block.</param>
/// <param name="Text">Text of the main block: the outcome or the question; <c>null</c> — a block that
/// states a text of the page itself.</param>
/// <param name="Feed">The current message of the feed, unless the end of the request closed it: none or
/// one.</param>
internal sealed record WebProviderPageView(
    WebProviderPageBlock Block,
    string? Text,
    IReadOnlyList<WebProviderFeedMessage> Feed)
{
    /// <summary>
    /// Separator of the parts of the fingerprint.
    /// </summary>
    private const char FingerprintSeparator = '\n';

    /// <summary>
    /// Number of bytes of the hash the fingerprint keeps.
    /// </summary>
    private const int FingerprintLengthBytes = 16;

    /// <summary>
    /// Derives the view. The conditions are checked top down, and the first that holds sets the main
    /// block:
    /// <list type="number">
    /// <item>an outcome is stored and the current message offers actions (the menu after a sign-in, the
    /// link to the claim form) — no block: the actions are what the user reads;</item>
    /// <item>an outcome is stored — the status text of the transaction;</item>
    /// <item>the transaction is terminal, and the feed holds a message sent once it ended — no block:
    /// the message, the receipt the core sends instead of an outcome when no question was asked, is
    /// what the user reads;</item>
    /// <item>the transaction is final in the core (gone, terminal or out of time) — the neutral line of
    /// an ended request;</item>
    /// <item>a question to the account of the session is stored and not answered — the question;</item>
    /// <item>otherwise — waiting.</item>
    /// </list>
    /// </summary>
    /// <param name="session">The session of the page.</param>
    /// <param name="transaction">The transaction as the store holds it; <c>null</c> — it is gone.</param>
    /// <param name="prompt">The question of the transaction; <c>null</c> — none was put.</param>
    /// <param name="outcome">The status text of the transaction; <c>null</c> — none was reported.</param>
    /// <param name="feed">The feed of the account of the session, oldest first; the view keeps only its
    /// current message (<see cref="CurrentFeed"/>), unless the end of the request closed it.</param>
    /// <param name="now">The server's current moment.</param>
    /// <returns>The view.</returns>
    public static WebProviderPageView Of(
        WebProviderPageSession session,
        Transaction? transaction,
        WebProviderPrompt? prompt,
        string? outcome,
        IReadOnlyList<WebProviderFeedMessage> feed,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(feed);

        feed = CurrentFeed(feed, transaction, now);
        if (IsClosedByTheEnd(feed, transaction, outcome, now))
        {
            feed = [];
        }

        // The menu sent after a sign-in already tells the user it went through, so the page adds no
        // outcome line under it.
        if (outcome is not null && OffersActions(feed))
        {
            return new WebProviderPageView(WebProviderPageBlock.Closed, Text: null, feed);
        }

        if (outcome is not null)
        {
            return new WebProviderPageView(WebProviderPageBlock.Outcome, outcome, feed);
        }

        if (transaction is not null
            && transaction.IsTerminal()
            && feed.Any(message => message.SentAt >= transaction.UpdatedAt))
        {
            return new WebProviderPageView(WebProviderPageBlock.Closed, Text: null, feed);
        }

        if (transaction is null || transaction.IsTerminal() || transaction.HasRunOutOfTime(now))
        {
            return new WebProviderPageView(WebProviderPageBlock.Ended, Text: null, feed);
        }

        // A question put to another account signed in for the same transaction is not this one's.
        if (prompt is { Answered: false } && string.Equals(prompt.ChannelUserId, session.ChannelUserId, StringComparison.Ordinal))
        {
            return new WebProviderPageView(WebProviderPageBlock.Question, prompt.Text, feed);
        }

        return new WebProviderPageView(WebProviderPageBlock.Waiting, Text: null, feed);
    }

    /// <summary>
    /// The current message of the feed — the only one the page shows and takes a press from. The feed
    /// belongs to the account and holds the messages of its earlier sign-ins too; the page of one
    /// transaction shows the latest message sent since that transaction began, so a past sign-in leaves
    /// neither its texts nor its buttons, and a step the message closed — the form, once filled — is not
    /// shown under the next one. A link to the claim form lives only while its transaction goes on: once
    /// the transaction is over or out of time, the link opens nothing, and no message is current.
    /// </summary>
    /// <param name="feed">The feed of the account, oldest first.</param>
    /// <param name="transaction">The transaction of the page; <c>null</c> — it is gone, and so is its
    /// current message.</param>
    /// <param name="now">The server's current moment.</param>
    /// <returns>None or one message.</returns>
    public static IReadOnlyList<WebProviderFeedMessage> CurrentFeed(
        IReadOnlyList<WebProviderFeedMessage> feed,
        Transaction? transaction,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(feed);

        if (transaction is null || feed.Count is 0 || feed[^1].SentAt < transaction.CreatedAt)
        {
            return [];
        }

        var latest = feed[^1];
        if (latest.ClaimFormLink is not null && (transaction.IsTerminal() || transaction.HasRunOutOfTime(now)))
        {
            return [];
        }

        return [latest];
    }

    /// <summary>
    /// Tells whether the current message is a step of the running request the end of the request has
    /// closed, as filling the form closes its link: a plain text sent before the end — the notice that
    /// asks to confirm on the sign-in page — once an outcome is stored or the transaction is over. A
    /// message sent after the transaction ended — a receipt, a welcome — is what the user reads then and
    /// stays; so do the actions of a message and the link to the claim form, which have rules of their own.
    /// </summary>
    /// <param name="feed">The current message: none or one.</param>
    /// <param name="transaction">The transaction of the page; <c>null</c> — it is gone.</param>
    /// <param name="outcome">The status text of the transaction; <c>null</c> — none was reported.</param>
    /// <param name="now">The server's current moment.</param>
    /// <returns><c>true</c> — the message is no longer current.</returns>
    private static bool IsClosedByTheEnd(
        IReadOnlyList<WebProviderFeedMessage> feed,
        Transaction? transaction,
        string? outcome,
        DateTimeOffset now) =>
        feed is [{ ClaimFormLink: null, ActionButtons: null } message]
        && transaction is not null
        && (outcome is not null || transaction.IsTerminal() || transaction.HasRunOutOfTime(now))
        && !(transaction.IsTerminal() && message.SentAt >= transaction.UpdatedAt);

    /// <summary>
    /// Whether the page shows the request over: an outcome, a message the core sent at the end (a receipt,
    /// the menu or the button after a sign-in) or the line of an ended request. The question and the
    /// waiting state do not.
    /// </summary>
    public bool IsOver =>
        Block is WebProviderPageBlock.Outcome or WebProviderPageBlock.Closed or WebProviderPageBlock.Ended;

    /// <summary>
    /// Tells whether the current message offers an action the page can draw — an item of a menu, a link
    /// button or the link to the claim form.
    /// </summary>
    /// <param name="feed">The current message: none or one.</param>
    /// <returns><c>true</c> — it does.</returns>
    private static bool OffersActions(IReadOnlyList<WebProviderFeedMessage> feed) =>
        feed.Count > 0
        && ((feed[^1].ClaimFormLink is { } formLink && WebProviderFeedRules.IsDrawable(formLink))
            || (feed[^1].ActionButtons is { } actions && actions.Buttons.Any(WebProviderFeedRules.IsDrawable)));

    /// <summary>
    /// Fingerprint of the view — what the page script compares to tell that the page has something new
    /// to show: the main block with its text and the feed. It names nothing the page does not show to
    /// the same browser already.
    /// </summary>
    /// <returns>The fingerprint as lowercase hex.</returns>
    public string Fingerprint()
    {
        var latest = Feed.Count > 0 ? Feed[^1].SentAt.UtcTicks : 0L;
        var canonical = string.Join(
            FingerprintSeparator,
            Block.ToString(),
            Text ?? string.Empty,
            Feed.Count.ToString(CultureInfo.InvariantCulture),
            latest.ToString(CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexStringLower(hash.AsSpan(0, FingerprintLengthBytes));
    }
}
