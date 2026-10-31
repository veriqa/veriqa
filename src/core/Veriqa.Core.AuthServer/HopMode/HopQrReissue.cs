// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using Veriqa.Core.AuthServer.Hubs;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The replacement of the hop QR codes of a page before their tokens expire (SPEC-025 HOP-152) — the work
/// behind <see cref="AuthTransactionHub.ReissueHopQrAsync"/>. It issues new tokens through the one step that
/// issues them for a core page and draws their pictures; it reads the transaction and nothing else: no
/// write to the transaction, no audit, no status, no code of the hop family, and nothing about the
/// completion of the transaction (the reservation HOP-152 makes to HOP-104a and HOP-008).
/// </summary>
/// <remarks>
/// Neither a token nor a hop URL reaches the log, at any level. Scoped: the channel set is selected over the
/// channel adapters and the tokens are issued through <see cref="HopQrContent"/>, whose lifetime a host may
/// narrow to the request.
/// </remarks>
internal sealed class HopQrReissue
{
    /// <summary>
    /// Key of the connection state the frequency limit of issue is kept under — the tokens this connection
    /// was given, by entry.
    /// </summary>
    private static readonly object GrantsItemKey = new();

    /// <summary>
    /// Transaction store — read here, never written.
    /// </summary>
    private readonly ITransactionStore _transactionStore;

    /// <summary>
    /// The channel set of the transaction and the one answer to whether the unified QR code applies.
    /// </summary>
    private readonly HopUnifiedMode _unifiedMode;

    /// <summary>
    /// The one answer to the hop mode of a channel — the same one the QR code was first issued by, so a
    /// QR code issued with the hop switch off is replaced with it off as well.
    /// </summary>
    private readonly HopChannelModes _channelModes;

    /// <summary>
    /// The one step that issues hop URLs.
    /// </summary>
    private readonly HopQrContent _hopQrContent;

    /// <summary>
    /// QR code generation service.
    /// </summary>
    private readonly IQrCodeService _qrCodeService;

    /// <summary>
    /// Clock the state of the transaction and the lifetime of the tokens are judged by.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<HopQrReissue> _logger;

