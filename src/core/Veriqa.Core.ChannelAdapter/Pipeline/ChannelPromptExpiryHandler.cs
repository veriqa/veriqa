// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Events;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Lifecycle handler that, on transaction TTL expiry, shows the user the terminal "expired" status in
/// the chat the transaction was started from (SPEC-003 §4.5), whether the question was asked there or
/// not. It states the same intent as the live confirm/decline path — the adapter picks the platform
/// operations — but is driven by <see cref="TransactionExpiredEvent"/> instead of an inbound callback.
/// The notice carries every reference the record of the chat has: to the prompt, and to a phone
/// number request still waiting in the chat.
///
/// If the transaction left no record (it never reached a channel that reports outcomes, or a different
/// instance wrote it in a multi-instance in-memory deployment) the handler is a graceful no-op. All failures are logged,
/// never thrown — a background lifecycle handler must not break event processing (a throw here is
/// isolated by the publisher, but we degrade explicitly).
/// </summary>
internal sealed class ChannelPromptExpiryHandler : ITransactionEventHandler
{
    /// <summary>
    /// Store of the chat a transaction was started from, keyed by transaction.
    /// </summary>
    private readonly IChannelPromptMessageStore _promptMessageStore;

    /// <summary>
    /// Registered channel adapters (resolved by <see cref="ChannelPromptMessageRef.ChannelType"/>).
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Terminal text of the transaction: the handler states the address of the receipt and the core
    /// answers with the wording, already localized (SPEC-036 TPL-123).
    /// </summary>
    private readonly IOutcomeReceiptText _receiptText;

    /// <summary>
    /// How the deployment wants the receipt to appear in the conversation. The background path states
    /// the very same intent the live one does — an expiry is shown the way every other terminal
    /// outcome of the same deployment is.
    /// </summary>
    private readonly IOutcomeNoticeDisplayIntentSource _displayIntentSource;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ChannelPromptExpiryHandler> _logger;

    /// <summary>
    /// Component metrics: an expiry notice the channel refused is counted by the outbound seam.
    /// This background path is the very reason the instrument travels as a parameter rather than
    /// being taken from a request's service provider — there is no request here.
    /// </summary>
    private readonly ChannelAdapterMetrics _metrics;

