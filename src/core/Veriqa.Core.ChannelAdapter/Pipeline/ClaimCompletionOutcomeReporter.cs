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
/// Implementation of the outcome report of a claim completion ended outside a channel event: the
/// channel and the tenant are taken off the identity snapshot of the transaction, the receipt is
/// worded by the port every receipt is worded by, and the notice is assembled and sent by the one
/// function the phone step sends its receipt through.
/// </summary>
internal sealed class ClaimCompletionOutcomeReporter : IClaimCompletionOutcomeReporter
{
    /// <summary>
    /// Registered channel adapters.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Port the wording of a confirmation and of a refusal comes from.
    /// </summary>
    private readonly IOutcomeReceiptText _receiptText;

    /// <summary>
    /// Natural Key localizer the neutral error text is localized by.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _localizer;

    /// <summary>
    /// Confirmation prompt orchestrator: answers whether the question was asked in the channel, which
    /// decides whether the receipt names the values of the calling party.
    /// </summary>
    private readonly IConfirmationPromptOrchestrator _confirmationPromptOrchestrator;

    /// <summary>
    /// Store of the phone number request waiting in a chat.
    /// </summary>
    private readonly IPhoneRequestCorrelationStore _phoneRequestCorrelationStore;

    /// <summary>
    /// Store of sent-prompt coordinates keyed by transaction.
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
    private readonly ILogger<ClaimCompletionOutcomeReporter> _logger;

    /// <summary>
    /// Creates the reporter of the outcome of a claim completion ended outside a channel event.
    /// </summary>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="receiptText">Port the wording of a receipt comes from.</param>
    /// <param name="localizer">Natural Key localizer.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator.</param>
    /// <param name="phoneRequestCorrelationStore">Store of the phone number request waiting in a chat.</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates.</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="metrics">Component metrics.</param>
    /// <param name="logger">Logger.</param>
    public ClaimCompletionOutcomeReporter(
        IEnumerable<IChannelAdapter> adapters,
        IOutcomeReceiptText receiptText,
        IConfirmationPromptLocalizer localizer,
        IConfirmationPromptOrchestrator confirmationPromptOrchestrator,
        IPhoneRequestCorrelationStore phoneRequestCorrelationStore,
        IChannelPromptMessageStore promptMessageStore,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ChannelAdapterMetrics metrics,
        ILogger<ClaimCompletionOutcomeReporter> logger)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _receiptText = receiptText ?? throw new ArgumentNullException(nameof(receiptText));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _confirmationPromptOrchestrator = confirmationPromptOrchestrator
            ?? throw new ArgumentNullException(nameof(confirmationPromptOrchestrator));
        _phoneRequestCorrelationStore = phoneRequestCorrelationStore
            ?? throw new ArgumentNullException(nameof(phoneRequestCorrelationStore));
        _promptMessageStore = promptMessageStore ?? throw new ArgumentNullException(nameof(promptMessageStore));
        _displayIntentSource = displayIntentSource ?? throw new ArgumentNullException(nameof(displayIntentSource));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ReportAsync(Transaction ended, CancellationToken cancellationToken = default)
    {
        // Leaves only for a transaction that has nothing to report here — never in the claim
        // completion, not ended, or expired — and for one with no channel to report to. Everything
        // else gets the receipt as a new message,
        // whether or not a reference to the question or to a phone number request is stored. Whatever
        // fails below is logged and swallowed: the sign-in has already ended.
        ArgumentNullException.ThrowIfNull(ended);

        // The completion mark is set once and stays on the transaction through its end, so it tells a
        // sign-in ended in the claim completion from one refused before its confirmation — the "No"
        // of the web confirmation page, which IWebConfirmationReceiptReporter shows in the chat, and a
        // sign-in window cancelled while the channel was still awaited, which
        // ISignInCancelChannelReporter shows in the chat when the question stood there.
        if (ended.ClaimCompletion is null)
        {
            return;
        }

        // An expiry is shown by the expiry handler, on the terms every expiry is shown on.
        if (TerminalTransactionOutcome.Of(ended) is not { } outcome || outcome == TransactionOutcome.Expired)
        {
            return;
        }

        if (ended.ChannelIdentitySnapshot is not { } snapshot)
        {
            _logger.LogDebug(
                "The ended sign-in has no channel identity snapshot — no outcome is shown in a channel. TransactionId: {TransactionId}",
                ended.Id.ToString());
            return;
        }

        try
        {
            // A web path has no channel tenant of its own, so the tenant the wording, the stores and
            // the send below read for is the one of the snapshot.
            using var tenantScope = ChannelTenantContext.BeginScope(snapshot.TenantId);

            var adapter = ChannelWebhookPipeline.FindAdapter(_adapters, snapshot.ChannelType);
            if (adapter is null)
            {
                _logger.LogDebug(
                    "No adapter serves the channel of the ended sign-in — no outcome is shown in a channel. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                    snapshot.ChannelType,
                    ended.Id.ToString());
                return;
            }

            await ChannelWebhookPipeline.ShowClaimCompletionOutcomeAsync(
                adapter,
                ended,
                outcome,
                await ChannelWebhookPipeline.RenderClaimCompletionOutcomeTextAsync(
                    _receiptText, _localizer, ended, outcome, ended.UpdatedAt, adapter,
                    _confirmationPromptOrchestrator, cancellationToken),
                _phoneRequestCorrelationStore,
                _promptMessageStore,
                _displayIntentSource,
                _logger,
                _metrics,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Showing the outcome of the claims completion in the channel failed; the sign-in result is not affected. ChannelType: {ChannelType}, TransactionId: {TransactionId}, Outcome: {Outcome}",
                snapshot.ChannelType,
                ended.Id.ToString(),
                outcome.Value);
        }
    }
}
