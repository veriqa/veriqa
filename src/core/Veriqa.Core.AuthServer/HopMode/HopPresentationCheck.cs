// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Steps 2–3 of the resolve endpoint (SPEC-025 HOP-074): decodes the presented token and checks it and
/// the transaction it names, row by row of the answer matrix — the first row that does not pass is the
/// refusal. The check WRITES NOTHING — neither to the transaction, nor to the log, nor to the audit: it
/// returns the outcome, and the caller records it (<see cref="HopRefusalJournal"/>), so the same check
/// serves every method of the route.
/// <para>
/// A token that carries no channel type is the unified token (SPEC-025 HOP-142): it passes the same rows in
/// the same order, except that it names no channel to match against the transaction, and the rows of the
/// adapter and of the switch of the channel become the rows of the channel set and of the unified mode
/// (HOP-143) — judged by <see cref="HopUnifiedMode"/>, the one answer to both.
/// </para>
/// <para>
/// The expiries judged here are chosen by the caller (<see cref="HopExpiryBasis"/>). The own expiry of the
/// token (SPEC-025 HOP-149) is compared with the clock alone and never with another token issued for the
/// same transaction.
/// </para>
/// </summary>
internal sealed class HopPresentationCheck
{
    /// <summary>
    /// Codec of hop tokens.
    /// </summary>
    private readonly HopTokenCodec _codec;

    /// <summary>
    /// Transaction service — read here, never written.
    /// </summary>
    private readonly ITransactionService _transactionService;

    /// <summary>
    /// Adapters of the channels registered in this deployment.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _channelAdapters;

    /// <summary>
    /// Core-level declarations of the channels of the host — the owner of each registration.
    /// </summary>
    private readonly IEnumerable<CoreChannelDeclaration> _channelDeclarations;

    /// <summary>
    /// The one answer to the hop mode of a channel.
    /// </summary>
    private readonly HopChannelModes _channelModes;

    /// <summary>
    /// The channel set and the unified mode a unified token is judged by.
    /// </summary>
    private readonly HopUnifiedMode _unifiedMode;