    /// <summary>
    /// Creates the expiry handler.
    /// </summary>
    /// <param name="promptMessageStore">Store of the chat a transaction was started from.</param>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="receiptText">Terminal text of the transaction.</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics.</param>
    public ChannelPromptExpiryHandler(
        IChannelPromptMessageStore promptMessageStore,
        IEnumerable<IChannelAdapter> adapters,
        IOutcomeReceiptText receiptText,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ILogger<ChannelPromptExpiryHandler> logger,
        ChannelAdapterMetrics metrics)
    {
        _promptMessageStore = promptMessageStore ?? throw new ArgumentNullException(nameof(promptMessageStore));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _receiptText = receiptText ?? throw new ArgumentNullException(nameof(receiptText));
        _displayIntentSource = displayIntentSource ?? throw new ArgumentNullException(nameof(displayIntentSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <inheritdoc />
    public async Task HandleAsync(TransactionEvent transactionEvent, CancellationToken cancellationToken = default)
    {
        // Only TTL expiry is relevant here: confirm/decline are already handled by the webhook pipeline.
        if (transactionEvent is not TransactionExpiredEvent expired)
        {
            return;
        }

        // Take (get + remove) the record of the chat. Null — nobody to report the expiry to.
        var reference = await _promptMessageStore.TakeAsync(expired.TransactionId, cancellationToken);
        if (reference is null)
        {
            return;
        }

        var adapter = FindAdapter(reference.ChannelType);
        if (adapter is null)
        {
            _logger.LogWarning(
                "No adapter for channel {ChannelType} to report the expired transaction. TransactionId: {TransactionId}",
                reference.ChannelType,
                expired.TransactionId.ToString());
            return;
        }

        // Restore the tenant captured when the record was written: a background handler has no ambient request
        // context, and the adapter resolves its per-tenant client/credentials from ChannelTenantContext.
        // The scope is for the ADAPTER's credentials only — it is no longer the source of the ownership
        // the receipt is worded over: that comes from the stored record below. Tenant is the level the
        // prompt store contracts for (ChannelPromptMessageRef.TenantId), and this receipt is worded at
        // that level on purpose: the expiry event does carry an application id, but it is deliberately
        // not promoted into the receipt's ownership, so the single record captured at the start stays
        // the whole answer here. The ui_config record of an expired transaction cannot be read back
        // at all.
        using (ChannelTenantContext.BeginScope(reference.TenantId))
        {
            // The type of the transaction comes from the event: the publisher of the expiry states it
            // in the context of the event, and the transaction itself is already gone by now. The
            // surface is the channel the transaction was started from, the locale is the one captured
            // on the record; a missing translation falls back to the base text of the wording.
            var address = new OutcomeReceiptAddress(
                TransactionOutcome.Expired,
                OutcomeReceiptSurfaces.ForChannel(adapter),
                expired.Context?.TransactionType,
                expired.Context?.ConfirmationParameters?.ActionType,
                reference.ChannelType);

            // The ownership the receipt is worded over is the ownership its display is resolved for:
            // the tenant captured on the stored record, exactly as the wording below states it.
            var displayIntent = await _displayIntentSource.ResolveAsync(
                ResolutionContext.ForTenant(reference.TenantId),
                cancellationToken);

            var notice = new TransactionOutcomeNotice
            {
                TransactionId = expired.TransactionId,
                Outcome = TransactionOutcome.Expired,
                ChannelUserId = reference.ChannelUserId,
                StatusText = await _receiptText.RenderAsync(
                    address,
                    reference.RecipientLocale,
                    // Zone off the SAME record the locale is: the two are one answer to how this
                    // recipient reads the receipt, and reading one half from the send-time snapshot
                    // and the other from the event would be two sources for one thing. The moment of
                    // the outcome is the TTL the transaction ended at, which the expiry event states
                    // beside the moment of its own publication — the same moment the pages of the
                    // core report for an expiry, and never the clock of the sweep that noticed it.
                    reference.RecipientTimeZone,
                    expired.ExpiresAt,
                    // The values come off the event beside the action type of the address, for the same
                    // reason: the transaction itself is already gone. The attribution of the event
                    // carries no initiator context, so the application is named by the display name
                    // the transaction took at its creation, as a confirmation names it everywhere.
                    expired.Context is { } eventContext ? SlotSourceOf(eventContext, reference) : null,
                    ResolutionContext.ForTenant(reference.TenantId),
                    cancellationToken),
                // No inbound token on the background path: nobody pressed anything.
                PromptMessage = reference.Message,
                // A request still waiting in the chat: the adapter takes away what it offered.
                PhoneRequestMessage = reference.PhoneRequestMessage,
                DisplayIntent = displayIntent
            };

            await ChannelOutbound.NotifyOutcomeAsync(adapter, notice, _logger, _metrics, cancellationToken);
        }
    }

    /// <summary>
    /// Builds the source of the slots the receipt is worded from. The values the calling party
    /// supplied are named only where the question was shown in this very channel: a chat that never
    /// saw the question — it was asked on the web page, or not asked at all — never saw them either.
    /// </summary>
    /// <param name="eventContext">Context of the expiry event.</param>
    /// <param name="reference">Record of the chat the transaction was started from.</param>
    /// <returns>The source of the slots.</returns>
    private static TransactionSlotSource SlotSourceOf(
        TransactionEventContext eventContext,
        ChannelPromptMessageRef reference)
    {
        var source = TransactionSlotSource.From(eventContext);

        return reference.PromptShown
            ? source
            : source.WithoutCallerValues();
    }

    /// <summary>
    /// Finds the adapter serving the given channel type via a linear scan (there are only a few).
    /// </summary>
    /// <param name="channelType">Channel type to resolve.</param>
    /// <returns>Adapter of the channel type, or null when not registered.</returns>
    private IChannelAdapter? FindAdapter(string channelType)
    {
        foreach (var adapter in _adapters)
        {
            if (string.Equals(adapter.ChannelType, channelType, StringComparison.Ordinal))
            {
                return adapter;
            }
        }

        return null;
    }
}