    /// <summary>
    /// Creates the re-issue.
    /// </summary>
    /// <param name="transactionStore">Transaction store.</param>
    /// <param name="unifiedMode">The channel set and the unified mode.</param>
    /// <param name="channelModes">The one answer to the hop mode of a channel.</param>
    /// <param name="hopQrContent">The one step that issues hop URLs.</param>
    /// <param name="qrCodeService">QR code generation service.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public HopQrReissue(
        ITransactionStore transactionStore,
        HopUnifiedMode unifiedMode,
        HopChannelModes channelModes,
        HopQrContent hopQrContent,
        IQrCodeService qrCodeService,
        TimeProvider timeProvider,
        ILogger<HopQrReissue> logger)
    {
        ArgumentNullException.ThrowIfNull(transactionStore);
        ArgumentNullException.ThrowIfNull(unifiedMode);
        ArgumentNullException.ThrowIfNull(channelModes);
        ArgumentNullException.ThrowIfNull(hopQrContent);
        ArgumentNullException.ThrowIfNull(qrCodeService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _transactionStore = transactionStore;
        _unifiedMode = unifiedMode;
        _channelModes = channelModes;
        _hopQrContent = hopQrContent;
        _qrCodeService = qrCodeService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Replaces the hop QR codes a page asks for (SPEC-025 HOP-152). An entry is answered only while the
    /// transaction is in the state of entering a channel, names a channel of its set (or the unified QR code
    /// that applies to the set), and is past the frequency limit of issue for this connection; any other
    /// entry is left out, and the page stops re-issuing it. A failure of one entry is answered as such and
    /// does not stop the others.
    /// </summary>
    /// <remarks>
    /// The frequency limit is kept per entry, from the lifetime of the token this method last gave this
    /// connection for it: a new token is not issued before that token is due for replacement
    /// (<see cref="HopReissueSchedule.DueAt"/>). A new connection — a reconnect included — starts without it.
    /// </remarks>
    /// <param name="sessionId">External session identifier of the transaction.</param>
    /// <param name="entries">
    /// The entries of the page: channel types of its per-channel QR codes, in any letter case, or an empty
    /// string for its unified QR code.
    /// </param>
    /// <param name="connectionItems">State of the calling connection (<c>HubCallerContext.Items</c>).</param>
    /// <param name="cancellationToken">Cancellation token of the connection.</param>
    /// <returns>An entry per replaced or failed QR code; empty — nothing to replace.</returns>
    public async Task<IReadOnlyList<HopQrReissueItem>> ReissueAsync(
        string? sessionId,
        IReadOnlyList<string?>? entries,
        IDictionary<object, object?> connectionItems,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionItems);

        if (entries is null || entries.Count == 0)
        {
            return [];
        }

        // An identifier that does not parse is no capability: nothing to answer, and nothing for the page
        // to show, so no error either.
        if (SessionIdMapper.ToTransactionId(sessionId) is not { } transactionId)
        {
            return [];
        }

        var calledAt = _timeProvider.GetUtcNow();

        Transaction? transaction;
        IReadOnlyList<string> channelSet;
        try
        {
            transaction = await _transactionStore.GetByIdAsync(transactionId, cancellationToken);

            // No transaction, or one past entering a channel (the rows of an expired transaction and of
            // the state of entering a channel of the answer matrix, SPEC-025 HOP-074): the page has
            // nothing left to replace.
            if (transaction is null
                || transaction.State is not TransactionState.Pending
                || transaction.ExpiresAt <= calledAt)
            {
                return [];
            }

            channelSet = await _unifiedMode.ResolveChannelSetAsync(transaction, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A failure to read is not an answer that the entries are over: the page keeps its QR codes and
            // asks again.
            _logger.LogWarning(
                ex,
                "The hop QR codes of transaction {TransactionId} could not be re-issued: the transaction or its "
                + "channel set could not be read",
                transactionId.ToString());

            return [.. entries
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(Failure)];
        }

        var grants = GrantsOf(connectionItems);
        var resolution = TransactionResolutionContext.For(transaction);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<HopQrReissueItem>();

        foreach (var entry in entries.OfType<string>())
        {
            if (KeyOf(entry, channelSet) is not { } key || !answered.Add(key))
            {
                continue;
            }

            if (grants.TryGetValue(key, out var grant)
                && calledAt < HopReissueSchedule.DueAt(grant.IssuedAt, grant.ExpiresAt))
            {
                continue;
            }

            var item = await ReissueEntryAsync(
                new ReissueEntry(entry, key, transaction, channelSet, resolution),
                grants,
                calledAt,
                cancellationToken);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items.AsReadOnly();
    }

    /// <summary>
    /// Issues the new token of one entry and draws its picture.
    /// </summary>
    /// <param name="entry">The entry and what it is issued for.</param>
    /// <param name="grants">Tokens this connection was given, by entry — updated on success.</param>
    /// <param name="calledAt">
    /// Moment of the call, read before the token is issued — the moment of issue the frequency limit counts
    /// from. It is never later than the moment of issue the token carries, so the limit never falls after
    /// the moment the page asks again, which the page counts from the remaining lifetime the answer states.
    /// </param>
    /// <param name="cancellationToken">Cancellation token of the connection.</param>
    /// <returns>The answer of the entry; null — the entry is left out of the answer.</returns>
    private async ValueTask<HopQrReissueItem?> ReissueEntryAsync(
        ReissueEntry entry,
        ConcurrentDictionary<string, HopReissueGrant> grants,
        DateTimeOffset calledAt,
        CancellationToken cancellationToken)
    {
        try
        {
            // The unified entry issues through the same shared step as a channel, over the mode already
            // resolved here: a failure of the issuance reaches the one warning below rather than the wording
            // of the page render, where the per-channel QR codes take the place of the unified one.
            HopChannelMode mode;
            if (entry.Key.Length == 0)
            {
                var unifiedMode = await _unifiedMode.ResolveAsync(
                    entry.Resolution, entry.ChannelSet, cancellationToken);
                if (unifiedMode is null || unifiedMode.BaseUrl is null)
                {
                    return null;
                }

                mode = unifiedMode;
            }
            else
            {
                mode = await _channelModes.ResolveAsync(entry.Resolution, entry.Key, cancellationToken);
                if (!mode.IsEnabled || mode.BaseUrl is null)
                {
                    return null;
                }
            }

            var issued = await _hopQrContent.IssueHopUrlAsync(
                new HopIssueRequest(
                    entry.Transaction.Id,
                    entry.Transaction.ExpiresAt,
                    entry.Key,
                    entry.Resolution,
                    ChannelQrSurface.CorePage),
                mode,
                cancellationToken);

            // The unified code names no channel: the flat pixel scale of the level answers for it, as on the
            // page that drew it first.
            var qrCodeBase64 = await _qrCodeService.GenerateBase64PngAsync(
                issued.Url, entry.Key, entry.Resolution, cancellationToken);

            var remaining = issued.ExpiresAt - _timeProvider.GetUtcNow();
            grants[entry.Key] = new HopReissueGrant(calledAt, issued.ExpiresAt);

            return new HopQrReissueItem(
                entry.Requested,
                issued.Url,
                qrCodeBase64,
                Math.Max(0L, (long)remaining.TotalMilliseconds),
                HopReissueSchedule.ExpiresBeforeTransaction(issued.ExpiresAt, entry.Transaction.ExpiresAt),
                Failed: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "The hop QR code of channel {ChannelType} of transaction {TransactionId} could not be re-issued",
                entry.Key,
                entry.Transaction.Id.ToString());
            return Failure(entry.Requested);
        }
    }

    /// <summary>
    /// The key an entry is answered under: the empty string of the unified QR code, or the channel type of
    /// the set the entry names regardless of letter case — the value the token and the picture are issued
    /// with, since the resolve endpoint compares channel types exactly.
    /// </summary>
    /// <param name="entry">The entry as the page sent it.</param>
    /// <param name="channelSet">The channel set of the transaction.</param>
    /// <returns>The key; null — the entry names no channel of the set.</returns>
    private static string? KeyOf(string entry, IReadOnlyList<string> channelSet) =>
        entry.Length == 0
            ? string.Empty
            : channelSet.FirstOrDefault(
                channelType => string.Equals(channelType, entry, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The tokens this connection was given, by entry, kept in the state of the connection.
    /// </summary>
    /// <param name="connectionItems">State of the connection.</param>
    /// <returns>The tokens; a new, empty map for a connection that was given none.</returns>
    private static ConcurrentDictionary<string, HopReissueGrant> GrantsOf(
        IDictionary<object, object?> connectionItems)
    {
        if (connectionItems.TryGetValue(GrantsItemKey, out var stored)
            && stored is ConcurrentDictionary<string, HopReissueGrant> existing)
        {
            return existing;
        }

        var grants = new ConcurrentDictionary<string, HopReissueGrant>(StringComparer.Ordinal);
        connectionItems[GrantsItemKey] = grants;
        return grants;
    }

    /// <summary>
    /// The answer of an entry whose replacement failed this time.
    /// </summary>
    /// <param name="entry">The entry as the page sent it.</param>
    /// <returns>The failed answer.</returns>
    private static HopQrReissueItem Failure(string entry) =>
        new(
            entry,
            Url: string.Empty,
            QrCodeBase64: string.Empty,
            RemainingMs: 0,
            ExpiresBeforeTransaction: false,
            Failed: true);

    /// <summary>
    /// One entry being re-issued and what its token is issued for.
    /// </summary>
    /// <param name="Requested">The entry as the page sent it.</param>
    /// <param name="Key">The channel type of the set it names, or the empty string of the unified QR code.</param>
    /// <param name="Transaction">The transaction of the page.</param>
    /// <param name="ChannelSet">The channel set of the transaction.</param>
    /// <param name="Resolution">Resolution context of the transaction.</param>
    private sealed record ReissueEntry(
        string Requested,
        string Key,
        Transaction Transaction,
        IReadOnlyList<string> ChannelSet,
        ResolutionContext Resolution);

    /// <summary>
    /// A token this connection was given for an entry — what the frequency limit of issue counts from.
    /// </summary>
    /// <param name="IssuedAt">Moment of issue, as the frequency limit counts it.</param>
    /// <param name="ExpiresAt">Own expiry of the token.</param>
    private readonly record struct HopReissueGrant(DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
}
