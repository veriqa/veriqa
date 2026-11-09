// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Email.Abstractions;
using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Domain;
using Veriqa.Core.ChannelAdapter.Email.Enums;
using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// Handles one inbound mail of the Push mode (SPEC-016 §5) once it has been turned into an
/// <see cref="InboundEmailMessage"/>: deduplicates by message-id, delegates sender verification to
/// <see cref="IEmailInboundProcessor"/>, and on success either completes the sign-in through the pipeline
/// (effective surface <c>None</c>) or attaches the proved mailbox and leaves the decision to the sign-in window.
/// A mail correlated for the email completion of the claims records the verified sender's address instead.
/// </summary>
/// <remarks>
/// The handler is shared by every way a mail reaches the adapter, so it answers with an
/// <see cref="EmailInboundMailOutcome"/> and leaves it to the caller to act on it for its transport.
/// </remarks>
internal static class EmailInboundMailPipeline
{
    /// <summary>
    /// Handles one inbound mail and states what its source should do with it.
    /// </summary>
    /// <remarks>
    /// Preconditions of the caller: the tenant scope of the mail is open, and
    /// <see cref="InboundEmailMessage.MessageId"/> is not empty. Dependencies are taken from
    /// <paramref name="services"/> per call. An exception this handler does not catch reaches the caller.
    /// <para>
    /// The mail is one action of the channel (<see cref="ChannelInboundProcessor.OpenAction"/>): the admission of
    /// its sender is decided once — by the gate — and the write and the redelivery check below reuse that
    /// verdict. Its message-id identifies the letter to the budget of the confirmation operation, so a
    /// redelivery of the same mail, a retry among them, costs its sender no second attempt.
    /// </para>
    /// <para>
    /// A mail with a tenant — on the inbound route of a tenant, or through the polling of a tenant — acts
    /// only for a transaction of that tenant (SPEC-042 R60). The transaction its correlation names is
    /// compared before anything is done for it, and the processor then has to report that very
    /// transaction; any other answer is rejected like a mail of an unknown transaction, with nothing spent.
    /// </para>
    /// </remarks>
    /// <param name="services">The service provider of the caller's scope.</param>
    /// <param name="processor">The inbound processor that verifies the sender.</param>
    /// <param name="inbound">Inbound processing settings of the tenant.</param>
    /// <param name="message">The inbound mail.</param>
    /// <param name="cancellationToken">
    /// Cancels the reads only: completion and the message-id registration are not interrupted by it.
    /// </param>
    /// <returns>The outcome of handling the mail.</returns>
    internal static async Task<EmailInboundMailOutcome> HandleAsync(
        IServiceProvider services,
        IEmailInboundProcessor processor,
        EmailInboundOptions inbound,
        InboundEmailMessage message,
        CancellationToken cancellationToken)
    {
        // The method orchestrates one inbound mail: dedup, statuses, sender verification, completion
        using var action = ChannelInboundProcessor.OpenAction();

        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(EmailInboundMailPipeline));

        // The store keeps the budget marks of the letters next to the message-ids of this dedup, under keys of
        // a prefix of their own. A mail whose message-id has that form is not handled at all — not even
        // registered: it could otherwise set the mark of a letter that has not arrived yet.
        if (EmailLetterBudgetChargeMark.IsReservedKey(message.MessageId))
        {
            logger.LogWarning("Email inbound mail: the message-id has the form of a budget mark; the mail is not handled.");
            return EmailInboundMailOutcome.Rejected;
        }

        var correlationStore = services.GetRequiredService<IEmailPushCorrelationStore>();

        // Idempotent deduplication of redelivery by message-id (EM-023, EM-127).
        // Early read-only check: if the email has already been processed successfully — ignore it.
        // The message-id is registered ONLY after the transaction completes successfully
        // (see below) — otherwise a transient failure would mark the email as processed before it
        // actually completes, and redelivery/replay would become a no-op.
        if (await correlationStore.IsMessageProcessedAsync(message.MessageId, cancellationToken))
        {
            logger.LogInformation("Email inbound mail: repeated delivery ignored (dedup).");
            return EmailInboundMailOutcome.Completed;
        }

