// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Implementation of the receipt of a cancel in the chat — of the sign-in window, of the entry page of a
/// confirmation and of the scanner page of the unified hop QR code of either type alike
/// (<see cref="ISignInCancelChannelReporter"/>): the recipient, the tenant,
/// the locale and the zone come off the record of the chat — captured when the question was shown to
/// that recipient — the wording is the one of the refusal in the channel, and the notice is sent
/// through the outbound seam every terminal notice goes through.
/// </summary>
internal sealed class SignInCancelChannelReporter : ISignInCancelChannelReporter
{
    /// <summary>
    /// Store of the chat a transaction was started from, keyed by transaction.
    /// </summary>
    private readonly IChannelPromptMessageStore _promptMessageStore;

    /// <summary>
    /// Registered channel adapters.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Port the wording of a refusal comes from.
    /// </summary>
    private readonly IOutcomeReceiptText _receiptText;

    /// <summary>
    /// How the deployment wants the receipt to appear in the conversation.
    /// </summary>
    private readonly IOutcomeNoticeDisplayIntentSource _displayIntentSource;

    /// <summary>
    /// Component metrics a refused notice is counted into.
    /// </summary>
    private readonly ChannelAdapterMetrics _metrics;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<SignInCancelChannelReporter> _logger;

    /// <summary>
    /// Creates the reporter of a cancel.
    /// </summary>
    /// <param name="promptMessageStore">Store of the chat a transaction was started from.</param>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="receiptText">Port the wording of a receipt comes from.</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="metrics">Component metrics.</param>
    /// <param name="logger">Logger.</param>
    public SignInCancelChannelReporter(
        IChannelPromptMessageStore promptMessageStore,
        IEnumerable<IChannelAdapter> adapters,
        IOutcomeReceiptText receiptText,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ChannelAdapterMetrics metrics,
        ILogger<SignInCancelChannelReporter> logger)
    {
        _promptMessageStore = promptMessageStore ?? throw new ArgumentNullException(nameof(promptMessageStore));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _receiptText = receiptText ?? throw new ArgumentNullException(nameof(receiptText));
        _displayIntentSource = displayIntentSource ?? throw new ArgumentNullException(nameof(displayIntentSource));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ReportAsync(
        Transaction beforeRefusal,
        Transaction refused,
        CancellationToken cancellationToken = default)
    {
        // Checks that the answer was still awaited in the channel, takes the record of the chat, checks
        // that the question was shown there, and sends the receipt of the refusal under the tenant of
        // the record. Whatever fails is logged and swallowed: the refusal is already written, and the
        // answer of the page — access_denied to the client of the sign-in window, the receipt of the entry
        // page or of the scanner page — is the same either way.
        ArgumentNullException.ThrowIfNull(beforeRefusal);
        ArgumentNullException.ThrowIfNull(refused);

        // Read before the refusal was written: a transaction not yet entered in a channel, one whose
        // question was handed to the core page, a sign-in in the claims completion or one already decided
        // in the channel has no question standing in a chat — and the record of its chat is left
        // where it is.
        if (!beforeRefusal.IsAwaitingAnswerInChannel())
        {
            return;
        }

        var transactionId = beforeRefusal.Id.ToString();

        try
        {
            // The refusal is terminal, so the record is spent whatever happens next: nothing reads it
            // after a Failed transaction.
            var reference = await _promptMessageStore.TakeAsync(beforeRefusal.Id, cancellationToken);
            if (reference is null)
            {
                _logger.LogDebug(
                    "The cancelled transaction left no record of its chat — no receipt is shown in a channel. TransactionId: {TransactionId}",
                    transactionId);
                return;
            }

            if (!reference.PromptShown)
            {
                _logger.LogDebug(
                    "The question of the cancelled transaction was not shown in the chat — no receipt is shown in a channel. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                    reference.ChannelType,
                    transactionId);
                return;
            }

            // A web request has no channel tenant of its own: the adapter's credentials, the wording
            // and the display intent below are read for the tenant captured on the record.
            using var tenantScope = ChannelTenantContext.BeginScope(reference.TenantId);

            var adapter = ChannelWebhookPipeline.FindAdapter(_adapters, reference.ChannelType);
            if (adapter is null)
            {
                _logger.LogWarning(
                    "No adapter for channel {ChannelType} to show the cancelled transaction. TransactionId: {TransactionId}",
                    reference.ChannelType,
                    transactionId);
                return;
            }

            var resolutionContext = TransactionResolutionContext.For(beforeRefusal);

            var notice = new TransactionOutcomeNotice
            {
                TransactionId = beforeRefusal.Id,
                Outcome = TransactionOutcome.Declined,
                ChannelUserId = reference.ChannelUserId,
                // The address and the values are those of the refusal in the channel: the question
                // stood before this user in this channel, so the values the calling party supplied may
                // be named. Locale and zone come off the record — the transaction carries no channel
                // snapshot in this state — and the moment is the one the write of the refusal recorded.
                StatusText = await _receiptText.RenderAsync(
                    ChannelWebhookPipeline.ReceiptAddressOf(TransactionOutcome.Declined, adapter, beforeRefusal),
                    reference.RecipientLocale,
                    reference.RecipientTimeZone,
                    refused.UpdatedAt,
                    TransactionSlotSource.From(beforeRefusal),
                    resolutionContext,
                    cancellationToken),
                // Nobody pressed anything in the chat.
                InboundToken = null,
                // Null when the channel keeps no reference: the adapter then sends a new message.
                PromptMessage = reference.Message,
                PhoneRequestMessage = reference.PhoneRequestMessage,
                DisplayIntent = await _displayIntentSource.ResolveAsync(resolutionContext, cancellationToken)
            };

            await ChannelOutbound.NotifyOutcomeAsync(adapter, notice, _logger, _metrics, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Showing the cancelled transaction in the channel failed; the answer of the page is not affected. TransactionId: {TransactionId}",
                transactionId);
        }
    }
}
