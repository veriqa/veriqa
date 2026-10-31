// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The terminal receipt of a sign-in that went through claim completion (SPEC-003 CA-142, SPEC-041
/// L91): the one receipt the phone step and the paths that end the completion outside a channel event
/// — the form, the link and the mail of the email completion, the background end of the web step,
/// "Cancel sign-in" of the sign-in window — show in the chat of the user of the channel snapshot.
/// </summary>
/// <remarks>
/// The receipt always arrives as a new message: the message holding the question stays as the
/// claims completion state left it, and only a new message can take away what a phone number request
/// offered. Neither stored reference is a gate of the send — the receipt goes out with or without them.
/// </remarks>
public static partial class ChannelWebhookPipeline
{
    /// <summary>
    /// Renders the receipt of a confirmation or of a refusal that ended a sign-in in claim completion,
    /// addressed to the channel of the transaction's snapshot.
    /// </summary>
    /// <remarks>
    /// The values the calling party supplied are named only where the question was asked in this very
    /// channel: after "Yes" on the web confirmation page the chat never saw them.
    /// </remarks>
    /// <param name="receiptText">Port the wording of the receipt comes from.</param>
    /// <param name="transaction">Transaction the receipt reports; it carries a channel snapshot.</param>
    /// <param name="outcome">Outcome to word — a confirmation or a refusal.</param>
    /// <param name="outcomeMoment">Moment the outcome was written down.</param>
    /// <param name="adapter">Adapter of the snapshot's channel.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator (null — the path
    /// asks no question in the chat).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The receipt text, already localized.</returns>
    internal static async ValueTask<string> RenderClaimCompletionReceiptAsync(
        IOutcomeReceiptText receiptText,
        Transaction transaction,
        TransactionOutcome outcome,
        DateTimeOffset outcomeMoment,
        IChannelAdapter adapter,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        CancellationToken cancellationToken)
    {
        var snapshot = transaction.ChannelIdentitySnapshot!;

        // Recipient-locale chain (B17): channel snapshot locale → initiation-page language → base.
        return await receiptText.RenderAsync(
            ReceiptAddressOf(outcome, adapter, transaction),
            snapshot.Locale ?? transaction.GetUiLocale(),
            transaction.GetUiTimeZone(),
            outcomeMoment,
            TransactionSlotSource.From(transaction) with
            {
                CallerSlotValues = await ShownCallerSlotValuesAsync(
                    transaction, adapter, confirmationPromptOrchestrator, cancellationToken)
            },
            TransactionResolutionContext.For(transaction),
            cancellationToken);
    }

    /// <summary>
    /// Words the terminal outcome of a sign-in that ended in claim completion: a confirmation and a
    /// refusal by the receipt of that outcome (<see cref="RenderClaimCompletionReceiptAsync"/>); a
    /// failure that is not the user's refusal by the neutral error text — the receipt group has no
    /// wording for it, and the error text a channel registration declares belongs to the route of
    /// that channel's own outcomes.
    /// </summary>
    /// <param name="receiptText">Port the wording of a receipt comes from.</param>
    /// <param name="localizer">Natural Key localizer the neutral error text is localized by.</param>
    /// <param name="transaction">Transaction the text reports; it carries a channel snapshot.</param>
    /// <param name="outcome">Terminal outcome to word.</param>
    /// <param name="outcomeMoment">Moment the outcome was written down.</param>
    /// <param name="adapter">Adapter of the snapshot's channel.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator (null — the path
    /// asks no question in the chat).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The text, already localized.</returns>
    internal static async ValueTask<string> RenderClaimCompletionOutcomeTextAsync(
        IOutcomeReceiptText receiptText,
        IConfirmationPromptLocalizer localizer,
        Transaction transaction,
        TransactionOutcome outcome,
        DateTimeOffset outcomeMoment,
        IChannelAdapter adapter,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        CancellationToken cancellationToken)
    {
        if (outcome == TransactionOutcome.Confirmed || outcome == TransactionOutcome.Declined)
        {
            return await RenderClaimCompletionReceiptAsync(
                receiptText, transaction, outcome, outcomeMoment, adapter, confirmationPromptOrchestrator,
                cancellationToken);
        }

        // Recipient-locale chain (B17): channel snapshot locale → initiation-page language → base.
        return localizer.ResolveOrBaseText(
            MessageTemplateNaturalKeys.OutcomeErrorNeutral,
            transaction.ChannelIdentitySnapshot!.Locale ?? transaction.GetUiLocale());
    }

