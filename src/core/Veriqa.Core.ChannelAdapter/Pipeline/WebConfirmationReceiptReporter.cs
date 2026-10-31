// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Implementation of the receipt of an answer given on the web confirmation page: the recipient, the
/// channel and the tenant come off the channel snapshot the transaction attached when its question was
/// handed over to the page, the locale and the zone off the same chain the notice of that hand-over was
/// worded with, and the notice is sent through the outbound seam every terminal notice goes through.
/// </summary>
internal sealed class WebConfirmationReceiptReporter : IWebConfirmationReceiptReporter
{
    /// <summary>
    /// Registered channel adapters.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Port the wording of a receipt comes from.
    /// </summary>
    private readonly IOutcomeReceiptText _receiptText;

    /// <summary>
    /// Store of the chat record the start in the channel left for the transaction.
    /// </summary>
    private readonly IChannelPromptMessageStore _promptMessageStore;

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
    private readonly ILogger<WebConfirmationReceiptReporter> _logger;

    /// <summary>
    /// Creates the reporter of an answer given on the web confirmation page.
    /// </summary>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="receiptText">Port the wording of a receipt comes from.</param>
    /// <param name="promptMessageStore">Store of the chat record of the transaction.</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="metrics">Component metrics.</param>
    /// <param name="logger">Logger.</param>
    public WebConfirmationReceiptReporter(
        IEnumerable<IChannelAdapter> adapters,
        IOutcomeReceiptText receiptText,
        IChannelPromptMessageStore promptMessageStore,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ChannelAdapterMetrics metrics,
        ILogger<WebConfirmationReceiptReporter> logger)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _receiptText = receiptText ?? throw new ArgumentNullException(nameof(receiptText));
        _promptMessageStore = promptMessageStore ?? throw new ArgumentNullException(nameof(promptMessageStore));
        _displayIntentSource = displayIntentSource ?? throw new ArgumentNullException(nameof(displayIntentSource));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ReportAsync(Transaction decided, CancellationToken cancellationToken = default)
    {
        // Leaves for an outcome that is not the answer of the page; then consumes the chat record of the
        // transaction, finds the channel of the snapshot under its tenant and sends the receipt as a new
        // message. A sign-in and a confirmation are answered alike (SPEC-012 CFG-266): the address of
        // the receipt names the type, so only the words differ. Whatever fails below is logged and
        // swallowed: the answer is already written, and the browser gets its response either way.
        ArgumentNullException.ThrowIfNull(decided);

        // "Yes" completed the transaction, "No" refused it; anything else is not an answer of the page.
        if (TerminalTransactionOutcome.Of(decided) is not { } outcome
            || (outcome != TransactionOutcome.Confirmed && outcome != TransactionOutcome.Declined))
        {
            return;
        }

        var transactionId = decided.Id.ToString();

        await ConsumeChatRecordAsync(decided, transactionId, cancellationToken);

        if (decided.ChannelIdentitySnapshot is not { } snapshot)
        {
            _logger.LogDebug(
                "The answered transaction has no channel identity snapshot — no receipt is shown in a channel. TransactionId: {TransactionId}",
                transactionId);
            return;
        }

        try
        {
            // A web request has no channel tenant of its own: the adapter's credentials, the wording and
            // the display intent below are read for the tenant the snapshot was captured under.
            using var tenantScope = ChannelTenantContext.BeginScope(snapshot.TenantId);

            var adapter = ChannelWebhookPipeline.FindAdapter(_adapters, snapshot.ChannelType);
            // Debug, as the post-completion action that follows at the same point logs it: a host that
            // serves no adapter of the channel of the snapshot has no chat to tell.
            if (adapter is null)
            {
                _logger.LogDebug(
                    "No adapter serves the channel of the answered transaction — no receipt is shown in a channel. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                    snapshot.ChannelType,
                    transactionId);
                return;
            }

            // Checked before the wording is resolved: a channel that shows no outcome notices — Email,
            // which reaches the page by a letter and gets no notice of the hand-over — has nothing to
            // word.
            if (!adapter.Capabilities.DeliversOutcomeNotice)
            {
                return;
            }

            var resolutionContext = TransactionResolutionContext.For(decided);

            var notice = new TransactionOutcomeNotice
            {
                TransactionId = decided.Id,
                Outcome = outcome,
                ChannelUserId = snapshot.ChannelUserId,
                // The recipient-locale chain and the zone are those the notice of the hand-over was
                // worded with: the channel snapshot locale, then the language of the initiating page.
                // The question was asked on the page and never in the chat, so the values the calling
                // party supplied are not named; the moment is the one the write of the answer recorded.
                StatusText = await _receiptText.RenderAsync(
                    ChannelWebhookPipeline.ReceiptAddressOf(outcome, adapter, decided),
                    snapshot.Locale ?? decided.GetUiLocale(),
                    decided.GetUiTimeZone(),
                    decided.UpdatedAt,
                    TransactionSlotSource.From(decided).WithoutCallerValues(),
                    resolutionContext,
                    cancellationToken),
                // Nobody pressed anything in the chat.
                InboundToken = null,
                // No question stood in the chat and the notice of the hand-over is not referenced: the
                // adapter sends a new message.
                PromptMessage = null,
                PhoneRequestMessage = null,
                DisplayIntent = await _displayIntentSource.ResolveAsync(resolutionContext, cancellationToken)
            };

            await ChannelOutbound.NotifyOutcomeAsync(adapter, notice, _logger, _metrics, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Showing the answer of the web confirmation page in the channel failed; the answer to the browser is not affected. ChannelType: {ChannelType}, TransactionId: {TransactionId}, Outcome: {Outcome}",
                snapshot.ChannelType,
                transactionId,
                outcome.Value);
        }
    }

    /// <summary>
    /// Takes the chat record the start in the channel left for the answered transaction. The answer of the
    /// page is its terminal outcome, the one a channel event would have consumed the record with: no expiry
    /// and no cancel is left to be reported by it, so it must not outlive the transaction in the store with the
    /// address of the chat (SPEC-003 §6.2). The receipt itself is addressed by the channel snapshot.
    /// Best-effort: a failure of the store is logged and leaves the record to its retention.
    /// </summary>
    /// <param name="decided">The answered transaction.</param>
    /// <param name="transactionId">Identifier of the transaction, for the log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed operation.</returns>
    private async Task ConsumeChatRecordAsync(Transaction decided, string transactionId, CancellationToken cancellationToken)
    {
        try
        {
            await _promptMessageStore.TakeAsync(decided.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Consuming the chat record of the transaction answered on the web confirmation page failed; the record stays until its retention. TransactionId: {TransactionId}",
                transactionId);
        }
    }
}