    /// <summary>
    /// Clock the expiries are judged by.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the check.
    /// </summary>
    /// <param name="codec">Codec of hop tokens.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="channelAdapters">Adapters of the registered channels.</param>
    /// <param name="channelDeclarations">Core-level declarations of the channels of the host.</param>
    /// <param name="channelModes">The one answer to the hop mode of a channel.</param>
    /// <param name="unifiedMode">The channel set and the unified mode a unified token is judged by.</param>
    /// <param name="timeProvider">Clock.</param>
    public HopPresentationCheck(
        HopTokenCodec codec,
        ITransactionService transactionService,
        IEnumerable<IChannelAdapter> channelAdapters,
        IEnumerable<CoreChannelDeclaration> channelDeclarations,
        HopChannelModes channelModes,
        HopUnifiedMode unifiedMode,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(transactionService);
        ArgumentNullException.ThrowIfNull(channelAdapters);
        ArgumentNullException.ThrowIfNull(channelDeclarations);
        ArgumentNullException.ThrowIfNull(channelModes);
        ArgumentNullException.ThrowIfNull(unifiedMode);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _codec = codec;
        _transactionService = transactionService;
        _channelAdapters = channelAdapters;
        _channelDeclarations = channelDeclarations;
        _channelModes = channelModes;
        _unifiedMode = unifiedMode;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Checks a presented token and the transaction it names, in the order of the rows of the answer
    /// matrix (SPEC-025 HOP-074).
    /// </summary>
    /// <param name="rawToken">The token string as presented in the route.</param>
    /// <param name="expiryBasis">
    /// Which expiries are judged: <see cref="HopExpiryBasis.TransactionOnly"/> skips the row of the own
    /// expiry of the token and nothing else — the other rows and their order stay as they are.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The outcome: the first refusal met, or what the transition into the channel needs — for the unified
    /// token, the channel set of the transaction.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The hop settings of the channel, or those the unified token is judged by, cannot be resolved; the
    /// failure is not replaced by a default.
    /// </exception>
    public async ValueTask<HopPresentationOutcome> CheckAsync(
        string? rawToken,
        HopExpiryBasis expiryBasis,
        CancellationToken cancellationToken)
    {
        if (!_codec.TryDecode(rawToken, out var token))
        {
            return Refused(HopRefusal.TokenUndecodable, token: null, transaction: null);
        }

        var loaded = await _transactionService.GetTransactionAsync(token.TransactionId, cancellationToken);
        if (loaded.IsFailure)
        {
            // A transaction whose time ran out before the cleanup removed it is reported by the loading
            // itself; that is the row of an expired transaction, not of a missing one, and the rows
            // between the two are then not checked.
            var refusal = string.Equals(loaded.Error.Code, TransactionErrorCodes.TransactionExpired, StringComparison.Ordinal)
                ? HopRefusal.TransactionExpired
                : HopRefusal.TransactionNotFound;

            return Refused(refusal, token, transaction: null) with { LoadErrorCode = loaded.Error.Code };
        }

        var transaction = loaded.Value;

        // The empty channel type is the mark of the unified token (SPEC-025 HOP-142): a token issued for
        // one channel keeps the per-channel rows unchanged, whatever the QR mode is now.
        var isUnified = token.ChannelType.Length == 0;

        // The one field of the transaction that names channels before any channel is entered: an empty
        // set admits every channel. The unified token names none — its check of the channel is the check
        // of the unified mode below.
        if (!isUnified
            && transaction.AllowedChannelTypes.Count > 0
            && !transaction.AllowedChannelTypes.Contains(token.ChannelType))
        {
            return Refused(HopRefusal.ChannelTypeMismatch, token, transaction);
        }

        // SPEC-042 R60: a channel whose registration belongs to another tenant does not exist for the
        // tenant of the transaction, so a token issued for it is refused the way a channel the transaction
        // does not admit is. The unified token is judged over the channel set, which already leaves such a
        // channel out.
        if (!isUnified
            && ChannelRegistrationOwnership.IsOwnedByAnotherTenant(
                _channelDeclarations, token.ChannelType, transaction.GetTenantId()))
        {
            return Refused(HopRefusal.ChannelTypeMismatch, token, transaction);
        }

        var now = _timeProvider.GetUtcNow();

        if (expiryBasis is HopExpiryBasis.TokenAndTransaction && token.ExpiresAt <= now)
        {
            return Refused(HopRefusal.TokenExpired, token, transaction);
        }

        if (transaction.ExpiresAt <= now || transaction.State is TransactionState.Expired)
        {
            return Refused(HopRefusal.TransactionExpired, token, transaction);
        }

        if (transaction.State is not TransactionState.Pending)
        {
            return Refused(HopRefusal.NotInTransitionState, token, transaction);
        }

        if (isUnified)
        {
            return await CheckUnifiedAsync(token, transaction, cancellationToken);
        }

        var adapter = _channelAdapters.FirstOrDefault(
            candidate => string.Equals(candidate.ChannelType, token.ChannelType, StringComparison.Ordinal));
        if (adapter is null)
        {
            return Refused(HopRefusal.AdapterMissing, token, transaction);
        }

        // Resolved over the ownership context of the transaction, per act: hop turned off for the channel
        // at any level that owns the transaction closes the way in, even for a token already issued. A
        // channel whose link carries no transaction is not closed by the switch — its QR code was issued
        // with the switch unread, and the same answer admits it here.
        var channelMode = await _channelModes.ResolveAsync(
            TransactionResolutionContext.For(transaction),
            adapter,
            cancellationToken);

        return channelMode.IsEnabled
            ? new HopPresentationOutcome(Refusal: null, token, transaction, adapter, channelMode)
            : new HopPresentationOutcome(HopRefusal.HopDisabled, token, transaction, adapter, channelMode);
    }

    /// <summary>
    /// The rows of the adapter and of the switch, as the unified token passes them (SPEC-025 HOP-143): the
    /// channel set of the transaction is not empty, then the unified mode applies to it. The set goes
    /// first, so an empty set is logged as such and not as a mode that does not apply.
    /// </summary>
    /// <param name="token">The decoded unified token.</param>
    /// <param name="transaction">The transaction the token names, in the state of entering a channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome: a refusal, or the admitted transition with its channel set.</returns>
    private async ValueTask<HopPresentationOutcome> CheckUnifiedAsync(
        HopToken token,
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        var channelSet = await _unifiedMode.ResolveChannelSetAsync(transaction, cancellationToken);
        if (channelSet.Count is 0)
        {
            return Refused(HopRefusal.UnifiedSetEmpty, token, transaction);
        }

        // Resolved over the ownership context of the transaction, per act, like the switch of one channel:
        // a mode that stopped applying closes the way in, even for a token already issued.
        var unifiedMode = await _unifiedMode.ResolveAsync(
            TransactionResolutionContext.For(transaction),
            channelSet,
            cancellationToken);

        return unifiedMode is null
            ? Refused(HopRefusal.UnifiedModeInactive, token, transaction)
            : new HopPresentationOutcome(Refusal: null, token, transaction, Adapter: null, ChannelMode: null)
            {
                UnifiedChannelSet = channelSet
            };
    }

    /// <summary>
    /// Outcome of a refusal decided before the channel of the token is looked up.
    /// </summary>
    /// <param name="refusal">The refusal.</param>
    /// <param name="token">The decoded token, when there is one.</param>
    /// <param name="transaction">The loaded transaction, when there is one.</param>
    /// <returns>The outcome.</returns>
    private static HopPresentationOutcome Refused(HopRefusal refusal, HopToken? token, Transaction? transaction) =>
        new(refusal, token, transaction, Adapter: null, ChannelMode: null);
}