        // The token of the mail_received status, of the consumption on noRetry and of the consumption
        // after Completed is ONE token, extracted by ONE routine — which is why the extraction lives in
        // a shared method rather than in each step. The processor below runs the same routine again on
        // the same mail (it is reachable through its own SPI and cannot be handed a token through it),
        // so a mail is read twice; the cost is bounded by MaxCorrelationTokenProbes and the two reads
        // answer identically, because neither of them decides anything on its own.
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var correlationToken = await DefaultEmailInboundProcessor.ExtractCorrelationTokenAsync(
            message,
            inbound,
            correlationStore,
            now,
            cancellationToken);

        // The correlation is read once: it tells the mail of the email completion from a sign-in mail, and it
        // is what the "mail_received" status below is published for.
        var receivedCorrelation = string.IsNullOrEmpty(correlationToken)
            ? null
            : await correlationStore.GetAsync(correlationToken, cancellationToken);

        // The transaction an unexpired correlation names, worked out once for the tenant check and the status below;
        // null — no correlation, an expired one, or one whose transaction does not parse.
        TransactionId? unexpiredCorrelatedTransactionId =
            receivedCorrelation is not null
            && !receivedCorrelation.IsExpired(now)
            && TransactionId.TryParse(receivedCorrelation.TransactionId, out var parsedTransactionId)
                ? parsedTransactionId
                : null;

        // The tenant of the mail is compared with the transaction its unexpired correlation names before
        // anything is done for that transaction: a transaction of another tenant is answered like an
        // unknown token — no status, no processor, no spending of the correlation or of the message-id.
        // The inbound processor is taken on every mail: it is the one place the tenant of the event is
        // worked out, and the check of the transaction the processor of mails reports needs that tenant
        // even when there is no unexpired correlation.
        var inboundProcessor = services.GetRequiredService<ChannelInboundProcessor>();
        var eventTenantId = inboundProcessor.EventTenantOf(ChannelTypes.Email);
        TransactionId? comparedTransactionId = null;

        if (eventTenantId is not null && unexpiredCorrelatedTransactionId is { } correlatedTransactionId)
        {
            if (await ChannelWebhookPipeline.ReadOwnedTransactionAsync(
                    inboundProcessor.TransactionStore,
                    correlatedTransactionId,
                    eventTenantId,
                    ChannelTypes.Email,
                    inboundProcessor.Logger,
                    inboundProcessor.Metrics,
                    cancellationToken) is null)
            {
                return EmailInboundMailOutcome.Rejected;
            }

            comparedTransactionId = correlatedTransactionId;
        }

        var compared = new ComparedTransaction(eventTenantId, comparedTransactionId);

        // The mail of the Push way of the email completion (SPEC-041 L50) is not a sign-in mail: the sign-in is
        // already confirmed, so it takes its own branch before any status of the sign-in and before the
        // confirmation branch — the sender is verified the same way, and its address is recorded.
        if (!string.IsNullOrEmpty(correlationToken) && receivedCorrelation is { ForClaimCompletion: true })
        {
            return await HandleClaimCompletionMailAsync(
                services, message, correlationToken, processor, correlationStore, compared, logger, cancellationToken);
        }

        // Publish the "mail_received" status (SPEC-016 §10.1) if the email correlates
        // with an active transaction — before sender verification
        if (unexpiredCorrelatedTransactionId is { } receivedTransactionId
            && receivedCorrelation is { IsConsumed: false })
        {
            await EmailAuthEndpoints.PublishEmailChannelStatusAsync(
                services,
                receivedTransactionId,
                EmailChannelStatuses.PushMailReceived,
                logger);
        }

        // Delegate sender verification and result formation to the processor (handed in by the caller)
        var inboundResult = await processor.ProcessInboundEmailAsync(message, cancellationToken);

        if (inboundResult is not ChannelAuthConfirmResult confirmResult)
        {
            // The email does not belong to an active Push transaction or failed verification.
            // We intentionally do not register the message-id: a redelivery may arrive after
            // the transaction becomes active, and it should be processed again.
            logger.LogInformation("Email inbound mail: the mail did not result in a confirmation (Result={ResultType}).",
                inboundResult.GetType().Name);
            return EmailInboundMailOutcome.Rejected;
        }

        if (compared.RefusesReported(confirmResult.TransactionId, logger))
        {
            return EmailInboundMailOutcome.Rejected;
        }

