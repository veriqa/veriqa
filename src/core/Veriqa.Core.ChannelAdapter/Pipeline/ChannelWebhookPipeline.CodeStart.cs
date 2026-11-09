// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Contracts;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The start of a sign-in by the short code of a transaction: the code a user sent to the bot is
/// matched to the transaction it stands for, and the start then takes the very path a start by link
/// takes.
/// </summary>
/// <remarks>
/// The code is a secret of the transaction for as long as the transaction lives, so nothing here
/// writes it — or the text the user sent — to a log, on any level.
/// </remarks>
public static partial class ChannelWebhookPipeline
{
    /// <summary>
    /// Drops a start by a code that came through a channel which declared no start by a code
    /// (<see cref="ChannelCapabilities.CodeStart"/> is <c>null</c>) — a built-in, a third-party or an HTTP
    /// channel alike. Such a channel shows no code and promises none, so the event is not served: no
    /// attempt of the sender is taken, the store of codes is not asked, nothing is answered in the
    /// channel. The event is counted as a start that was not served, and the warning names the channel
    /// alone — never the code or the text the user sent.
    /// </summary>
    /// <param name="adapter">Channel adapter the event arrived through.</param>
    /// <param name="logger">Logger of the pipeline.</param>
    /// <param name="metrics">Metrics of the channel adapters.</param>
    private static void DropUndeclaredCodeStart(IChannelAdapter adapter, ILogger logger, ChannelAdapterMetrics metrics)
    {
        metrics.RecordUnservedStart(adapter.ChannelType);

        logger.LogWarning(
            "Sign-in start by a code from a channel that declares no start by a code — the event is not processed. " +
            "ChannelType: {ChannelType}",
            adapter.ChannelType);
    }

