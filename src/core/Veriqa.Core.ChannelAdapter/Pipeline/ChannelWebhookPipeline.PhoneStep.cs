// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The answers of the phone step of claim completion (SPEC-041 L44–L47, L73): a shared contact, and
/// the secondary button of the request — "Skip" at an <c>Optional</c> number, "Cancel sign-in" at a
/// <c>Required</c> one.
/// </summary>
/// <remarks>
/// An answer is taken only from the chat of the user of the transaction's channel snapshot, and only
/// while the transaction waits in the phone step (L91); anything else is ignored without a reply — save
/// a text that matches the label of a button the step no longer takes, which is an ordinary message and
/// gets the answer any message naming no transaction gets. A write the engine refuses — the step closed
/// between the read and the write — is ignored without a reply.
/// The number is taken from nowhere but a shared contact that passed the channel's ownership check
/// (R5): no text of a message and no form ever becomes it.
/// </remarks>
public static partial class ChannelWebhookPipeline
{
    /// <summary>
    /// Handles a contact the user shared: the number of the transaction whose request waits in the
    /// sender's chat, a request sent again when the contact is not the sender's own (L46), or nothing
    /// when no request waits there.
    /// </summary>
    /// <param name="processor">Processor holding the services of the event's scope.</param>
    /// <param name="result">The shared contact.</param>
    /// <param name="adapter">Channel adapter the contact arrived through.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator (null — the path
    /// asks no question in the chat).</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — no prompt reference).</param>
    /// <param name="ownerTenantId">Tenant that owns the channel registration (SPEC-042 R60); null — any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task HandlePhoneSharedAsync(
        ChannelInboundProcessor processor,
        ChannelPhoneSharedResult result,
        IChannelAdapter adapter,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        IChannelPromptMessageStore? promptMessageStore,
        string? ownerTenantId,
        CancellationToken cancellationToken)
    {
        var logger = processor.Logger;
        var identity = result.Identity;

        // CA-121, SPEC-041 N68: the number itself never reaches the log — the fact and the channel do.
        var correlation = await processor.PhoneRequestCorrelationStore.FindAsync(
            identity.TenantId, identity.ChannelType, identity.ChannelUserId, cancellationToken);
        if (correlation is null)
        {
            logger.LogInformation(
                "Phone number received with no phone number request waiting in the chat — not taken. ChannelType: {ChannelType}",
                identity.ChannelType);
            return;
        }

        if (await ReadPhoneStepAsync(processor, correlation.TransactionId, identity, ownerTenantId, cancellationToken)
            is not { } step)
        {
            return;
        }

        var transaction = step.Transaction;

        if (result.OwnershipRejected)
        {
            logger.LogWarning(
                "Shared contact did not pass the ownership check of the channel — the number is not taken and the request is sent again. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                transaction.Id.ToString(),
                identity.ChannelType);

            if (await processor.PhoneNumberRequestBuilder.BuildAsync(transaction, cancellationToken) is { } request)
            {
                processor.ClaimCompletionDelivery.DeliverInBackground(transaction, request.Message);
            }

            return;
        }

        // SPEC-041 L47, SPEC-003 CA-009: the number is the adapter's to hand over in E.164. One that is
        // not already the canonical form counts as absent — it is not repaired and not written, and the
        // step keeps waiting as it would without an answer. An empty number is an adapter's regular answer
        // for a contact whose number has no E.164 form, so the warning blames no one for it.
        if (!E164PhoneNumber.IsCanonical(result.PhoneNumber))
        {
            logger.LogWarning(
                "Shared phone number is not in the E.164 format with the leading plus — counted absent, the phone step keeps waiting; an empty number means the channel adapter found no E.164 form of it, any other value is one the adapter left unconverted. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                transaction.Id.ToString(),
                identity.ChannelType);
            return;
        }

        // SPEC-041 R10: verified only where the channel proves that a shared number is the sender's own.
        var recorded = await processor.TransactionService.RecordClaimValueAsync(
            transaction.Id,
            VeriqaClaimTypes.PhoneNumber,
            result.PhoneNumber,
            verified: adapter.Capabilities.PhoneNumber.Proof is PhoneNumberProof.Proven,
            ClaimCollectionMethod.ChannelRequest,
            cancellationToken);

        await FinishPhoneStepWriteAsync(
            processor, transaction, recorded, adapter, confirmationPromptOrchestrator, promptMessageStore,
            cancellationToken);
    }

    /// <summary>
    /// Takes a message naming no transaction as the secondary button of the phone step when its text is
    /// the label of that button in the request waiting in the sender's chat — the form the button takes
    /// in a channel whose keyboard buttons carry no payload (SPEC-041 L73).
    /// </summary>
    /// <param name="processor">Processor holding the services of the event's scope.</param>
    /// <param name="result">The message naming no transaction.</param>
    /// <param name="adapter">Channel adapter the message arrived through.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator (null — the path
    /// asks no question in the chat).</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — no prompt reference).</param>
    /// <param name="ownerTenantId">Tenant that owns the channel registration (SPEC-042 R60); null — any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true — the message was the button and has been acted on; false — it is an ordinary
    /// message naming no transaction, including a text that matches the label while the step no longer
    /// takes the button: it gets the answer any such message gets.</returns>
    private static async Task<bool> TryApplyPhoneStepButtonTextAsync(
        ChannelInboundProcessor processor,
        ChannelUnaddressedResult result,
        IChannelAdapter adapter,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        IChannelPromptMessageStore? promptMessageStore,
        string? ownerTenantId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            return false;
        }

        var identity = result.Identity;
        var correlation = await processor.PhoneRequestCorrelationStore.FindAsync(
            identity.TenantId, identity.ChannelType, identity.ChannelUserId, cancellationToken);

        if (correlation is null
            || !string.Equals(result.Text.Trim(), correlation.SecondaryButtonLabel, StringComparison.Ordinal))
        {
            return false;
        }

        return await ApplyPhoneStepActionAsync(
            processor, correlation.TransactionId, identity, correlation.SecondaryAction, adapter,
            confirmationPromptOrchestrator, promptMessageStore, ownerTenantId, cancellationToken);
    }

    /// <summary>
    /// Acts on the secondary action of the phone step (SPEC-041 L73): "Skip" skips an <c>Optional</c>
    /// number, "Cancel sign-in" declines the sign-in in place of a <c>Required</c> one — the relying party
    /// then gets <c>access_denied</c>, as after a decline on the web confirmation page. An action that
    /// does not match the level of the number (a stale button) is ignored.
    /// </summary>
    /// <param name="processor">Processor holding the services of the scope.</param>
    /// <param name="transactionId">Transaction the action is for.</param>
    /// <param name="sender">Identity of the sender of the action in the channel.</param>
    /// <param name="action">The action.</param>
    /// <param name="adapter">Channel adapter the answer goes back through.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator (null — the path
    /// asks no question in the chat).</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — no prompt reference).</param>
    /// <param name="ownerTenantId">Tenant that owns the channel registration (SPEC-042 R60); null — any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true — the step took the action; false — it was ignored: the transaction does not wait
    /// in the step for this sender, or the action does not match the level of the number. A write the
    /// engine refuses counts as taken — the step was there when the action was read.</returns>
    internal static async Task<bool> ApplyPhoneStepActionAsync(
        ChannelInboundProcessor processor,
        TransactionId transactionId,
        ChannelIdentitySnapshot sender,
        PhoneStepAction action,
        IChannelAdapter adapter,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        IChannelPromptMessageStore? promptMessageStore,
        string? ownerTenantId,
        CancellationToken cancellationToken)
    {
        if (await ReadPhoneStepAsync(processor, transactionId, sender, ownerTenantId, cancellationToken)
            is not { } step)
        {
            return false;
        }

        var transaction = step.Transaction;

        if (PhoneNumberRequestBuilder.SecondaryActionOf(step.Level) != action)
        {
            processor.Logger.LogDebug(
                "Phone step action does not match the level of the phone number — ignored. TransactionId: {TransactionId}, Action: {Action}, Level: {Level}",
                transaction.Id.ToString(),
                action,
                step.Level);
            return false;
        }

        if (action is PhoneStepAction.Skip)
        {
            var skipped = await processor.TransactionService.SkipClaimAsync(
                transaction.Id, VeriqaClaimTypes.PhoneNumber, cancellationToken);

            await FinishPhoneStepWriteAsync(
                processor, transaction, skipped, adapter, confirmationPromptOrchestrator, promptMessageStore,
                cancellationToken);
            return true;
        }

        var declined = await processor.TransactionService.FailTransactionAsync(
            transaction.Id,
            TransactionErrorCodes.DeclinedByUser,
            transaction.ConcurrencyToken,
            cancellationToken);

        if (declined.IsFailure)
        {
            processor.Logger.LogDebug(
                "Cancellation of the sign-in in the phone step refused by the engine — ignored. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transaction.Id.ToString(),
                declined.Error.Code);
            return true;
        }

        // The cancellation is answered like the "No" of the question — the receipt of a decline.
        await ShowPhoneStepReceiptAsync(
            processor, transaction, TransactionOutcome.Declined, declined.Value.UpdatedAt, adapter,
            confirmationPromptOrchestrator, promptMessageStore, cancellationToken);
        return true;
    }

    /// <summary>
    /// Completes a write of the outcome of the phone number — a value or a skip: a write that ended the
    /// sign-in is answered with the receipt of its outcome — the confirmation, or the neutral error
    /// text when the sign-in failed on that write — which also takes the request away. A write that
    /// moved the completion to its web step is not answered in the chat: the page carries on.
    /// </summary>
    /// <remarks>
    /// The record of the request is not removed here. After a move to the web step it stays until the
    /// receipt of the sign-in removes it, or until it expires; the step takes no answer through it
    /// meanwhile, because every answer is checked against the current step of the transaction.
    /// </remarks>
    /// <param name="processor">Processor holding the services of the scope.</param>
    /// <param name="transaction">The transaction as read before the write.</param>
    /// <param name="written">The outcome of the write.</param>
    /// <param name="adapter">Channel adapter the answer goes back through.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator (null — the path
    /// asks no question in the chat).</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — no prompt reference).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task FinishPhoneStepWriteAsync(
        ChannelInboundProcessor processor,
        Transaction transaction,
        Result<Transaction> written,
        IChannelAdapter adapter,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        IChannelPromptMessageStore? promptMessageStore,
        CancellationToken cancellationToken)
    {
        if (written.IsFailure)
        {
            processor.Logger.LogDebug(
                "Answer of the phone step refused by the engine — ignored. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transaction.Id.ToString(),
                written.Error.Code);
            return;
        }

        // Still in the completion — the page carries on. An expiry is shown by its own handler.
        if (TerminalTransactionOutcome.Of(written.Value) is not { } outcome
            || outcome == TransactionOutcome.Expired)
        {
            return;
        }

        // The write ended the sign-in — completed, or failed: the finalization refused it, or a
        // required claim turned out unobtainable. Either end is answered, so neither the question
        // nor the request stays in the chat as if the sign-in were still waiting.
        await ShowPhoneStepReceiptAsync(
            processor, transaction, outcome, written.Value.UpdatedAt, adapter,
            confirmationPromptOrchestrator, promptMessageStore, cancellationToken);

        if (written.Value.State is TransactionState.Completed)
        {
            // The post-completion action follows the receipt and never replaces it (SPEC-012 §4.9);
            // it is handed the instance the write returned — the one carrying the resolved identity.
            await processor.CompletionBehaviorDispatcher.DispatchAsync(written.Value, cancellationToken);
        }
    }

    /// <summary>
    /// Reads the transaction an answer of the phone step is for, and admits the answer only when the
    /// transaction waits in that step and the answer comes from the chat of the user of its channel
    /// snapshot (SPEC-041 L91). The transaction is read past the TTL gate, and its deadline is checked
    /// here, so a late answer is ignored like any other the step no longer takes.
    /// </summary>
    /// <param name="processor">Processor holding the services of the scope.</param>
    /// <param name="transactionId">Transaction the answer names or its chat points to.</param>
    /// <param name="sender">Identity of the sender of the answer in the channel.</param>
    /// <param name="ownerTenantId">Tenant that owns the channel registration (SPEC-042 R60); null — any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction and the level of its missing number; null — the answer is ignored.</returns>
    private static async Task<PhoneStepTransaction?> ReadPhoneStepAsync(
        ChannelInboundProcessor processor,
        TransactionId transactionId,
        ChannelIdentitySnapshot sender,
        string? ownerTenantId,
        CancellationToken cancellationToken)
    {
        var transaction = await processor.TransactionStore.GetByIdAsync(transactionId, cancellationToken);

        if (transaction is null
            || (ownerTenantId is not null
                && !string.Equals(transaction.GetTenantId(), ownerTenantId, StringComparison.Ordinal)))
        {
            processor.Logger.LogDebug(
                "Answer of the phone step names no transaction of this registration — ignored. ChannelType: {ChannelType}",
                sender.ChannelType);
            return null;
        }

        if (transaction.ChannelIdentitySnapshot is not { } snapshot
            || !string.Equals(TenantKey.Segment(snapshot.TenantId), TenantKey.Segment(sender.TenantId), StringComparison.Ordinal)
            || !string.Equals(snapshot.ChannelType, sender.ChannelType, StringComparison.Ordinal)
            || !string.Equals(snapshot.ChannelUserId, sender.ChannelUserId, StringComparison.Ordinal))
        {
            processor.Logger.LogDebug(
                "Answer of the phone step did not come from the chat of the user of the transaction — ignored. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                transaction.Id.ToString(),
                sender.ChannelType);
            return null;
        }

        if (transaction.HasRunOutOfTime(processor.TimeProvider.GetUtcNow())
            || transaction.GetCurrentClaimCompletionStep() is not ClaimCompletionStep.Phone
            || PhoneNumberRequestBuilder.PhoneLevelOf(transaction) is not { } level)
        {
            processor.Logger.LogDebug(
                "Answer of the phone step arrived when the transaction no longer waits for it — ignored. TransactionId: {TransactionId}, State: {TransactionState}",
                transaction.Id.ToString(),
                transaction.State);
            return null;
        }

        return new PhoneStepTransaction(transaction, level);
    }

    /// <summary>
    /// Shows the receipt of an outcome the phone step ended the sign-in with, in the chat of the user of
    /// the channel snapshot — a confirmation and a decline worded like the ones the answer to the
    /// question in the chat shows, a failure by the neutral error text.
    /// </summary>
    /// <remarks>
    /// The receipt is always a new message carrying the reference to the request of the step: that
    /// message is what takes the request away.
    /// </remarks>
    /// <param name="processor">Processor holding the services of the scope.</param>
    /// <param name="transaction">The transaction as read before the write.</param>
    /// <param name="outcome">Outcome to show.</param>
    /// <param name="outcomeMoment">Moment the outcome was written down.</param>
    /// <param name="adapter">Channel adapter the receipt goes through.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator (null — the path
    /// asks no question in the chat).</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — no prompt reference).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task ShowPhoneStepReceiptAsync(
        ChannelInboundProcessor processor,
        Transaction transaction,
        TransactionOutcome outcome,
        DateTimeOffset outcomeMoment,
        IChannelAdapter adapter,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        IChannelPromptMessageStore? promptMessageStore,
        CancellationToken cancellationToken)
    {
        await ShowClaimCompletionOutcomeAsync(
            adapter,
            transaction,
            outcome,
            await RenderClaimCompletionOutcomeTextAsync(
                processor.ReceiptText, processor.PromptLocalizer, transaction, outcome, outcomeMoment,
                adapter, confirmationPromptOrchestrator, cancellationToken),
            processor.PhoneRequestCorrelationStore,
            promptMessageStore,
            processor.DisplayIntentSource,
            processor.Logger, processor.Metrics, cancellationToken);
    }

    /// <summary>
    /// A transaction waiting in the phone step and the level of its missing number.
    /// </summary>
    /// <param name="Transaction">The transaction as read.</param>
    /// <param name="Level">Requirement level of the missing phone number.</param>
    private readonly record struct PhoneStepTransaction(Transaction Transaction, ClaimRequirementLevel Level);
}