        // The transaction of the verified mail, read once: it answers both whether the sender may move it
        // and which path the mail takes below.
        var verifiedTransaction = await ReadVerifiedTransactionAsync(
            services, confirmResult.TransactionId, logger);

        // The snapshot of the mail carries the tenant of its transaction, not the one the processor of mails
        // wrote (SPEC-003 CA-164): the attaches below go past the processing of the event that sets it
        // everywhere else. Compared above, the transaction is of the tenant of the mail when it states one.
        if (verifiedTransaction is not null)
        {
            confirmResult = confirmResult with
            {
                Identity = InboundSnapshotTenant.OfTransaction(
                    confirmResult.Identity, verifiedTransaction, ChannelTypes.Email, logger)
            };
        }

        // Sender verified — the verdict reaches the window only once the transaction has said whether it
        // admits the sender's subject (SPEC-016 §10.1): a transaction bound to the subject of another
        // channel refuses this mail (SPEC-039 N34), and "verified" would promise a sign-in that does not
        // follow. The refusal itself travels its usual path below; only the status differs. A transaction
        // that could not be read gets no verdict at all.
        if (verifiedTransaction is not null)
        {
            await EmailAuthEndpoints.PublishEmailChannelStatusAsync(
                services,
                confirmResult.TransactionId,
                verifiedTransaction.IsBoundToAnotherSubject(confirmResult.Identity)
                    ? EmailChannelStatuses.PushSubjectMismatch
                    : EmailChannelStatuses.PushVerified,
                logger);
        }

        // A CONFIRMATION does not end with the letter that identifies its sender: the letter says WHO,
        // and what is being confirmed is still to be shown and answered on the core page (SPEC-039
        // E29). The mail therefore attaches the identity and leaves the transaction waiting for that
        // answer, exactly as the magic link of the Pull mode does. A sign-in travels its usual path.
        // A transaction the store can no longer answer for is not called a confirmation, nor taken as a
        // decision of a sign-in: the check below asks the engine for the real state, and answering "yes"
        // on a failed read would turn an unavailable transaction into a silent success of this handler.
        var isConfirmation = verifiedTransaction is not null
            && string.Equals(verifiedTransaction.Type, TransactionTypes.Confirmation, StringComparison.Ordinal);

        var attachRefusedForGood = false;
        bool completed;
        var adapter = ChannelWebhookPipeline.FindAdapter(
            services.GetRequiredService<IEnumerable<IChannelAdapter>>(), ChannelTypes.Email);

        if (isConfirmation && verifiedTransaction is not null && adapter is not null)
        {
            var attachErrorCode = await AttachPushIdentityAsync(
                services, adapter, verifiedTransaction, confirmResult, message.MessageId, logger);
            completed = attachErrorCode is null;
            attachRefusedForGood = IsAttachRefusedForGood(attachErrorCode);
        }
        else if (verifiedTransaction is null)
        {
            // There is no transaction to read the surface off, so the mail is not taken as a decision: it is
            // not handled, and whether it is to be handed in again is answered below.
            completed = false;
        }
        else if (adapter is not null
                 && !await EmailAuthEndpoints.ShouldAutoConfirmAsync(services, verifiedTransaction, CancellationToken.None))
        {
            // On any surface but None the sign-in window asks the one question of the sign-in (SPEC-012
            // §4.4.2): the mail only attaches the mailbox it proved, and the window decides the sign-in.
            var attachErrorCode = await AttachSignInIdentityAsync(
                services, adapter, verifiedTransaction, confirmResult, message.MessageId, logger);
            completed = attachErrorCode is null;
            attachRefusedForGood = IsAttachRefusedForGood(attachErrorCode);
        }
        else
        {
            // The surface None: the mail completes the sign-in. A missing adapter is reported there, and the
            // mail — of a sign-in or of a confirmation — is then not handled.
            completed = await CompletePushTransactionAsync(
                services, correlationToken, confirmResult, message.MessageId, correlationStore, logger);
        }