    /// <summary>
    /// Handles a sign-in start that names a code instead of a transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attempt of the sender is taken first, before the store of codes is asked anything: the
    /// existing budget of sign-in starts per channel user is what bounds guessing, and a guess must
    /// cost an attempt whether or not it hits. It is the one attempt of the event — the start is then
    /// handed on with no limiter, so none of the points of the start path that charge a start by link
    /// charges this one again.
    /// </para>
    /// <para>
    /// A code that names no live record — unknown, expired, already taken, or of another tenant — is a
    /// start that cannot be served: it is counted like an identifier no transaction answers to and
    /// answered with the text the channel declares for such a code — one text for every reason, so the
    /// answer does not tell whether the code ever existed — and nothing is written to the journal,
    /// which is keyed by a transaction this event does not have.
    /// </para>
    /// <para>
    /// With a tenant of the event stated (SPEC-042 R60), the store takes the code only when it was issued
    /// to a transaction of that tenant, and a code of another tenant stays where it is. The sender of
    /// such an event is whatever its source names, so the limit per channel user does not bound guessing
    /// there: the pair "tenant of the event + channel type" has a budget of codes that took no record.
    /// It is looked at before the store is asked — a spent budget leaves the start unanswered, the store
    /// untouched — and only a code that took no record spends it. A value that is no code at all, and a
    /// failure of the store, neither look at it nor spend it. Without a tenant of the event the code is
    /// looked up among the codes of the whole deployment and there is no such budget.
    /// </para>
    /// </remarks>
    /// <param name="processor">Processor holding the services of the event's scope.</param>
    /// <param name="result">The start, carrying the code as the user sent it.</param>
    /// <param name="adapter">Channel adapter the event arrived through.</param>
    /// <param name="replyTexts">Texts the channel states for itself.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator (null — the path
    /// asks no question in the chat).</param>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="ownerTenantId">Tenant of the event (SPEC-042 R60); null — none, the code of a
    /// transaction of any tenant is served.</param>
    /// <param name="snapshotTenant">The tenant of the identity snapshot of the event (SPEC-003 CA-164);
    /// without a tenant of the event the snapshot has none until the start reads the transaction of the
    /// code, so the attempt taken here is charged to the key without a tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task HandleAuthStartByCodeAsync(
        ChannelInboundProcessor processor,
        ChannelAuthStartByCodeResult result,
        IChannelAdapter adapter,
        ChannelReplyTexts replyTexts,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        IConfirmationPromptLocalizer? promptLocalizer,
        string? ownerTenantId,
        InboundSnapshotTenant snapshotTenant,
        CancellationToken cancellationToken)
    {
        var logger = processor.Logger;
        var metrics = processor.Metrics;
        var userKey = NormalizeUserKey(result.Identity);

        if (processor.UserAuthRateLimiter is { } rateLimiter)
        {
            var rateLimitResult = await rateLimiter.TryAcquireAsync(
                UserAuthRateLimitOperations.AuthStart + userKey, cancellationToken);

            if (!rateLimitResult.IsAllowed)
            {
                // Whether the code was a live one is not looked at: the store is not asked, so the
                // event is counted as a start that was not served and left without an answer.
                metrics.RecordUnservedStart(adapter.ChannelType);

                logger.LogWarning(
                    "Per-user rate limit exceeded — the sign-in start by code stays unanswered. " +
                    "ChannelType: {ChannelType}, UserKeyHash: {UserKeyHash}",
                    adapter.ChannelType,
                    LogMasking.Fingerprint(userKey));
                return;
            }
        }

        // A value that cannot be a code of any deployment never reaches the store, and it is no guess
        // of a code either: the budget of unmatched codes is neither looked at nor spent for it.
        if (!ChannelStartCodeText.TryNormalize(result.Code, out var code)
            || processor.StartCodeStore is not { } startCodeStore)
        {
            await AnswerUnmatchedCodeAsync(
                result, adapter, replyTexts, promptLocalizer, logger, metrics, userKey, cancellationToken);
            return;
        }

        var budget = processor.UnmatchedStartCodeBudget;

        if (ownerTenantId is not null
            && budget is not null
            && budget.IsExhausted(ownerTenantId, adapter.ChannelType))
        {
            // Whether the code was a live one is not looked at: the store is not asked, so the event is
            // counted as a start that was not served and left without an answer until the window ends.
            metrics.RecordUnservedStart(adapter.ChannelType);

            logger.LogWarning(
                "The budget of unmatched start codes of the tenant of the event is spent — the sign-in start by code stays unanswered. " +
                "ChannelType: {ChannelType}, EventTenant: {EventTenant}, UserKeyHash: {UserKeyHash}",
                adapter.ChannelType,
                ownerTenantId,
                LogMasking.Fingerprint(userKey));
            return;
        }

        ChannelStartCode? record;

        try
        {
            record = await startCodeStore.TakeAsync(code, ownerTenantId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The fault is named by its type alone and goes no further: what a storage says about
            // a code it failed to take may quote the code. Nothing is known about the code, so the
            // event is not answered as a wrong one, and the budget is not spent. The failure ends here,
            // so the span of the webhook is marked; on a polling loop there is no such span and nothing
            // is marked.
            if (Activity.Current is { OperationName: ChannelTelemetry.InboundActivityName } inbound)
            {
                inbound.SetStatus(ActivityStatusCode.Error);
            }

            logger.LogError(
                "The storage of start codes failed ({ExceptionType}) — the sign-in start by code stays unanswered. " +
                "ChannelType: {ChannelType}, UserKeyHash: {UserKeyHash}",
                ex.GetType().Name,
                adapter.ChannelType,
                LogMasking.Fingerprint(userKey));
            return;
        }

        if (record is null)
        {
            if (ownerTenantId is not null)
            {
                budget?.Charge(ownerTenantId, adapter.ChannelType);
            }

            await AnswerUnmatchedCodeAsync(
                result, adapter, replyTexts, promptLocalizer, logger, metrics, userKey, cancellationToken);
            return;
        }

        var start = new ChannelAuthStartResult
        {
            TransactionId = record.TransactionId,
            Identity = result.Identity,
            RawEvent = result.RawEvent
        };

        await RouteAuthStartAsync(
            processor, start, adapter, replyTexts, userAuthRateLimiter: null,
            confirmationPromptOrchestrator, promptLocalizer, ownerTenantId, snapshotTenant, cancellationToken);
    }

    /// <summary>
    /// Answers a start by a code that took no record — with the text the channel declares for such a code,
    /// one text for every reason. The event is counted as a start that was not served.
    /// </summary>
    /// <param name="result">The start, carrying the code as the user sent it.</param>
    /// <param name="adapter">Channel adapter the event arrived through.</param>
    /// <param name="replyTexts">Texts the channel states for itself.</param>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="logger">Logger of the pipeline.</param>
    /// <param name="metrics">Metrics of the channel adapters.</param>
    /// <param name="userKey">Key of the sender, logged as a fingerprint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task AnswerUnmatchedCodeAsync(
        ChannelAuthStartByCodeResult result,
        IChannelAdapter adapter,
        ChannelReplyTexts replyTexts,
        IConfirmationPromptLocalizer? promptLocalizer,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        string userKey,
        CancellationToken cancellationToken)
    {
        metrics.RecordUnservedStart(adapter.ChannelType);

        logger.LogWarning(
            "Sign-in start by a code that names no live transaction. " +
            "ChannelType: {ChannelType}, UserKeyHash: {UserKeyHash}",
            adapter.ChannelType,
            LogMasking.Fingerprint(userKey));

        // The attempt of this event is already taken by the caller, so the declared reply goes out
        // directly — in the language of the channel snapshot, as for an unknown identifier.
        await SendDeclaredReplyAsync(
            adapter, result.Identity, replyTexts.EffectiveUnmatchedCodeReplyText, result.Identity.Locale,
            promptLocalizer, logger, metrics, cancellationToken);
    }
}