    /// <summary>
    /// Shows the terminal outcome of a sign-in that went through claim completion — the single place
    /// its notice is assembled. The caller has set the tenant of the transaction's snapshot as the
    /// ambient channel tenant.
    /// </summary>
    /// <remarks>
    /// The record of the chat is spent here and does not decide whether the notice is sent. The
    /// reference to the phone number request it carries travels on the notice, so the adapter takes
    /// away what the request offered; the reference to the question is dropped, because this receipt
    /// never replaces the question. The record tying the chat to the request is removed as well.
    /// </remarks>
    /// <param name="adapter">Adapter of the snapshot's channel.</param>
    /// <param name="ended">The ended transaction; it carries a channel snapshot.</param>
    /// <param name="outcome">Terminal outcome to show.</param>
    /// <param name="statusText">Already localized text of the receipt.</param>
    /// <param name="phoneRequestCorrelationStore">Store of the phone number request waiting in a chat.</param>
    /// <param name="promptMessageStore">Store of the chat of a transaction (null — the path keeps none,
    /// and the notice carries no reference to the request).</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task ShowClaimCompletionOutcomeAsync(
        IChannelAdapter adapter,
        Transaction ended,
        TransactionOutcome outcome,
        string statusText,
        IPhoneRequestCorrelationStore phoneRequestCorrelationStore,
        IChannelPromptMessageStore? promptMessageStore,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
    {
        var snapshot = ended.ChannelIdentitySnapshot!;

        // CancellationToken.None on both stores: the transaction has already ended, so what it left
        // behind is cleared even if the request is being cancelled.
        await RemovePhoneRequestCorrelationAsync(phoneRequestCorrelationStore, snapshot, ended.Id);

        // Not a gate: with no record the notice goes out all the same, without the reference.
        var chatRecord = promptMessageStore is null
            ? null
            : await promptMessageStore.TakeForChannelAsync(
                ended.Id, snapshot.ChannelType, CancellationToken.None);

        var notice = new TransactionOutcomeNotice
        {
            TransactionId = ended.Id,
            Outcome = outcome,
            ChannelUserId = snapshot.ChannelUserId,
            StatusText = statusText,
            InboundToken = null,
            PhoneRequestMessage = chatRecord?.PhoneRequestMessage,
            DisplayIntent = await displayIntentSource.ResolveAsync(
                TransactionResolutionContext.For(ended), cancellationToken)
        };

        await ChannelOutbound.NotifyOutcomeAsync(adapter, notice, logger, metrics, cancellationToken);
    }

    /// <summary>
    /// Removes the record tying the chat of the snapshot to the phone number request of the transaction.
    /// The store removes it only while it still points to this transaction.
    /// </summary>
    /// <param name="phoneRequestCorrelationStore">Store of the phone number request waiting in a chat.</param>
    /// <param name="snapshot">Channel snapshot of the ended transaction.</param>
    /// <param name="transactionId">Transaction whose request is over.</param>
    private static Task RemovePhoneRequestCorrelationAsync(
        IPhoneRequestCorrelationStore phoneRequestCorrelationStore,
        ChannelIdentitySnapshot snapshot,
        TransactionId transactionId) =>
        phoneRequestCorrelationStore.RemoveAsync(
            snapshot.TenantId, snapshot.ChannelType, snapshot.ChannelUserId, transactionId,
            CancellationToken.None);
}