        if (!completed)
        {
            // If the transaction is already terminal/unavailable — a retry won't help; reject the mail and record the dedup.
            var transactionService = services.GetRequiredService<ITransactionService>();
            var noRetry = attachRefusedForGood;

            var verifyTxResult = await transactionService.GetTransactionAsync(
                confirmResult.TransactionId, CancellationToken.None);

            if (verifyTxResult.IsFailure
                && verifyTxResult.Error.Code is TransactionErrorCodes.TransactionNotFound
                    or TransactionErrorCodes.TransactionExpired)
            {
                noRetry = true;
            }

            // Terminal is asked of the domain (Transaction.IsTerminal) instead of being spelled out
            // here: a transaction that has ENDED accepts nothing from this handler any more, and a
            // hand-written list of states is what left Completed — the state a confirmation answered
            // on the core page reaches — outside the answer. Neither Confirmed nor Created is
            // terminal, so neither ends the retry by itself: both still move (finalization, handover
            // of the freshly created transaction to the user), and a redelivery may yet be the one
            // that lands. The deadline term narrows that for the confirmed one — and it is no
            // refusal to finalize past the TTL: this handler itself finalizes, through the branch
            // above that hands a sign-in mail on the surface None to CompletePushTransactionAsync, and a
            // redelivery getting through there does drive Confirmed → Completed after the deadline
            // (the TTL of a confirmed transaction decides nothing — SPEC-001 §4.4 item 4). Here that
            // drive has already failed, and what is left to retry into is a wait the TTL does not
            // govern, bounded by the finalization window that the cleanup sweep ends — see
            // ITransactionStore.GetStalledConfirmedAsync.
            // So reject the mail and register the dedup rather than hold its source for all of it.
            if (verifyTxResult.IsSuccess
                && (verifyTxResult.Value.IsTerminal()
                    || verifyTxResult.Value.IsExpired(services.GetRequiredService<TimeProvider>().GetUtcNow())))
            {
                noRetry = true;
            }

            // A sign-in mail completed through the pipeline hands back no refusal code — its completion
            // answers yes or no — so the sender the transaction does not admit is found by asking the
            // gate itself: every redelivery of this mail carries the same identity and is refused the
            // same way, and the source is told to stop, as it is for an attach. No code is restated
            // here: any refusal of the gate counts. Asked only where the retry is still open, so a completed
            // sign-in and an ended transaction never pay for it. The subject binding is asked of the
            // transaction first: the completion refuses a sender other than the bound subject before
            // it reaches the gate (SPEC-039 N34), so the gate alone would admit that sender and leave
            // the source redelivering a mail that is refused the same way every time.
            if (!noRetry && !isConfirmation && verifyTxResult.IsSuccess)
            {
                noRetry = verifyTxResult.Value.IsBoundToAnotherSubject(confirmResult.Identity);

                if (!noRetry)
                {
                    var admissibility = await transactionService.ValidateChannelAdmissibilityAsync(
                        verifyTxResult.Value, confirmResult.Identity, CancellationToken.None);

                    noRetry = admissibility.IsFailure;
                }
            }

            if (noRetry)
            {
                logger.LogInformation(
                    "Email inbound mail: redelivery not needed (transaction unavailable, terminal or past its deadline, bound to another subject, or the sender is not admitted). TransactionId: {TransactionId}",
                    confirmResult.TransactionId);

                if (!string.IsNullOrEmpty(correlationToken))
                {
                    _ = await correlationStore.TryConsumeAsync(correlationToken, CancellationToken.None);
                }

                await correlationStore.TryRegisterProcessedMessageAsync(message.MessageId, CancellationToken.None);

                return EmailInboundMailOutcome.Rejected;
            }

            // Transaction completion failed (a pipeline/DB failure, or the transaction did not move to
            // Completed) — an internal transient error awaiting recovery. We intentionally do NOT
            // register the message-id and ask for the mail to be handed in again — otherwise accepting
            // it would lose the email.
            return EmailInboundMailOutcome.RetryLater;
        }

        // Register the message-id for deduplication only after confirmed transaction completion.
        // We use CancellationToken.None: cancellation by the caller after completion must not break
        // idempotency — otherwise the mail could be redelivered indefinitely.
        await correlationStore.TryRegisterProcessedMessageAsync(
            message.MessageId, CancellationToken.None);

        return EmailInboundMailOutcome.Completed;
    }

    /// <summary>
    /// Reads the transaction the verified mail names. One past its deadline is read past the TTL gate of
    /// the transaction service, so the path of the mail refuses it under its own class (LinkExpired,
    /// SPEC-039 E48) instead of leaving the mail unhandled and unjournaled.
    /// </summary>
    /// <param name="services">The service provider of the caller's scope.</param>
    /// <param name="transactionId">Transaction the verified mail names.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The transaction; null — the store could not answer for it.</returns>
    private static async Task<Transaction?> ReadVerifiedTransactionAsync(
        IServiceProvider services,
        TransactionId transactionId,
        ILogger logger)
    {
        var txResult = await services.GetRequiredService<ITransactionService>()
            .GetTransactionAsync(transactionId, CancellationToken.None);

        if (txResult.IsFailure && txResult.Error.Code is TransactionErrorCodes.TransactionExpired)
        {
            return await services.GetRequiredService<ITransactionStore>()
                .GetByIdAsync(transactionId, CancellationToken.None);
        }

        if (txResult.IsFailure)
        {
            logger.LogWarning(
                "Email inbound mail: failed to read the transaction of the verified mail. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transactionId,
                txResult.Error.Code);
            return null;
        }

        return txResult.Value;
    }

    /// <summary>
    /// Attaches the identity the inbound mail proved, leaving the confirmation waiting for its answer
    /// on the core page (SPEC-039 E29). The mirror of what the magic link does in the Pull mode, through the
    /// gate and the budget of the mail of a sign-in (<see cref="ChannelWebhookPipeline.AttachLetterIdentityAsync"/>).
    /// </summary>
    /// <remarks>
    /// Neither the correlation token nor the message-id changes hands here for the token's sake: the
    /// token is spent on a terminal success, and there is none — the answer is still ahead. A repeated
    /// delivery of the same mail attaches the same snapshot again, which the attach makes idempotent
    /// (the last delivery wins), and the dedup registration of the caller keeps that from happening
    /// needlessly. A budget the mail exhausts fails the confirmation, and the check of the caller then finds
    /// the transaction terminal.
    /// <para>
    /// The user may have no browser open at all, in which case the transaction waits out its TTL: that
    /// is a known limitation of asking the question on a page, and completing it here without ever
    /// showing the subject is the one thing that must not happen instead.
    /// </para>
    /// </remarks>
    /// <param name="services">The service provider of the caller's scope.</param>
    /// <param name="adapter">The Email channel adapter.</param>
    /// <param name="transaction">The confirmation the verified mail names, as read once by the caller.</param>
    /// <param name="inboundResult">The confirmation result from the processor.</param>
    /// <param name="messageId">Message-id of the mail — what identifies it to the budget of its sender.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>Null on success; otherwise the code the attach was refused with.</returns>
    private static async Task<string?> AttachPushIdentityAsync(
        IServiceProvider services,
        IChannelAdapter adapter,
        Transaction transaction,
        ChannelAuthConfirmResult inboundResult,
        string messageId,
        ILogger logger)
    {
        // CancellationToken.None, as on the completion path: the mail has arrived and its identity
        // must be recorded even if the caller cancels.
        var attachResult = await ChannelWebhookPipeline.AttachLetterIdentityAsync(
            services.GetRequiredService<ChannelInboundProcessor>(),
            transaction,
            inboundResult.Identity,
            adapter,
            messageId,
            CancellationToken.None);

        if (attachResult.IsSuccess)
        {
            return null;
        }

        logger.LogWarning(
            "Email inbound mail: the channel identity was not attached. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
            inboundResult.TransactionId,
            attachResult.Error.Code);

        return attachResult.Error.Code;
    }

    /// <summary>
    /// Attaches the mailbox the inbound mail of a sign-in proved, leaving the sign-in waiting for the answer
    /// of the sign-in window (SPEC-012 §4.4.2) — the path the "Continue signing in" of the Pull mode takes
    /// as well (<see cref="ChannelWebhookPipeline.AttachSignInLetterIdentityAsync"/>).
    /// </summary>
    /// <remarks>
    /// As with the mail of a confirmation (<see cref="AttachPushIdentityAsync"/>), neither the correlation
    /// token nor the message-id changes hands for the token's sake: the answer is still ahead. A budget the
    /// mail exhausts fails the sign-in, and the check of the caller then finds the transaction terminal.
    /// </remarks>
    /// <param name="services">The service provider of the caller's scope.</param>
    /// <param name="adapter">The Email channel adapter.</param>
    /// <param name="transaction">The sign-in the verified mail names, as read once by the caller.</param>
    /// <param name="inboundResult">The confirmation result from the processor.</param>
    /// <param name="messageId">Message-id of the mail — what identifies it to the budget of its sender.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>Null on success; otherwise the code the attach was refused with.</returns>
    private static async Task<string?> AttachSignInIdentityAsync(
        IServiceProvider services,
        IChannelAdapter adapter,
        Transaction transaction,
        ChannelAuthConfirmResult inboundResult,
        string messageId,
        ILogger logger)
    {
        // CancellationToken.None, as on the completion path: the mail has arrived and its identity
        // must be recorded even if the caller cancels.
        var attachResult = await ChannelWebhookPipeline.AttachSignInLetterIdentityAsync(
            services.GetRequiredService<ChannelInboundProcessor>(),
            transaction,
            inboundResult.Identity,
            adapter,
            messageId,
            CancellationToken.None);

        if (attachResult.IsSuccess)
        {
            return null;
        }

        logger.LogWarning(
            "Email inbound mail: the mailbox of the sign-in mail was not attached. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
            inboundResult.TransactionId,
            attachResult.Error.Code);

        return attachResult.Error.Code;
    }

    /// <summary>
    /// Whether an attach of the mail's identity was refused for a reason every redelivery of the same mail
    /// meets again, so that its source is told to stop rather than to retry.
    /// </summary>
    /// <remarks>
    /// A sender the transaction does not admit is refused the same way by every redelivery. An oversized
    /// snapshot is refused the same way and for the same reason: the identity is composed from THIS mail, so
    /// the next delivery of it measures the same bytes. A transaction bound to another subject refuses this
    /// sender for good as well: the binding is never lifted, so every redelivery meets it again (SPEC-039
    /// N34). A sign-in already in the claims completion refuses every redelivery too: the mark freezes its
    /// channel snapshot for the rest of the transaction (SPEC-041 L91), and the transaction leaves the
    /// completion only into a state the caller's check already ends — yet while it lasts the transaction is
    /// still Pending, so without this code the source would retry for the whole completion. Otherwise the
    /// set names refusals about THIS MAIL only; refusals about the transaction itself — it ended, or it ran
    /// out of time while still Pending — are answered by the check of the caller, which asks the engine for
    /// its current state instead of restating the codes here.
    /// </remarks>
    /// <param name="attachErrorCode">The code the attach was refused with; null — it was not refused.</param>
    /// <returns>true — no redelivery of the mail can be attached.</returns>
    private static bool IsAttachRefusedForGood(string? attachErrorCode) =>
        attachErrorCode is TransactionErrorCodes.ChannelNotAllowed
            or TransactionErrorCodes.BotRejected
            or TransactionErrorCodes.RestrictionPolicyViolation
            or TransactionErrorCodes.SecurityPolicyViolation
            or TransactionErrorCodes.SnapshotTooLarge
            or TransactionErrorCodes.ChannelSubjectMismatch
            or TransactionErrorCodes.ClaimCompletionInProgress;

    /// <summary>
    /// Completes the Push transaction through the channel pipeline and consumes the correlation token.
    /// Mirrors the Pull-confirm logic: the correlation is consumed only after the transaction's confirmed
    /// transition to Completed; on failure the correlation remains available within the TTL.
    /// </summary>
    /// <param name="services">The service provider of the caller's scope.</param>
    /// <param name="correlationToken">
    /// The correlation token, extracted from the email once in the calling code
    /// (single point of extraction). Null/empty — consumption is skipped.
    /// </param>
    /// <param name="inboundResult">The confirmation result from the processor.</param>
    /// <param name="messageId">Message-id of the mail — what identifies it to the budget of its sender.</param>
    /// <param name="correlationStore">The correlation token store.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>true — the transaction is completed and the correlation is consumed.</returns>
    private static async Task<bool> CompletePushTransactionAsync(
        IServiceProvider services,
        string? correlationToken,
        ChannelAuthConfirmResult inboundResult,
        string messageId,
        IEmailPushCorrelationStore correlationStore,
        ILogger logger)
    {
        // The method orchestrates transaction completion and single-use consumption of the correlation

        var adapters = services.GetRequiredService<IEnumerable<IChannelAdapter>>();
        var adapter = adapters.FirstOrDefault(a => a.ChannelType is ChannelTypes.Email);

        if (adapter is null)
        {
            logger.LogError("Email inbound mail: Email adapter not found in DI. TransactionId: {TransactionId}",
                inboundResult.TransactionId);
            return false;
        }

        var transactionService = services.GetRequiredService<ITransactionService>();
        var processor = services.GetRequiredService<ChannelInboundProcessor>();

        // Process through the pipeline (CancellationToken.None: completion must not be interrupted by the caller's cancellation)
        try
        {
            // Email sends no in-channel prompt, so the outcome reaches the user only through the
            // sign-in page — the letter's path asks no question in the chat. The terminal wording comes
            // from the core through the receipt port; the error text stays the channel's own.
            await processor.ProcessLetterAsync(
                inboundResult,
                adapter,
                new ChannelReplyTexts(MessageTemplateNaturalKeys.OutcomeErrorInChannel),
                messageId,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Email inbound mail: pipeline error while completing the Push transaction. TransactionId: {TransactionId}",
                inboundResult.TransactionId);
            return false;
        }

        // The pipeline does not return a success flag — re-read the transaction state (SPEC-016 §5)
        var verifyTxResult = await transactionService.GetTransactionAsync(
            inboundResult.TransactionId, CancellationToken.None);

        var isCompleted = verifyTxResult.IsSuccess
            && verifyTxResult.Value.State is TransactionState.Completed;

        if (!isCompleted)
        {
            logger.LogWarning(
                "Email inbound mail: transaction not finished in Completed. TransactionId: {TransactionId}",
                inboundResult.TransactionId);
            return false;
        }

        // Consume the correlation token only after confirmed transaction completion
        // (the token is extracted once in the calling code — single point of extraction)
        if (!string.IsNullOrEmpty(correlationToken))
        {
            var consumed = await correlationStore.TryConsumeAsync(correlationToken, CancellationToken.None);
            if (!consumed)
            {
                var existing = await correlationStore.GetAsync(correlationToken, CancellationToken.None);
                if (existing is not null
                    && !existing.IsExpired(services.GetRequiredService<TimeProvider>().GetUtcNow())
                    && !existing.IsConsumed)
                {
                    logger.LogWarning(
                        "Email inbound mail: failed to consume the correlation token after Completed. TransactionId: {TransactionId}",
                        inboundResult.TransactionId);
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Answers an inbound mail whose correlation is that of the <c>Push</c> way of the email completion
    /// (SPEC-041 L50, SPEC-016 §5.5): the sender is verified by the processor of the Push sign-in, and a
    /// verified sender's address is recorded as the verified email of the sign-in. A recorded address is
    /// announced to the pages of the sign-in by the channel status
    /// <see cref="EmailChannelStatuses.ClaimEmailConfirmed"/>, as the link of the completion mail announces it;
    /// no status of the Email sign-in is published — the sign-in is already confirmed.
    /// </summary>
    /// <remarks>
    /// A mail that failed the verification is answered as a sign-in mail that failed it: the processor has
    /// published <c>email_sender_verification_failed</c>, and the message-id is not registered. A write the engine
    /// refuses — the sign-in no longer waits for the address — is final: the correlation is spent and the
    /// message-id registered (anti-replay, SPEC-016 EM-023). A store that failed is transient: neither is
    /// touched, and the mail is asked to be handed in again.
    /// </remarks>
    /// <param name="services">The service provider of the caller's scope.</param>
    /// <param name="message">The inbound mail.</param>
    /// <param name="correlationToken">The correlation token the mail carries.</param>
    /// <param name="processor">The inbound processor that verifies the sender.</param>
    /// <param name="correlationStore">The correlation store.</param>
    /// <param name="compared">The tenant of the mail and the transaction of its correlation compared with it.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Cancels the sender verification only; the write is not interrupted by it.</param>
    /// <returns>The outcome of handling the mail.</returns>
    private static async Task<EmailInboundMailOutcome> HandleClaimCompletionMailAsync(
        IServiceProvider services,
        InboundEmailMessage message,
        string correlationToken,
        IEmailInboundProcessor processor,
        IEmailPushCorrelationStore correlationStore,
        ComparedTransaction compared,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var inboundResult = await processor.ProcessInboundEmailAsync(message, cancellationToken);

        if (inboundResult is not ChannelAuthConfirmResult { Identity.Email: { Length: > 0 } address } confirmResult)
        {
            logger.LogInformation(
                "Email inbound mail: the mail of the email completion recorded nothing (Result={ResultType}).",
                inboundResult.GetType().Name);
            return EmailInboundMailOutcome.Rejected;
        }

        if (compared.RefusesReported(confirmResult.TransactionId, logger))
        {
            return EmailInboundMailOutcome.Rejected;
        }

        Result<Transaction> recorded;
        bool held;

        // CancellationToken.None: the mail has arrived verified, and what it proves must be recorded — or held
        // while other fields of the web step are open — even if the caller cancels.
        try
        {
            var transactionService = services.GetRequiredService<ITransactionService>();
            var found = await transactionService.GetTransactionAsync(confirmResult.TransactionId, CancellationToken.None);
            var obtained = new ClaimEmailHeldAddress(address, Verified: true, EmailCompletionMethod.Push);
            var write = found.IsSuccess
                ? await ClaimEmailWrites.RecordOrHoldAsync(
                    transactionService,
                    services.GetService<IClaimCompletionEmail>(),
                    found.Value,
                    obtained,
                    services.GetRequiredService<TimeProvider>().GetUtcNow(),
                    CancellationToken.None)
                : new ClaimEmailWrite(Result<Transaction>.Failure(found.Error), Held: false);

            recorded = write.Outcome;
            held = write.Held;

            // Recorded or refused for good — either way this mail has done what it can.
            _ = await correlationStore.TryConsumeAsync(correlationToken, CancellationToken.None);
            await correlationStore.TryRegisterProcessedMessageAsync(message.MessageId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Email inbound mail: the mail of the email completion could not be recorded; it is to be retried later. TransactionId: {TransactionId}",
                confirmResult.TransactionId);

            return EmailInboundMailOutcome.RetryLater;
        }

        if (recorded.IsFailure)
        {
            logger.LogInformation(
                "Email inbound mail: the sign-in took no address from the mail of the email completion. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                confirmResult.TransactionId,
                recorded.Error.Code);
            return EmailInboundMailOutcome.Rejected;
        }

        logger.LogInformation(
            "Email inbound mail: the address of the email completion was confirmed by the mail of its owner. TransactionId: {TransactionId}, Held: {Held}",
            confirmResult.TransactionId,
            held);

        // The form of the web step learns of the confirmation from the hub and shows the block again by
        // itself. The hint goes out before the post-completion work, which may take long, and a page that
        // misses it still has its button to check the confirmation.
        await EmailAuthEndpoints.PublishEmailChannelStatusAsync(
            services,
            recorded.Value.Id,
            EmailChannelStatuses.ClaimEmailConfirmed,
            logger);

        // A held address has not ended the sign-in: the form records it when it is finished.
        if (!held)
        {
            await EmailAuthEndpoints.DispatchCompletionBehaviorAsync(services, recorded.Value);
        }

        return EmailInboundMailOutcome.Completed;
    }

    /// <summary>
    /// The tenant of a mail and the transaction of its correlation that was compared with it — what the
    /// transaction the processor reports is held against (SPEC-042 R60).
    /// </summary>
    /// <param name="EventTenantId">Tenant of the mail; null — none, and nothing is compared.</param>
    /// <param name="CorrelatedTransactionId">Transaction of the unexpired correlation of the mail, found to be of
    /// that tenant; null — the mail has no unexpired correlation, or no tenant.</param>
    private readonly record struct ComparedTransaction(string? EventTenantId, TransactionId? CorrelatedTransactionId)
    {
        /// <summary>
        /// Whether the transaction the processor reported is refused: the mail has a tenant, and the
        /// transaction is not strictly the one of its correlation — another transaction, of any tenant, or
        /// one reported for a mail whose correlation is missing, expired or was not compared. A refused mail
        /// is answered like a mail of an unknown transaction: nothing below the refusal runs.
        /// </summary>
        /// <param name="reported">Transaction the processor reported.</param>
        /// <param name="logger">The logger.</param>
        /// <returns>true — the mail is to be rejected.</returns>
        public bool RefusesReported(TransactionId reported, ILogger logger)
        {
            if (EventTenantId is null || CorrelatedTransactionId == reported)
            {
                return false;
            }

            logger.LogWarning(
                "Email inbound mail: the processor reported a transaction other than the one the correlation of the mail names — answered as an unknown transaction. ChannelType: {ChannelType}",
                ChannelTypes.Email);
            return true;
        }
    }
}
