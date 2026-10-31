// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using OpenIddict.Abstractions;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Hubs;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Authorization callback endpoint (GET /connect/authorize/callback).
/// Invoked after authentication completes in the mobile channel.
/// Creates an intermediate Cookie and redirects to /connect/authorize
/// to issue the authorization code via OpenIddict.
/// <para>
/// When the effective confirmation surface is <see cref="ConfirmationSurface.OnWebPage"/>
/// (SPEC-012 §4.4, SPEC-007 UI-038), GET does NOT sign in automatically: it returns a page asking
/// the user to confirm, with both answers guarded by an AntiForgery token. Sign-in completes only on
/// a POST to <see cref="OidcEndpoints.AuthorizeCallbackConfirm"/> ("Yes"); a POST to
/// <see cref="OidcEndpoints.AuthorizeCallbackDecline"/> ("No") declines it instead.
/// </para>
/// <para>
/// The sign-in window itself posts here as well: its "Cancel sign-in" button ends a sign-in that is
/// still waiting (<see cref="OidcEndpoints.AuthorizeCallbackCancel"/>) and returns the same refusal to
/// the client as the "No" above.
/// </para>
/// <para>
/// A sign-in confirmed while claims are still missing (SPEC-041 L91) is not signed in by the callback at all:
/// the callback shows the page of the claims completion (<see cref="ClaimCompletionPageResponder"/>), whose
/// actions post under the same path (<see cref="ClaimCompletionActionEndpoints"/>). Every handler here takes
/// that branch before any decision about the confirmation surface and before any sign-in.
/// </para>
/// </summary>
public static class AuthorizeCallbackEndpoint
{
    /// <summary>
    /// Cache-Control of a response whose body carries the outcome of a sign-in attempt.
    /// </summary>
    private const string ResponseNoStore = "no-store";

    /// <summary>
    /// Pragma of that same response, for HTTP/1.0 caches that ignore Cache-Control.
    /// </summary>
    private const string ResponseNoCache = "no-cache";

    /// <summary>
    /// How the warning of <see cref="RecordRefusalAndDenyAsync"/> names the "No" of the web confirmation
    /// page when the write of that refusal is refused.
    /// </summary>
    private const string DeclinedRefusalLogName = "declined";

    /// <summary>
    /// How that same warning names "Cancel sign-in" of the sign-in window: the log has to tell the two
    /// callers apart.
    /// </summary>
    private const string CancelledRefusalLogName = "cancelled";

    /// <summary>
    /// Interval between the reads of a repeated "Yes" waiting for the parallel one that is finishing the
    /// sign-in (the transaction is <c>Confirmed</c> between its confirmation and its completion).
    /// </summary>
    private static readonly TimeSpan FinalizationPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long a repeated "Yes" waits for the parallel one to finish the sign-in before it answers
    /// "session expired". The finalization is a confirmation, an identity resolution and a completion —
    /// a few store writes, not a user step.
    /// </summary>
    private static readonly TimeSpan FinalizationWaitLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Registers the authorization callback endpoint (GET), the web-confirmation endpoints (POST), the
    /// cancel endpoint of the sign-in window (POST) and the
    /// action endpoints of the page of the claims completion, its email block among them (POST, SPEC-041
    /// C39, L108) — all under the callback path, so the browser nonce cookie reaches every one of them — and
    /// the page of the claims completion on the device that confirmed the sign-in (GET,
    /// <see cref="OidcEndpoints.ClaimCompletionFormPage"/>),
    /// whose right is the form token rather than the cookie. All of them share the same rate limit.
    /// </summary>
    /// <param name="endpoints">The endpoint router.</param>
    /// <param name="rateLimitPolicyName">The rate limiting policy name (null — no limiting).</param>
    /// <returns>The router for chaining.</returns>
    public static IEndpointRouteBuilder MapAuthorizeCallbackEndpoint(this IEndpointRouteBuilder endpoints, string? rateLimitPolicyName = null)
    {
        RouteHandlerBuilder[] builders =
        [
            endpoints.MapGet(OidcEndpoints.AuthorizeCallback, HandleCallbackAsync),
            endpoints.MapPost(OidcEndpoints.AuthorizeCallbackConfirm, HandleWebConfirmAsync),
            endpoints.MapPost(OidcEndpoints.AuthorizeCallbackDecline, HandleWebDeclineAsync),
            endpoints.MapPost(OidcEndpoints.AuthorizeCallbackCancel, HandleSignInCancelAsync),
            endpoints.MapPost(OidcEndpoints.AuthorizeCallbackClaims, ClaimCompletionActionEndpoints.HandleSubmitAsync),
            endpoints.MapPost(OidcEndpoints.AuthorizeCallbackClaimsCancel, ClaimCompletionActionEndpoints.HandleCancelAsync),
            endpoints.MapPost(OidcEndpoints.AuthorizeCallbackClaimsEmail, ClaimCompletionEmailActionEndpoint.HandleAsync),
            endpoints.MapGet(OidcEndpoints.ClaimCompletionFormPage, ClaimCompletionFormPageEndpoint.HandleAsync)
        ];

        if (!string.IsNullOrEmpty(rateLimitPolicyName))
        {
            foreach (var builder in builders)
            {
                builder.RequireRateLimiting(rateLimitPolicyName);
            }
        }

        return endpoints;
    }

    /// <summary>
    /// Handles the callback after authentication via the channel completes (GET).
    /// A sign-in that failed — refused by the user or ended by a fault — gets that ending to the client; in
    /// the claims completion it returns the page of the completion; on the OnWebPage surface it returns the
    /// confirmation page; otherwise it completes the sign-in.
    /// </summary>
    private static async Task<IResult> HandleCallbackAsync(
        HttpContext httpContext,
        ITransactionService transactionService,
        IEffectiveConfirmationSurfaceResolver surfaceResolver,
        IEnumerable<IChannelAdapter> channelAdapters,
        IAntiforgery antiforgery,
        IConfirmationPromptLocalizer promptLocalizer,
        IOptions<VeriqaOptions> veriqaOptions,
        CorePageBrandingResolver brandingResolver,
        IAuthPageLanguageRegistry languageRegistry,
        ClaimCompletionPageResponder claimCompletionPages,
        AuthPageSettingsResolver pageSettings,
        TimeProvider timeProvider)
    {
        // Detect the page language up front (Accept-Language + configured default), so both the
        // web-confirmation page and the friendly "session expired" page localize consistently (TASK-060).
        var language = DetectCallbackLanguage(httpContext, veriqaOptions, languageRegistry);

        // 1. Transaction validation (session_id from query, state, nonce, snapshots). A stale/expired
        //    transaction (not found / not completed) is surfaced as a friendly localized page, not a raw
        //    ProblemDetails (product-mode handling).
        var sessionIdStr = httpContext.Request.Query[OidcConstants.SessionIdParameterName].FirstOrDefault();
        var validation = await ValidateCallbackAsync(
            httpContext, sessionIdStr, transactionService, promptLocalizer, language,
            brandingResolver);
        if (validation.Error is not null)
        {
            return validation.Error;
        }

        var transaction = validation.Transaction!;
        var transactionId = validation.TransactionId!.Value;

        // 2. A sign-in that failed ends on the client's redirect_uri, as the "No" of the confirmation
        //    page does: the client is told that it ended and, by the error code, whether the request
        //    was refused or the server failed it (SPEC-002 §4.3, SPEC-041 L73, L109). The claims
        //    completion goes next (SPEC-041 L91): a transaction waiting for missing claims is never
        //    signed in here — the page of the completion is shown instead, whatever the step.
        if (IsFailedSignIn(transaction))
        {
            return await SignInErrorResultAsync(
                httpContext, transaction, SignInFailureOAuthError.Resolve(transaction.StateReasonCode),
                promptLocalizer, language, veriqaOptions, brandingResolver, languageRegistry);
        }

        if (IsInClaimsCompletion(transaction))
        {
            return await claimCompletionPages.RespondCarriedAsync(httpContext, transaction, language);
        }

        // 3. Effective confirmation surface (SPEC-012 §4.4.2): OnWebPage → page with a button,
        //    sign-in is deferred until an explicit POST confirmation (UI-038). None/InChannel — sign-in immediately
        //    (confirmation is either not required or already done in the channel).
        //    A transaction the core is already waiting on skips the resolution entirely: the surface
        //    decision was made when the channel event arrived, and re-deciding it here could only
        //    contradict it. The resolution decides for a Completed sign-in: a channel that completed it
        //    by its sign-in confirmation event (AuthConfirm) before the question, or a surface setting
        //    changed in the middle of the sign-in. Email does not get here completed on this surface —
        //    its letter only links the mailbox, and the sign-in waits for this answer.
        if (transaction.IsAwaitingWebConfirmation()
            || await ResolveSurfaceAsync(
                transaction, surfaceResolver, channelAdapters, httpContext.RequestAborted)
                is ConfirmationSurface.OnWebPage)
        {
            var tokens = antiforgery.GetAndStoreTokens(httpContext);

            // The transaction carries the language the sign-in page was rendered in, so the
            // confirmation page continues in that language rather than falling back to the browser's.
            var confirmPageLanguage = AuthPageLocalization.ResolveTransactionLanguage(
                transaction,
                language,
                veriqaOptions.Value.Localization.DefaultLanguage,
                languageRegistry.SupportedLanguages);

            // The confirmation page is branded by the levels of ITS OWN transaction — the application
            // and the ui_config record the sign-in window was rendered with (SPEC-007 UI-101) — and its
            // title and answers take the overrides of the same levels (SPEC-012 CFG-254).
            var confirmPageResolution = TransactionResolutionContext.For(transaction);
            var confirmPageBranding = await brandingResolver.ResolveAsync(
                confirmPageResolution, httpContext.RequestAborted);
            var confirmPageTexts = await httpContext.RequestServices.GetRequiredService<CorePageTextResolver>()
                .ResolveAsync(confirmPageResolution, httpContext.RequestAborted);

            // The question is the confirmation prompt the chat would carry, asked at the surface of the
            // core page (SPEC-007 UI-123); a sign-in is always worded, at worst with the default text.
            var question = await WebConfirmPage.ResolveQuestionAsync(
                httpContext, transaction, promptLocalizer, confirmPageLanguage)
                ?? throw new InvalidOperationException("The question of a sign-in is always worded.");

            // Both answers end on the client's redirect_uri — "Yes" through /connect/authorize, "No" straight
            // away in the query and fragment modes — and the browser applies form-action to the whole
            // chain of redirects after a form post. So this page is the second one whose response allows
            // the validated redirect_uri as a form target (SPEC-007 UI-091); every other response of the
            // callback keeps form-action 'self'.
            var formActionSource = SecurityHeadersMiddleware.GetFormActionSource(transaction.OidcContext?.RedirectUri);
            if (!string.IsNullOrEmpty(formActionSource))
            {
                httpContext.Items[SecurityHeaderConstants.FormActionTargetItemKey] = formActionSource;
            }

            var pathBase = httpContext.Request.PathBase.Value ?? string.Empty;

            // The page watches its status as the page of the claims completion does (SPEC-007 UI-123). The
            // callback shows it in two states: Pending, the core waiting for this answer — the deadline ends
            // it, and so may a newer sign-in of the same user, so the page also counts it down; and Completed,
            // a channel having completed the sign-in before the question by its sign-in confirmation event
            // (AuthConfirm — the HTTP channel sends one) or the surface setting having changed in the middle
            // of the sign-in — terminal, its deadline ending nothing, so the page counts nothing down and
            // leaves once the status reports the code that "Yes" issues. A Completed sign-in whose code is
            // issued never gets here: the validation above answers it with the "session expired" page.
            var watch = await WebConfirmPage.ResolveWatchAsync(
                httpContext, transaction, pageSettings, pathBase, timeProvider);

            var sessionId = SessionIdMapper.ToSessionId(transactionId);
            var page = WebConfirmPage.Build(
                WebConfirmPage.ForSignIn(sessionId, question),
                sessionId: sessionId,
                antiforgeryFormFieldName: tokens.FormFieldName,
                antiforgeryToken: tokens.RequestToken,
                pathBase: pathBase,
                cspNonce: httpContext.Items[SecurityHeaderConstants.CspNonceItemKey] as string,
                watch: watch,
                localizer: promptLocalizer,
                language: confirmPageLanguage,
                texts: confirmPageTexts,
                branding: confirmPageBranding);
            return Results.Content(page, "text/html", Encoding.UTF8);
        }

        // 4. None/InChannel — complete the sign-in immediately
        return await CompleteSignInAsync(httpContext, transaction, transactionId);
    }

    /// <summary>
    /// Handles the web sign-in confirmation (POST, OnWebPage mode). Validates the AntiForgery token,
    /// re-validates the transaction and completes the sign-in (SPEC-007 UI-038). A "Yes" that starts the
    /// claims completion, and any answer to a transaction already in it, is sent to the callback, where the
    /// page of the completion takes over (SPEC-041 L91). A repeated "Yes" of the same page — a double
    /// click — ends where the winning one does, including while the winner is still finishing the sign-in.
    /// </summary>
    private static async Task<IResult> HandleWebConfirmAsync(
        HttpContext httpContext,
        ITransactionService transactionService,
        IIdentityResolutionService identityResolutionService,
        IAntiforgery antiforgery,
        IConfirmationPromptLocalizer promptLocalizer,
        IOptions<VeriqaOptions> veriqaOptions,
        CorePageBrandingResolver brandingResolver,
        IAuthPageLanguageRegistry languageRegistry,
        TimeProvider timeProvider)
    {
        // 1. AntiForgery token validation (UI-038): missing/invalid → 400, sign-in is not performed
        var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        // Detect the page language for a possible friendly "session expired" page (consistent with GET).
        var language = DetectCallbackLanguage(httpContext, veriqaOptions, languageRegistry);

        // 2. Transaction re-validation (session_id from the form, state, nonce, snapshots) — as in GET.
        //    A stale transaction reaped between showing the question and this POST (e.g. the user waited past
        //    its retention window) is surfaced as a friendly page instead of a raw "Transaction not found".
        //    A transaction being finalized by a parallel "Yes" is admitted here as well — the browser nonce
        //    check still applies to it.
        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        var sessionIdStr = form[OidcConstants.SessionIdParameterName].FirstOrDefault();
        var validation = await ValidateCallbackAsync(
            httpContext, sessionIdStr, transactionService, promptLocalizer, language,
            brandingResolver, admitBeingFinalized: true);
        if (validation.Error is not null)
        {
            return validation.Error;
        }

        var transaction = validation.Transaction!;
        var transactionId = validation.TransactionId!.Value;

        // 3. The claims completion goes first (SPEC-041 L91): the answer of this page is no longer taken — a
        //    repeated "Yes" changes nothing and signs nobody in. The browser is sent to the callback, which
        //    shows the page of the completion (or returns the ending of a failed sign-in to the client).
        if (IsInClaimsCompletion(transaction) || IsFailedSignIn(transaction))
        {
            return SeeOtherToCallback(httpContext, transactionId);
        }

        // 3a. A parallel "Yes" confirmed the transaction and is completing it: this answer is a repeated one,
        //     and it follows the winner once the transaction leaves that transient state.
        if (IsBeingFinalized(transaction))
        {
            var repeatExpiredPage = await ResolveExpiredPageSetupAsync(
                httpContext, transaction, brandingResolver, httpContext.RequestAborted);

            return await FollowSettledTransactionAsync(
                httpContext, transaction, transactionId, transactionService, timeProvider,
                promptLocalizer, language, repeatExpiredPage);
        }

        // 4. A transaction the core was waiting on is only now finalized: the channel data has been
        //    sitting on it since the user acted in the channel, and "Yes" is what authorizes turning
        //    it into a sign-in. The sequence is the webhook path's own (confirm → resolve identity →
        //    complete, compensating a refused resolution and a failed completion alike) — one
        //    implementation, two callers.
        if (transaction.IsAwaitingWebConfirmation())
        {
            var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AuthorizeCallbackEndpoint));

            // CancellationToken.None — as the compensation inside does: a double click makes the browser
            // abort the first request, and a finalization cancelled between its confirmation and its
            // completion would bypass that compensation and leave the transaction Confirmed for good.
            var finalizeResult = await ChannelTransactionFinalizer.ConfirmAndCompleteAsync(
                transaction.ChannelIdentitySnapshot!,
                transactionId,
                transactionService,
                identityResolutionService,
                transaction.ConcurrencyToken,
                logger,
                CancellationToken.None);

            // A parallel "Yes" set the claims completion mark first: this answer is a repeated one, and it
            // is sent where the repeated answers of the completion go — to the callback (SPEC-041 L91).
            if (finalizeResult.IsFailure
                && string.Equals(
                    finalizeResult.Error.Code, TransactionErrorCodes.ClaimCompletionInProgress, StringComparison.Ordinal))
            {
                return SeeOtherToCallback(httpContext, transactionId);
            }

            if (finalizeResult.IsFailure)
            {
                // The finalizer compensates only what it moved: a refusal at the confirmation step
                // returns before any compensation. Most such refusals leave the transaction still
                // waiting for an answer on this page; those that changed it are listed in the remarks of
                // ITransactionService.ConfirmTransactionAsync — a refusal by the deadline has already
                // moved it into Expired and published the expiry event, and a required claim nothing can
                // obtain has already failed it — so there is nothing left to close for them. So the
                // response is chosen by the error
                // instead of assumed, and the waiting state is never left open behind it.
                // The page this may end in belongs to a transaction that WAS found and validated, so
                // it is branded by that transaction's own levels (SPEC-007 UI-101) and offers the entry
                // point of that transaction's application (UI-109). Both are resolved here, while the
                // transaction is in hand: the branches below may reread it as gone. The resolution
                // stands between the finalization and its compensation, so it does not follow the
                // cancellation of the request: an aborted request must not leave the transaction open.
                var failureExpiredPage = await ResolveExpiredPageSetupAsync(
                    httpContext, transaction, brandingResolver, CancellationToken.None);

                return await HandleFailedWebFinalizationAsync(
                    httpContext,
                    finalizeResult.Error.Code,
                    transaction.ConcurrencyToken,
                    transactionId,
                    transactionService,
                    timeProvider,
                    promptLocalizer,
                    language,
                    failureExpiredPage,
                    logger);
            }

            // "Yes" set the claims completion mark instead of confirming (SPEC-041 L91): the sign-in
            // is not finalized yet — it waits for the missing claims — so there is nothing to sign in
            // with. The browser stays on a page of the core that follows the step signal: it is sent to
            // the callback (303, SPEC-007 UI-038), which shows the page of the completion. A repeated
            // "Yes" or "No" meets the completion branch above and is sent there as well.
            if (finalizeResult.Value.IsCompletingClaims())
            {
                logger.LogInformation(
                    "Web confirmation accepted; the sign-in waits for missing claims. TransactionId: {TransactionId}",
                    transactionId.ToString());

                return SeeOtherToCallback(httpContext, transactionId);
            }

            // The finalized instance is the one carrying ResolvedIdentitySnapshot and
            // CompletionSnapshot; the pre-finalization one validated above carries neither.
            transaction = finalizeResult.Value;

            // The chat the sign-in was entered from was sent to this page for the answer (SPEC-012
            // CFG-266), so it is shown the receipt of the "Yes" — before the post-completion action,
            // which follows the receipt (SPEC-012 §4.9). The transaction awaited this page before the
            // write, which is what the reporter is called on. A host that composes no channel contour
            // has no reporter, and there is no chat to tell. CancellationToken.None, for the reason
            // the dispatch below states.
            if (httpContext.RequestServices.GetService<IWebConfirmationReceiptReporter>() is { } receiptReporter)
            {
                await receiptReporter.ReportAsync(finalizeResult.Value, CancellationToken.None);
            }

            // "Yes" on this page completed the sign-in, so its post-completion action is performed
            // here, in the channel the user came through (SPEC-012 §4.9). A host that composes no
            // channel contour has no performer, and there is nothing to perform. CancellationToken.None —
            // a browser that goes away must not cancel a message of a sign-in that is already completed.
            if (httpContext.RequestServices.GetService<ICompletionBehaviorDispatcher>() is { } completionBehavior)
            {
                await completionBehavior.DispatchAsync(finalizeResult.Value, CancellationToken.None);
            }
        }

        // 5. Complete the sign-in
        return await CompleteSignInAsync(httpContext, transaction, transactionId);
    }

    /// <summary>
    /// Answers a failed finalization of a transaction the core was waiting on, and makes sure that
    /// transaction does not keep offering the same question afterwards.
    /// </summary>
    /// <remarks>
    /// Three classes of failure arrive here. A race lost to a parallel answer — the concurrency token
    /// moved, or the transaction left the waiting state — is not a stale session: the transaction is read
    /// again and the browser follows what the winner made of it (a race lost to a parallel "Yes" that set
    /// the claims completion mark does not come here: the caller sends it to the page of the completion).
    /// A transaction that expired or was reaped is a stale session from the user's side: they get the same
    /// friendly page the validation shows, with a way to start again, and the transaction is left alone.
    /// A required claim that cannot be obtained is an ending the engine has already written — the
    /// transaction is failed with that reason (SPEC-041 L48) — so the browser is sent to the callback,
    /// which returns the ending of a failed sign-in to the client (SPEC-002 §4.3). Anything else is a downstream failure of ours: the user gets the opaque 500
    /// the claims-mapping failure returns, and the transaction is closed so an unexplained refusal
    /// cannot leave it waiting and hand the user that 500 on every further attempt. Closing it is best
    /// effort — if the token has already moved, whoever moved it decides the outcome.
    /// </remarks>
    private static async Task<IResult> HandleFailedWebFinalizationAsync(
        HttpContext httpContext,
        string errorCode,
        string concurrencyToken,
        TransactionId transactionId,
        ITransactionService transactionService,
        TimeProvider timeProvider,
        IConfirmationPromptLocalizer promptLocalizer,
        string language,
        CallbackExpiredPageSetup expiredPage,
        ILogger logger)
    {
        // These four belong to the transaction lifecycle, and IIdentityResolutionService reserves them
        // for it: a resolver refusal travels out with its own code, so the classification below stays
        // a classification of lifecycle outcomes and not of arbitrary extension-point strings.
        // transactionId.ToString() — a Base62 string, log injection is excluded
        if (errorCode is TransactionErrorCodes.InvalidStateTransition
            or TransactionErrorCodes.ConcurrencyConflict)
        {
            logger.LogInformation(
                "Web confirmation lost a race to a parallel answer; following its outcome. " +
                "TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transactionId.ToString(),
                errorCode);

            var reread = await transactionService.GetTransactionAsync(transactionId, httpContext.RequestAborted);

            return await FollowSettledTransactionAsync(
                httpContext, reread.IsSuccess ? reread.Value : null, transactionId, transactionService,
                timeProvider, promptLocalizer, language, expiredPage);
        }

        if (errorCode is TransactionErrorCodes.TransactionNotFound
            or TransactionErrorCodes.TransactionExpired)
        {
            logger.LogWarning(
                "Web confirmation was not finalized — the session is no longer answerable here. " +
                "TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transactionId.ToString(),
                errorCode);

            return ExpiredSessionResult(httpContext, promptLocalizer, language, expiredPage);
        }

        // The engine failed the transaction itself on this code — a required claim the channel of the
        // transaction cannot ask for (SPEC-041 L48) — so it is an outcome, not a fault of ours: nothing
        // is left to close, and the answer is the one the callback gives a failed sign-in.
        if (errorCode is TransactionErrorCodes.RequiredClaimUnobtainable)
        {
            logger.LogWarning(
                "Web confirmation was not finalized — a required claim cannot be obtained, the transaction is failed. " +
                "TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transactionId.ToString(),
                errorCode);

            return SeeOtherToCallback(httpContext, transactionId);
        }

        logger.LogError(
            "Failed to finalize the web confirmation. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
            transactionId.ToString(),
            errorCode);

        // CancellationToken.None — the transaction must be closed even if the client goes away.
        var failResult = await transactionService.FailTransactionAsync(
            transactionId,
            TransactionErrorCodes.DownstreamFinalizeFailed,
            concurrencyToken,
            CancellationToken.None);

        // An invalid transition here means the transaction is already terminal — the compensation
        // inside the finalizer got there first, which is exactly the outcome wanted.
        if (failResult.IsFailure
            && !string.Equals(
                failResult.Error.Code, TransactionErrorCodes.InvalidStateTransition, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Failed to close the transaction after an unsuccessful web finalization. " +
                "TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transactionId.ToString(),
                failResult.Error.Code);
        }

        return Results.Problem(
            detail: "Failed to complete authentication. Try again later.",
            statusCode: StatusCodes.Status500InternalServerError,
            title: OidcErrorCodes.ClaimsMappingFailed);
    }

    /// <summary>
    /// Answers a repeated "Yes" by the transaction as the winning answer leaves it: waits while the winner
    /// is finishing the sign-in, then sends the browser where the winner's own answer went.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>Completed</c>, its authorization code not issued yet — the sign-in is completed
    /// by THIS response. A <c>303</c> to the callback would not do: the winner's response has already
    /// deleted the browser nonce cookie, and the callback would refuse the browser that started the
    /// sign-in.</description></item>
    /// <item><description>The claims completion, a failed sign-in, or a transaction still waiting for an
    /// answer — <c>303</c> to the callback, which shows what the transaction warrants (SPEC-041 L91),
    /// returns the ending of the failed one to the client, or shows a superseded one
    /// (<see cref="IsSupersededSignIn"/>) the "session expired" page.</description></item>
    /// <item><description>No transaction, <c>Expired</c>, still being finalized past
    /// <see cref="FinalizationWaitLimit"/>, or <c>Completed</c> with its authorization code already issued
    /// (<see cref="HasIssuedAuthorizationCode"/>) — "session expired". This is the one place of the
    /// callback path that reads the transaction again after its validation, so the mark is checked on
    /// the copy read here.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">The transaction as last read; null — it is gone.</param>
    /// <param name="transactionId">The transaction identifier.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="timeProvider">Time source of the wait.</param>
    /// <param name="promptLocalizer">Locale-file localizer.</param>
    /// <param name="language">Detected page language.</param>
    /// <param name="expiredPage">Branding and action address of a possible "session expired" page,
    /// resolved by the caller over the context of the transaction.</param>
    /// <returns>The response to the repeated answer.</returns>
    private static async Task<IResult> FollowSettledTransactionAsync(
        HttpContext httpContext,
        Transaction? transaction,
        TransactionId transactionId,
        ITransactionService transactionService,
        TimeProvider timeProvider,
        IConfirmationPromptLocalizer promptLocalizer,
        string language,
        CallbackExpiredPageSetup expiredPage)
    {
        var settled = await WaitWhileBeingFinalizedAsync(
            transaction, transactionId, transactionService, timeProvider, httpContext.RequestAborted);

        if (settled is null || IsBeingFinalized(settled))
        {
            return ExpiredSessionResult(httpContext, promptLocalizer, language, expiredPage);
        }

        if (IsInClaimsCompletion(settled)
            || IsFailedSignIn(settled)
            || settled.State is TransactionState.Pending)
        {
            return SeeOtherToCallback(httpContext, transactionId);
        }

        if (settled.State is TransactionState.Completed && !HasIssuedAuthorizationCode(settled))
        {
            return await CompleteSignInAsync(httpContext, settled, transactionId);
        }

        return ExpiredSessionResult(httpContext, promptLocalizer, language, expiredPage);
    }

    /// <summary>
    /// Reads the transaction again, at <see cref="FinalizationPollInterval"/>, while a parallel answer is
    /// finishing its sign-in — until it leaves that transient state, disappears, or
    /// <see cref="FinalizationWaitLimit"/> runs out.
    /// </summary>
    /// <param name="transaction">The transaction as last read; null — it is gone.</param>
    /// <param name="transactionId">The transaction identifier.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="timeProvider">Time source of the wait.</param>
    /// <param name="cancellationToken">The request's cancellation — a browser that left needs no answer.</param>
    /// <returns>The transaction as last read; null — it is gone.</returns>
    private static async Task<Transaction?> WaitWhileBeingFinalizedAsync(
        Transaction? transaction,
        TransactionId transactionId,
        ITransactionService transactionService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + FinalizationWaitLimit;

        while (transaction is not null && IsBeingFinalized(transaction) && timeProvider.GetUtcNow() < deadline)
        {
            await Task.Delay(FinalizationPollInterval, timeProvider, cancellationToken);

            var reread = await transactionService.GetTransactionAsync(transactionId, cancellationToken);
            transaction = reread.IsSuccess ? reread.Value : null;
        }

        return transaction;
    }

    /// <summary>
    /// Whether an answer is finishing the sign-in of the transaction right now: it is <c>Confirmed</c>
    /// outside the claims completion — the step between the confirmation and the completion of
    /// <see cref="ChannelTransactionFinalizer.ConfirmAndCompleteAsync"/>, not a state it rests in.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <returns>true while the finalization is in progress.</returns>
    private static bool IsBeingFinalized(Transaction transaction) =>
        transaction.State is TransactionState.Confirmed && transaction.ClaimCompletion is null;

    /// <summary>
    /// Handles the "No" answer on the web confirmation page (POST, OnWebPage mode). Validates the
    /// AntiForgery token, re-validates the transaction, records the refusal and returns the standard
    /// OAuth 2.0 <c>access_denied</c> error to the client. An answer to a transaction in the claims
    /// completion is not taken: it is sent to the callback, like a repeated "Yes" (SPEC-041 L91).
    /// </summary>
    private static async Task<IResult> HandleWebDeclineAsync(
        HttpContext httpContext,
        ITransactionService transactionService,
        IAntiforgery antiforgery,
        IConfirmationPromptLocalizer promptLocalizer,
        IOptions<VeriqaOptions> veriqaOptions,
        CorePageBrandingResolver brandingResolver,
        IAuthPageLanguageRegistry languageRegistry)
    {
        // 1. AntiForgery (CFG-045): declining changes state too, so it is guarded exactly like the confirm
        var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var language = DetectCallbackLanguage(httpContext, veriqaOptions, languageRegistry);

        // 2. Transaction re-validation — the same path as the confirm POST
        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        var sessionIdStr = form[OidcConstants.SessionIdParameterName].FirstOrDefault();
        var validation = await ValidateCallbackAsync(
            httpContext, sessionIdStr, transactionService, promptLocalizer, language,
            brandingResolver);
        if (validation.Error is not null)
        {
            return validation.Error;
        }

        var transaction = validation.Transaction!;

        // 3. The claims completion goes first (SPEC-041 L91): the answer of this page is no longer taken, and
        //    the transaction is left as it is. A sign-in that has already failed is left as well: the
        //    callback returns its ending to the client.
        if (IsInClaimsCompletion(transaction) || IsFailedSignIn(transaction))
        {
            return SeeOtherToCallback(httpContext, validation.TransactionId!.Value);
        }

        // 4. Record the refusal and return it to the client — one answer for this "No", for "Cancel sign-in"
        //    of the sign-in window and for "Cancel sign-in" of the page of the claims completion. Whether
        //    the transaction awaited this page is read now, before the write: a store may hand out the
        //    very instance it keeps, and the refusal would then change what this one reads.
        return await RecordRefusalAndDenyAsync(
            httpContext, transactionService, transaction, validation.TransactionId!.Value,
            DeclinedRefusalLogName, answeredOnWebConfirmationPage: transaction.IsAwaitingWebConfirmation(),
            promptLocalizer, language, veriqaOptions, brandingResolver, languageRegistry);
    }

    /// <summary>
    /// Handles "Cancel sign-in" of the sign-in window (POST): validates the AntiForgery token, checks that
    /// the transaction is still open and that the request comes from the browser that started the sign-in,
    /// records the refusal and returns the standard OAuth 2.0 <c>access_denied</c> error to the client.
    /// </summary>
    /// <remarks>
    /// The state check is this handler's own. <see cref="ValidateCallbackAsync"/> admits a transaction
    /// only once the channel has acted on it, and the window cancels one that is still waiting for that —
    /// so every state but the two closed ones is taken here, the claims completion included (it is the
    /// user's way out of a sign-in that still asks for data), except the moment a "yes" of the channel is
    /// being finalized (<see cref="IsBeingFinalized"/>): that answer stands, as on a confirmation (SPEC-007
    /// UI-122), and the browser that started the sign-in follows it like a repeated "Yes" does. A transaction that ran out of time has
    /// nothing left to cancel and gets the "session expired" page, and so does a sign-in that has already
    /// issued its authorization code (<see cref="HasIssuedAuthorizationCode"/>) — before the browser
    /// check, so nothing is recorded for it. One that was already failed — refused
    /// in the channel or ended by a fault a moment before the button was pressed — has nothing left to
    /// cancel either, but its ending is still the client's to learn: the browser is sent to the callback,
    /// which checks it and carries that ending to the client, as it does for every failed sign-in but a
    /// superseded one (<see cref="IsSupersededSignIn"/>), which it answers with the "session expired"
    /// page. Who
    /// may cancel is decided by the check the callback makes (<see cref="ValidateBrowserAsync"/>): a
    /// caller who only knows the session identifier ends nobody's sign-in (SPEC-002 §10.7).
    /// <para>
    /// A sign-in the channel confirmed after this request read it is not undone: the write of the refusal is
    /// refused, which is logged, and the client still receives <c>access_denied</c> — the user's answer
    /// stands, exactly as in <see cref="HandleWebDeclineAsync"/>. No authorization code is issued on that
    /// path, because the callback that would issue it was never requested.
    /// </para>
    /// </remarks>
    private static async Task<IResult> HandleSignInCancelAsync(
        HttpContext httpContext,
        ITransactionService transactionService,
        IAntiforgery antiforgery,
        IConfirmationPromptLocalizer promptLocalizer,
        IOptions<VeriqaOptions> veriqaOptions,
        CorePageBrandingResolver brandingResolver,
        IAuthPageLanguageRegistry languageRegistry,
        TimeProvider timeProvider)
    {
        // 1. AntiForgery (CFG-045): cancelling changes state, so it is guarded like the two answers of
        //    the web confirmation page
        var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var language = DetectCallbackLanguage(httpContext, veriqaOptions, languageRegistry);

        // 2. The transaction the form names — the same answers as the callback gives to a missing or
        //    malformed session_id and to a transaction that is gone
        var form = await ClaimCompletionActionEndpoints.ReadFormAsync(httpContext);
        var found = await FindTransactionAsync(
            httpContext,
            form[OidcConstants.SessionIdParameterName].FirstOrDefault(),
            transactionService,
            promptLocalizer,
            language,
            brandingResolver);
        if (found.Error is not null)
        {
            return found.Error;
        }

        var transaction = found.Transaction!;
        var transactionId = found.TransactionId!.Value;

        // 3. A closed transaction has nothing left to cancel, and this request ends nothing. One that
        //    ran out of time gets the "session expired" page for a sign-in, the neutral page for a
        //    transaction that is not one. One that was failed — by a refusal or by a fault — is handed
        //    to the callback: it makes the browser check of step 4 itself and returns the ending the
        //    transaction keeps to the client (access_denied or server_error), the "session expired"
        //    page to a browser that did not start the sign-in, and the neutral page for a transaction
        //    that is no sign-in. The callback never leads back here, so the chain ends there. A sign-in
        //    whose authorization code is already issued is over as well: the window that posts this is a
        //    stale one, and it gets the "session expired" page — above the browser check, whose nonce
        //    cookie the completed sign-in has already removed.
        if (transaction.State is TransactionState.Expired || HasIssuedAuthorizationCode(transaction))
        {
            return await ExpiredSessionResultAsync(
                httpContext, transaction, promptLocalizer, language, brandingResolver);
        }

        if (IsFailedSignIn(transaction))
        {
            return SeeOtherToCallback(httpContext, transactionId);
        }

        // 4. The browser that started the sign-in, and no other (SPEC-002 §10.7). A transaction created
        //    server-to-server has no client to return the refusal to and is left as it is.
        var browserRefusal = await ValidateBrowserAsync(
            httpContext,
            transaction,
            transactionId,
            promptLocalizer,
            language,
            brandingResolver,
            mismatchIsStale: false);
        if (browserRefusal is not null)
        {
            return browserRefusal;
        }

        // 4a. The channel has said "yes" and the sign-in is being finalized: the cancel does not overturn that
        //     answer (SPEC-007 UI-122). The browser follows the finalized sign-in, as a repeated "Yes" does —
        //     the browser check above is what lets it receive the authorization code.
        if (IsBeingFinalized(transaction))
        {
            var finalizingExpiredPage = await ResolveExpiredPageSetupAsync(
                httpContext, transaction, brandingResolver, httpContext.RequestAborted);

            return await FollowSettledTransactionAsync(
                httpContext, transaction, transactionId, transactionService, timeProvider,
                promptLocalizer, language, finalizingExpiredPage);
        }

        // 5. Record the refusal and return it to the client — the one answer of every refusal the browser
        //    takes back
        return await RecordRefusalAndDenyAsync(
            httpContext, transactionService, transaction, transactionId,
            CancelledRefusalLogName, answeredOnWebConfirmationPage: false,
            promptLocalizer, language, veriqaOptions, brandingResolver, languageRegistry);
    }

    /// <summary>
    /// The tail of the two refusals this endpoint records itself — "No" on the web confirmation page and
    /// "Cancel sign-in" of the sign-in window: moves the transaction to <c>Failed</c> and returns the
    /// standard OAuth 2.0 <c>access_denied</c> error to the client (<see cref="SignInErrorResultAsync"/>).
    /// </summary>
    /// <remarks>
    /// The refusal is recorded under the reason code the in-channel decline uses
    /// (<c>ChannelWebhookPipeline.HandleAuthDeclineAsync</c>): one meaning, one code, one audit trail. The
    /// transaction is left in its terminal Failed state (not deleted), so a status consumer still polling
    /// learns the outcome; retention reaps it afterwards. A refusal that ended a sign-in in the claims
    /// completion is also shown in the channel the user came through (SPEC-003 CA-142), and so is a
    /// cancel of a sign-in whose question still stood before the user in the chat
    /// (<see cref="ISignInCancelChannelReporter"/>) and the "No" of a sign-in whose chat was sent to the
    /// web confirmation page for the answer (<see cref="IWebConfirmationReceiptReporter"/>, SPEC-012
    /// CFG-266).
    /// <para>
    /// A write that is refused — the transaction moved under the request, most likely because the channel
    /// answered at the same moment — is logged and does not change the answer: the user's refusal stands,
    /// nobody is signed in by this request either way, and the client is told so regardless.
    /// </para>
    /// </remarks>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="transaction">The transaction the user refused, as the caller validated it.</param>
    /// <param name="transactionId">Identifier of that transaction.</param>
    /// <param name="refusalLogName">How the log names the refusal: <see cref="DeclinedRefusalLogName"/> or
    /// <see cref="CancelledRefusalLogName"/>.</param>
    /// <param name="answeredOnWebConfirmationPage">true — the refusal is the "No" of the web confirmation
    /// page, given while the transaction awaited that page (read by the caller before the write); false —
    /// "Cancel sign-in" of the sign-in window, or a "No" to a transaction in another state.</param>
    /// <param name="promptLocalizer">Locale-file localizer.</param>
    /// <param name="language">Language detected from the request headers.</param>
    /// <param name="veriqaOptions">Veriqa options (the configured default language).</param>
    /// <param name="brandingResolver">Resolver of the service-page branding.</param>
    /// <param name="languageRegistry">Registry of supported page languages.</param>
    /// <returns>The refusal: a redirect, or the auto-posting page of <c>form_post</c>.</returns>
    private static async Task<IResult> RecordRefusalAndDenyAsync(
        HttpContext httpContext,
        ITransactionService transactionService,
        Transaction transaction,
        TransactionId transactionId,
        string refusalLogName,
        bool answeredOnWebConfirmationPage,
        IConfirmationPromptLocalizer promptLocalizer,
        string language,
        IOptions<VeriqaOptions> veriqaOptions,
        CorePageBrandingResolver brandingResolver,
        IAuthPageLanguageRegistry languageRegistry)
    {
        // CancellationToken.None — the browser leaves the page on the answer, and the refusal must be
        // recorded even if the request is aborted on the way.
        var failResult = await transactionService.FailTransactionAsync(
            transactionId,
            TransactionErrorCodes.DeclinedByUser,
            transaction.ConcurrencyToken,
            CancellationToken.None);

        if (failResult.IsFailure)
        {
            // transactionId.ToString() is Base62 — no log injection; the name of the refusal is a constant.
            httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(AuthorizeCallbackEndpoint))
                .LogWarning(
                    "Failed to move the {Refusal} transaction to Failed. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                    refusalLogName,
                    transactionId.ToString(),
                    failResult.Error.Code);
        }
        else
        {
            // "Cancel sign-in" of the sign-in window also ends a sign-in whose claims completion goes
            // on elsewhere — on the device that confirmed it, or in the chat: the channel is shown the
            // refusal then (SPEC-003 CA-142). A sign-in refused before its confirmation carries no
            // completion mark and is not that reporter's.
            await ClaimCompletionActionEndpoints.ReportOutcomeToChannelAsync(httpContext, failResult.Value);

            // A sign-in cancelled while its question still stood before the user in the chat is shown
            // the refusal there, decided on the transaction as read before this write. The "No" of the
            // web confirmation page never qualifies — its question was handed to this page — and is
            // shown by the reporter below instead. CancellationToken.None, for the reason the write
            // above states. A host that composes no channel contour has no reporter, and there is no
            // chat to tell.
            if (httpContext.RequestServices.GetService<ISignInCancelChannelReporter>() is { } cancelReporter)
            {
                await cancelReporter.ReportAsync(transaction, failResult.Value, CancellationToken.None);
            }

            // The "No" of the web confirmation page is shown in the chat that was sent to this page for
            // the answer (SPEC-012 CFG-266). "Cancel sign-in" of the sign-in window is not that answer.
            if (answeredOnWebConfirmationPage
                && httpContext.RequestServices.GetService<IWebConfirmationReceiptReporter>() is { } receiptReporter)
            {
                await receiptReporter.ReportAsync(failResult.Value, CancellationToken.None);
            }
        }

        // The answer is stated, not read off the transaction: the user's refusal stands even when its
        // write was refused and the transaction carries no reason of it.
        return await SignInErrorResultAsync(
            httpContext, transaction, OpenIddictConstants.Errors.AccessDenied,
            promptLocalizer, language, veriqaOptions, brandingResolver, languageRegistry);
    }

    /// <summary>
    /// Ends a sign-in that did not happen in the browser: deletes the browser nonce cookie and returns a
    /// standard OAuth 2.0 error to the client in the response mode the request asked for (RFC 6749
    /// §4.1.2.1). The one answer of every such ending the browser takes back to the client — "No" on the
    /// web confirmation page, "Cancel sign-in" of the sign-in window and of the page of the claims
    /// completion, and a sign-in the callback finds failed, whether refused in the channel or ended by a
    /// fault (SPEC-002 §4.3, SPEC-041 L109).
    /// </summary>
    /// <remarks>
    /// The response carries the error code, the original <c>state</c> and the issuer identifier
    /// (<c>iss</c>, RFC 9207), and nothing else: no <c>error_description</c>, so the reason the
    /// transaction keeps is not disclosed to the client.
    /// </remarks>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">The transaction of the sign-in that ended.</param>
    /// <param name="error">The OAuth 2.0 error code the client receives: stated by the caller for a refusal
    /// it has just taken, resolved by <see cref="SignInFailureOAuthError.Resolve"/> for a failed transaction.</param>
    /// <param name="promptLocalizer">Locale-file localizer.</param>
    /// <param name="language">Language detected from the request headers.</param>
    /// <param name="veriqaOptions">Veriqa options (the configured default language).</param>
    /// <param name="brandingResolver">Resolver of the service-page branding.</param>
    /// <param name="languageRegistry">Registry of supported page languages.</param>
    /// <returns>The error: a redirect, or the auto-posting page of <c>form_post</c>.</returns>
    internal static async Task<IResult> SignInErrorResultAsync(
        HttpContext httpContext,
        Transaction transaction,
        string error,
        IConfirmationPromptLocalizer promptLocalizer,
        string language,
        IOptions<VeriqaOptions> veriqaOptions,
        CorePageBrandingResolver brandingResolver,
        IAuthPageLanguageRegistry languageRegistry)
    {
        // 1. Delete the browser nonce cookie (Path matches the one set in AuthorizeEndpoint):
        //    this sign-in attempt is over regardless of the answer.
        httpContext.Response.Cookies.Delete(OidcConstants.BrowserNonceCookieName, new CookieOptions
        {
            Path = $"{httpContext.Request.PathBase}{OidcEndpoints.AuthorizeCallback}"
        });

        // 2. Return the error to the client, in the response mode the request asked for. This path
        //    assembles the response by hand — it never reaches OpenIddict — so the mode has to be
        //    honoured here as well: a client that reads ONLY a POST would otherwise not see the
        //    error at all, and could not tell a declined sign-in from a failed one.
        var oidcContext = transaction.OidcContext!;
        if (string.IsNullOrEmpty(oidcContext.RedirectUri))
        {
            // Defensive: an accepted authorize request always carries a redirect_uri, so there is
            // nowhere to send the user only if the stored context is corrupt.
            return Results.Problem(
                detail: "Sign-in did not complete, and the client redirect URI is unavailable.",
                statusCode: StatusCodes.Status400BadRequest,
                title: OidcErrorCodes.OidcRequestInvalid);
        }

        if (string.Equals(
                oidcContext.ResponseMode,
                OpenIddictConstants.ResponseModes.FormPost,
                StringComparison.Ordinal))
        {
            return await SignInErrorFormPostResultAsync(
                httpContext, transaction, oidcContext, error, promptLocalizer, language, veriqaOptions,
                brandingResolver, languageRegistry);
        }

        var useFragment = string.Equals(
            oidcContext.ResponseMode,
            OpenIddictConstants.ResponseModes.Fragment,
            StringComparison.Ordinal);

        return Results.Redirect(BuildSignInErrorRedirectUrl(
            oidcContext, error, AuthorizationResponseIssuer.Resolve(httpContext), useFragment));
    }

    /// <summary>
    /// Returns the error as the auto-posting page of the <c>form_post</c> response mode: the same
    /// <c>error</c>, <c>state</c> and <c>iss</c> parameters, delivered in a POST body instead of a URL.
    /// </summary>
    /// <remarks>
    /// Unlike the page of the code-issuing path, which OpenIddict writes from the HTTP context alone,
    /// this one is built with the transaction in hand — every path that ends a sign-in this way leaves
    /// it in its terminal Failed state — so the page is branded by that transaction's own levels,
    /// its ui_config record among them (SPEC-007 UI-101), takes its text overrides from the same levels
    /// (SPEC-012 CFG-254) and its language from the transaction itself.
    /// </remarks>
    /// <param name="httpContext">HTTP context of the request the error answers.</param>
    /// <param name="transaction">The transaction of the sign-in that ended.</param>
    /// <param name="oidcContext">Its stored OIDC request context.</param>
    /// <param name="error">The OAuth 2.0 error code the client receives.</param>
    /// <param name="promptLocalizer">Locale-file localizer.</param>
    /// <param name="language">Language detected from the request headers (the fallback of the chain).</param>
    /// <param name="veriqaOptions">Veriqa options (the configured default language).</param>
    /// <param name="brandingResolver">Resolver of the service-page branding.</param>
    /// <param name="languageRegistry">Registry of supported page languages.</param>
    /// <returns>An HTML content result carrying the error.</returns>
    private static async Task<IResult> SignInErrorFormPostResultAsync(
        HttpContext httpContext,
        Transaction transaction,
        OidcContext oidcContext,
        string error,
        IConfirmationPromptLocalizer promptLocalizer,
        string language,
        IOptions<VeriqaOptions> veriqaOptions,
        CorePageBrandingResolver brandingResolver,
        IAuthPageLanguageRegistry languageRegistry)
    {
        var pageLanguage = AuthPageLocalization.ResolveTransactionLanguage(
            transaction,
            language,
            veriqaOptions.Value.Localization.DefaultLanguage,
            languageRegistry.SupportedLanguages);

        var resolution = TransactionResolutionContext.For(transaction);
        var branding = await brandingResolver.ResolveAsync(resolution, httpContext.RequestAborted);
        var texts = await httpContext.RequestServices.GetRequiredService<CorePageTextResolver>()
            .ResolveAsync(resolution, httpContext.RequestAborted);

        // The CSP source of THIS response only — the target the page posts to, which is the
        // redirect_uri OpenIddict validated when it accepted the authorize request (SPEC-007 UI-091).
        var formActionSource = SecurityHeadersMiddleware.GetFormActionSource(oidcContext.RedirectUri);
        if (!string.IsNullOrEmpty(formActionSource))
        {
            httpContext.Items[SecurityHeaderConstants.FormActionTargetItemKey] = formActionSource;
        }

        var html = FormPostResponsePage.Build(
            action: oidcContext.RedirectUri!,
            fields: BuildSignInErrorParameters(oidcContext, error, AuthorizationResponseIssuer.Resolve(httpContext)),
            cspNonce: httpContext.Items[SecurityHeaderConstants.CspNonceItemKey] as string,
            localizer: promptLocalizer,
            language: pageLanguage,
            texts: texts,
            branding: branding);

        // The body carries the outcome of a sign-in attempt — it is not cacheable, exactly as the
        // code-issuing form_post response is not.
        httpContext.Response.Headers[HeaderNames.CacheControl] = ResponseNoStore;
        httpContext.Response.Headers[HeaderNames.Pragma] = ResponseNoCache;

        return Results.Content(html, "text/html", Encoding.UTF8);
    }

    /// <summary>
    /// Builds the standard error response URL for the client (OAuth 2.0 RFC 6749 §4.1.2.1,
    /// OIDC Core §3.1.2.6): the error code plus the original <c>state</c> and the issuer identifier on
    /// the client's redirect_uri, so the RP can tell a deliberate refusal (<c>access_denied</c>) from a failed
    /// sign-in (<c>server_error</c>).
    /// </summary>
    /// <remarks>
    /// The redirect_uri is the one OpenIddict already validated against the client registration when it
    /// accepted the authorize request (<c>GetOpenIddictServerRequest</c> in AuthorizeEndpoint) and stored
    /// on the transaction — not a URI taken from the current request, so this is not an open redirect.
    /// </remarks>
    /// <param name="oidcContext">The OIDC request context from the transaction (redirect_uri set).</param>
    /// <param name="error">The OAuth 2.0 error code the client receives.</param>
    /// <param name="issuer">The issuer identifier of the response; null when none can be stated.</param>
    /// <param name="useFragment">Whether the request asked for the <c>fragment</c> response mode: the
    /// same parameters then go after <c>#</c> instead of into the query string.</param>
    /// <returns>The absolute redirect URL.</returns>
    private static string BuildSignInErrorRedirectUrl(
        OidcContext oidcContext,
        string error,
        string? issuer,
        bool useFragment)
    {
        var parameters = string.Join(
            '&',
            BuildSignInErrorParameters(oidcContext, error, issuer)
                .Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));

        // A registered redirect_uri may already carry a query string — keep it intact. The fragment
        // mode appends to the URI as a whole, so that query string survives untouched as well.
        var separator = useFragment
            ? '#'
            : oidcContext.RedirectUri!.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        return $"{oidcContext.RedirectUri}{separator}{parameters}";
    }

    /// <summary>
    /// The parameters of the error itself, independent of how they are delivered: the error code, the
    /// original <c>state</c> and the issuer identifier (<c>iss</c>, RFC 9207), in that order. Both the
    /// redirect and the auto-posting page state exactly these, so the two ways out cannot drift apart.
    /// </summary>
    /// <remarks>
    /// This response never reaches OpenIddict, which attaches <c>iss</c> to the authorization responses
    /// it writes; the server metadata promises the parameter on every authorization response, so it is
    /// attached here by the same rule (<see cref="AuthorizationResponseIssuer"/>).
    /// </remarks>
    /// <param name="oidcContext">The OIDC request context from the transaction.</param>
    /// <param name="error">The OAuth 2.0 error code the client receives.</param>
    /// <param name="issuer">The issuer identifier of the response; null when none can be stated.</param>
    /// <returns>Parameter name/value pairs of the error (values unescaped).</returns>
    private static IReadOnlyList<KeyValuePair<string, string>> BuildSignInErrorParameters(
        OidcContext oidcContext,
        string error,
        string? issuer)
    {
        var parameters = new List<KeyValuePair<string, string>>(3)
        {
            new(OpenIddictConstants.Parameters.Error, error)
        };

        if (!string.IsNullOrEmpty(oidcContext.State))
        {
            parameters.Add(new(OpenIddictConstants.Parameters.State, oidcContext.State));
        }

        if (!string.IsNullOrEmpty(issuer))
        {
            parameters.Add(new(OpenIddictConstants.Parameters.Iss, issuer));
        }

        return parameters;
    }

    /// <summary>
    /// Resolves the effective confirmation surface for the transaction by its channel (CompletionSnapshot).
    /// When the channel adapter is absent — the safe default is the core's own surface (explicit
    /// confirmation, not a silent sign-in).
    /// </summary>
    private static async ValueTask<ConfirmationSurface> ResolveSurfaceAsync(
        Transaction transaction,
        IEffectiveConfirmationSurfaceResolver surfaceResolver,
        IEnumerable<IChannelAdapter> channelAdapters,
        CancellationToken cancellationToken)
    {
        var channelType = transaction.CompletionSnapshot?.ChannelType
            ?? transaction.ChannelIdentitySnapshot?.ChannelType;

        var adapter = channelType is null
            ? null
            : channelAdapters.FirstOrDefault(a => string.Equals(a.ChannelType, channelType, StringComparison.Ordinal));

        // The channel is undetermined/the adapter is unavailable — do not guess a silent sign-in:
        // require explicit confirmation on the core's own surface.
        if (adapter is null)
        {
            return ConfirmationSurfacePolicy.CoreDefault;
        }

        return await surfaceResolver.ResolveEffectiveSurfaceAsync(transaction, adapter, cancellationToken);
    }

    /// <summary>
    /// Shared callback validation: session_id, transaction presence/state, browser nonce, snapshots.
    /// Returns either the transaction or a ready error response.
    /// </summary>
    /// <remarks>
    /// Four states are valid here. <c>Completed</c> — the channel path already finalized the sign-in,
    /// so the completion snapshots must be present. "Awaiting a web answer" — the core accepted the
    /// channel event and holds the transaction Pending until the user answers on this very page; it
    /// carries no completion snapshots yet, and the snapshot check does not apply to it. The claims
    /// completion (<see cref="IsInClaimsCompletion"/>) — the user confirmed, and the transaction waits for
    /// the missing claims or is being finalized after the last of them by another request; no snapshots
    /// either. A sign-in that failed (<see cref="IsFailedSignIn"/>), refused by the user or ended by a
    /// fault — admitted only to carry that ending to the client of
    /// the browser that started it: a missing or foreign nonce cookie gets the "session expired" page,
    /// and is not recorded as a nonce mismatch. The browser nonce check is
    /// otherwise identical for all of them. The "Yes" of the web confirmation page admits a fifth one — a
    /// transaction a parallel "Yes" is finalizing (<c>Confirmed</c> outside the claims completion) — which
    /// every other caller treats as stale.
    /// <para>
    /// A <c>Completed</c> sign-in that has already issued its authorization code
    /// (<see cref="HasIssuedAuthorizationCode"/>) is not one of them: it stays in the store for the
    /// retention of a completed transaction, and a request that names it — a callback address reopened
    /// from the history, a sign-in window restored from the back/forward cache — is a stale session. It
    /// gets the "session expired" page before the browser check, so nothing is recorded and no cookie
    /// is set.
    /// </para>
    /// <para>
    /// A sign-in closed because the same user started a newer one (<see cref="IsSupersededSignIn"/>) is
    /// a stale session in the same way and gets the same page at the same point: it is failed, but its
    /// ending is not the client's to learn — the request it belongs to was left by the user, and an
    /// error returned for it could reset the state of the newer sign-in in the same browser.
    /// </para>
    /// </remarks>
    internal static async Task<(Transaction? Transaction, TransactionId? TransactionId, IResult? Error)> ValidateCallbackAsync(
        HttpContext httpContext,
        string? sessionIdStr,
        ITransactionService transactionService,
        IConfirmationPromptLocalizer promptLocalizer,
        string language,
        CorePageBrandingResolver brandingResolver,
        bool admitBeingFinalized = false)
    {
        // 1–2. session_id validation (source — query for GET, form for the POST confirmation) and the
        //      transaction it names
        var found = await FindTransactionAsync(
            httpContext, sessionIdStr, transactionService, promptLocalizer, language, brandingResolver);
        if (found.Error is not null)
        {
            return found;
        }

        var transaction = found.Transaction!;
        var transactionId = found.TransactionId!.Value;

        // 3. State check — either the sign-in is already finalized by the channel path, or the core is
        //    waiting for the answer on this page, or for the missing claims (SPEC-041 L91), or it
        //    failed. Anything else (it expired before the channel
        //    confirmed, its authorization code is already issued, a newer sign-in of the same user
        //    superseded it, or the callback was opened without any channel action at all) is a stale
        //    session from the user's point of view — same friendly page as the not-found case.
        var failed = IsFailedSignIn(transaction);
        if (HasIssuedAuthorizationCode(transaction)
            || IsSupersededSignIn(transaction)
            || (transaction.State is not TransactionState.Completed
                && !transaction.IsAwaitingWebConfirmation()
                && !IsInClaimsCompletion(transaction)
                && !failed
                && !(admitBeingFinalized && IsBeingFinalized(transaction))))
        {
            // The transaction WAS found — expired or no longer answerable, but it still names its
            // application and its ui_config record, so this page is branded like any other page of
            // that sign-in (SPEC-007 UI-101; UI-090 covers only the not-found case above).
            return (null, null, await ExpiredSessionResultAsync(
                httpContext, transaction, promptLocalizer, language, brandingResolver));
        }

        // 3a–4. The browser that started the sign-in, and no other. A failed sign-in is over, so a
        //       browser that is not the one that started it gets the "session expired" page, and
        //       nothing is recorded.
        var browserRefusal = await ValidateBrowserAsync(
            httpContext,
            transaction,
            transactionId,
            promptLocalizer,
            language,
            brandingResolver,
            mismatchIsStale: failed);
        if (browserRefusal is not null)
        {
            return (null, null, browserRefusal);
        }

        // 5. Snapshot presence check — only for an already finalized transaction. The waiting ones have
        //    not been through the finalization yet; their snapshots appear when it runs.
        if (transaction.State is TransactionState.Completed
            && (transaction.ResolvedIdentitySnapshot is null || transaction.CompletionSnapshot is null))
        {
            return (null, null, Results.Problem(
                detail: "Missing data for claims mapping.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: OidcErrorCodes.ClaimsMappingFailed));
        }

        return (transaction, transactionId, null);
    }

    /// <summary>
    /// Reads the transaction a request of the callback path names by its <c>session_id</c>: the first two
    /// steps of every handler under that path, with one answer each for a malformed identifier and for a
    /// transaction that is gone.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="sessionIdStr">The <c>session_id</c> of the request (query of a GET, form of a POST).</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="promptLocalizer">Locale-file localizer.</param>
    /// <param name="language">Detected page language.</param>
    /// <param name="brandingResolver">Resolver of the effective branding of the generated pages.</param>
    /// <returns>The transaction with its identifier, or the ready refusal.</returns>
    private static async Task<(Transaction? Transaction, TransactionId? TransactionId, IResult? Error)> FindTransactionAsync(
        HttpContext httpContext,
        string? sessionIdStr,
        ITransactionService transactionService,
        IConfirmationPromptLocalizer promptLocalizer,
        string language,
        CorePageBrandingResolver brandingResolver)
    {
        // 1. session_id validation
        var transactionId = SessionIdMapper.ToTransactionId(sessionIdStr);
        if (transactionId is null)
        {
            return (null, null, Results.Problem(
                detail: "Invalid or missing session_id.",
                statusCode: StatusCodes.Status400BadRequest,
                title: OidcErrorCodes.OidcRequestInvalid));
        }

        // 2. Retrieve the transaction. A missing transaction is not an attack/malformed request but a
        //    natural stale state (it was reaped after its retention window, or the callback link is old):
        //    show a friendly localized "session expired" page instead of a raw ProblemDetails so product
        //    users get a clear next step ("start again") rather than a bare core error.
        var txResult = await transactionService.GetTransactionAsync(
            transactionId.Value,
            httpContext.RequestAborted);

        if (txResult.IsFailure)
        {
            // The transaction was NOT found: there is no context to resolve against — no tenant, no
            // application, no record — so the page is rendered from the core level alone. That is the
            // norm for this branch, not a fallback (SPEC-007 UI-090).
            var globalBranding = await brandingResolver.ResolveAsync(
                ResolutionContext.Core, httpContext.RequestAborted);

            // The application is unknown as well, so there is no entry point to offer and the page
            // closes the window instead (UI-109). It is the one branch that asks for no address. Nor
            // are the text overrides resolved: there is neither a client nor a tenant to read them
            // from, and the core level states no value of that setting, so the answer is always empty.
            return (null, null, ExpiredSessionResult(
                httpContext,
                promptLocalizer,
                language,
                new CallbackExpiredPageSetup(globalBranding, CorePageTexts.None, RestartUrl: null)));
        }

        return (txResult.Value, transactionId, null);
    }

    /// <summary>
    /// Checks that a request of the callback path comes from the browser that started the sign-in: the
    /// transaction carries an OIDC context, and the browser nonce cookie of the request matches the nonce
    /// stored in it. One check for every handler that acts on a transaction on the browser's word — the
    /// callback, the answers of the web confirmation page and "Cancel sign-in" of the sign-in window.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">The transaction the request names, already admitted by its state.</param>
    /// <param name="transactionId">Identifier of that transaction.</param>
    /// <param name="promptLocalizer">Locale-file localizer.</param>
    /// <param name="language">Detected page language.</param>
    /// <param name="brandingResolver">Resolver of the effective branding of the generated pages.</param>
    /// <param name="mismatchIsStale">
    /// Whether a missing or foreign nonce is answered with the "session expired" page and left unrecorded —
    /// the answer for a transaction that is already over, where no live sign-in is left to protect.
    /// Otherwise the mismatch is refused with <c>browser_nonce_mismatch</c> and recorded.
    /// </param>
    /// <returns>The ready refusal, or null when the browser is the one that started the sign-in.</returns>
    private static async Task<IResult?> ValidateBrowserAsync(
        HttpContext httpContext,
        Transaction transaction,
        TransactionId transactionId,
        IConfirmationPromptLocalizer promptLocalizer,
        string language,
        CorePageBrandingResolver brandingResolver,
        bool mismatchIsStale)
    {
        // 1. A transaction with no OIDC context is not a subject of the browser callback AT ALL: it
        //    was created server-to-server (SPEC-039 C19), it is finalized on the server, and the
        //    relying party learns its outcome through its own surfaces. Whoever sent this request holds
        //    the entry material of that transaction — the identifier is visible in the page URL and
        //    encoded in the QR — and without this guard the request would fall into the browser-nonce
        //    branch below, which answers a transaction that never had a nonce as if a nonce had
        //    failed to match, and records that as a security occurrence.
        //    The answer is the neutral page of a link that leads nowhere (SPEC-007 UI-109): nobody was
        //    signing in, so the page of an expired sign-in would answer a question the user never
        //    asked. The transaction is left exactly as it is. The state check of the caller answers
        //    such a transaction the same way before this point (see ExpiredSessionResultAsync), so this
        //    check is reached by one in an answerable state. It sits BEFORE the nonce check and does
        //    not silence the sign-in path: a login transaction always carries an OIDC context, so a
        //    missing or foreign nonce cookie on one still refuses below (SPEC-002 §4.3, §10.7).
        //    This is the server-side layer of the requirement; the renderer's no-navigation mode is
        //    the first one, and it holds only for the shipped renderer.
        if (transaction.OidcContext is null)
        {
            return await TransactionEntryUnavailableResultAsync(
                httpContext, promptLocalizer, language, brandingResolver);
        }

        // 2. Browser nonce check
        var browserNonce = httpContext.Request.Cookies[OidcConstants.BrowserNonceCookieName];
        var browserNonceMatches = !string.IsNullOrEmpty(browserNonce)
            && string.Equals(browserNonce, transaction.OidcContext.BrowserNonce, StringComparison.Ordinal);

        // 2a. A transaction that is already over — a failed sign-in — answers only the browser that
        //     started it. Any other browser gets the "session expired" page, and nothing is recorded:
        //     there is no live sign-in left to protect.
        if (mismatchIsStale && !browserNonceMatches)
        {
            return await ExpiredSessionResultAsync(
                httpContext, transaction, promptLocalizer, language, brandingResolver);
        }

        if (!browserNonceMatches)
        {
            // A mismatch is refused, not destructive (SPEC-002 §10.7): the transaction is left exactly
            // as it was and lives to its TTL, so a caller who only knows the session_id cannot end
            // someone else's sign-in without authenticating. The owner's browser, arriving with the
            // right nonce afterwards, still completes. The refusal is a 400 carrying the
            // browser_nonce_mismatch code; the occurrence is always logged as a Warning and, where the
            // audit component is configured, also publishes an audit event on the transaction bus.
            // The response shape still differs from the two refusals before it ("not found" and "wrong
            // state" answer with the "session expired" HTML page, this one with ProblemDetails), so a
            // caller who knows the session_id can tell a live transaction from a missing one. That is
            // decided and accepted, not an oversight: the machine code has to live in a problem+json
            // body, and equalizing the shapes would cost either that code or the branding of the
            // expired page (SPEC-002 §10.7 p. 2 — the refusal must not disclose the transaction or
            // prove ownership of it, which it does not).
            await RecordBrowserNonceMismatchAsync(httpContext, transaction, transactionId);

            return Results.Problem(
                detail: "Browser nonce does not match.",
                statusCode: StatusCodes.Status400BadRequest,
                title: OidcErrorCodes.BrowserNonceMismatch);
        }

        return null;
    }

    /// <summary>
    /// Whether the transaction is on the branch of the claims completion (SPEC-041 L91): it waits for the
    /// missing claims, or the last of them was written and the sign-in is being finalized by that request.
    /// Every handler of the callback takes this branch first — such a transaction is signed in only by the
    /// write that closed the last missing claim.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <returns>true on the branch of the claims completion.</returns>
    internal static bool IsInClaimsCompletion(Transaction transaction) =>
        transaction.IsCompletingClaims()
        || (transaction.State is TransactionState.Confirmed && transaction.ClaimCompletion is not null);

    /// <summary>
    /// Whether the transaction failed: the user refused the sign-in — in the channel, on the confirming
    /// question or during the claims completion (SPEC-041 L73), or on a page of the core — or a fault
    /// ended it. Either ending is carried to the client by the browser that started the sign-in
    /// (SPEC-002 §4.3), under the error code <see cref="SignInFailureOAuthError.Resolve"/> gives its
    /// reason. A transaction that ran out of time is not one of these: it is <c>Expired</c>, and the
    /// callback shows it the "session expired" page.
    /// <para>
    /// A superseded sign-in (<see cref="IsSupersededSignIn"/>) is failed too and answers true here, but
    /// its ending is carried to nobody: <see cref="ValidateCallbackAsync"/> gives it the "session
    /// expired" page. A handler that branches on this predicate without that validation still sends
    /// the browser to the callback, and the callback answers with that page.
    /// </para>
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <returns>true for a transaction in the <c>Failed</c> state.</returns>
    internal static bool IsFailedSignIn(Transaction transaction) =>
        transaction.State is TransactionState.Failed;

    /// <summary>
    /// Whether the sign-in was closed because the same channel subject started a newer sign-in to the
    /// same application: the engine fails the earlier one with a reason of its own. The pages of the
    /// core treat it as a stale session, not as a failure to report.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <returns>true for a <c>Failed</c> transaction whose reason is the supersession.</returns>
    internal static bool IsSupersededSignIn(Transaction transaction) =>
        transaction.State is TransactionState.Failed
        && string.Equals(
            transaction.StateReasonCode, TransactionErrorCodes.SupersededByNewSignIn, StringComparison.Ordinal);

    /// <summary>
    /// Whether the sign-in of the transaction has already issued its authorization code: the authorization
    /// endpoint marks the transaction when it issues the code and keeps it in the store, so the mark — not
    /// a missing record — is what makes a completed sign-in redeemable once (SPEC-002 §4.2). The one
    /// predicate of that mark for the endpoints: the authorization endpoint issues no second code by it,
    /// and every handler of the callback path answers such a transaction with the "session expired" page.
    /// The engine checks the same field on its own when it writes the mark.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <returns>true when the code of the sign-in has been issued.</returns>
    internal static bool HasIssuedAuthorizationCode(Transaction transaction) =>
        transaction.AuthorizationCodeIssuedAt is not null;

    /// <summary>
    /// Sends the browser to the callback of the transaction with <c>303 See Other</c>: the answer to a POST
    /// whose outcome is the page the callback shows for the transaction as it stands (post/redirect/get).
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <returns>An empty 303 result carrying the callback address, path base included.</returns>
    internal static IResult SeeOtherToCallback(HttpContext httpContext, TransactionId transactionId)
    {
        httpContext.Response.Headers.Location =
            $"{httpContext.Request.PathBase}{TransactionStatusMessageFactory.BuildCallbackUrl(SessionIdMapper.ToSessionId(transactionId))}";

        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    /// <summary>
    /// Records a browser nonce mismatch: a <c>Warning</c> log entry and an audit event on the
    /// transaction bus (SPEC-002 §10.7 p. 3, §13.2). Since the mismatch no longer destroys the
    /// transaction, this record is the only trace the occurrence leaves.
    /// </summary>
    /// <remarks>
    /// Neither the nonce value nor any other security token is written (SPEC-002 §13.3): the record
    /// carries the fact and the transaction identifier, and the identifier is the parsed Base62 value,
    /// not the raw request parameter — so no caller-controlled text reaches the log.
    /// <para>
    /// The event goes out through <see cref="ITransactionEventPublisher"/> rather than straight into
    /// the audit sink: the publisher is registered by the transaction engine itself, so it resolves on
    /// every host that has transactions at all, while the sink exists only where the audit component
    /// was added. A host without it simply has no subscriber for the event, and the sign-in works as
    /// before.
    /// </para>
    /// </remarks>
    /// <param name="httpContext">Current request — the source of the services and of the log.</param>
    /// <param name="transaction">Transaction the callback was opened for.</param>
    /// <param name="transactionId">Identifier of that transaction.</param>
    private static async Task RecordBrowserNonceMismatchAsync(
        HttpContext httpContext,
        Transaction transaction,
        TransactionId transactionId)
    {
        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AuthorizeCallbackEndpoint));

        // transactionId.ToString() — a Base62 string, log injection is excluded
        logger.LogWarning(
            "Browser nonce does not match — the callback was opened by a browser other than the one " +
            "that started the sign-in; the transaction is kept. TransactionId: {TransactionId}",
            transactionId.ToString());

        // The audit event states the channel of the transaction. Every state admitted above carries a
        // channel snapshot, so an empty type here means an unexpected shape: the event is then not
        // published at all rather than naming an invented channel — the Warning above still stands.
        var channelType = transaction.ChannelIdentitySnapshot?.ChannelType
            ?? transaction.RequestedChannelType;
        if (string.IsNullOrEmpty(channelType))
        {
            return;
        }

        try
        {
            var timeProvider = httpContext.RequestServices.GetRequiredService<TimeProvider>();
            var eventPublisher = httpContext.RequestServices.GetRequiredService<ITransactionEventPublisher>();

            // CancellationToken.None — the occurrence must reach the journal even if the caller drops
            // the connection; otherwise aborting the request would suppress its own trace.
            // No attribution is attached: the caller here is unauthenticated by definition, and the
            // identity the transaction carries belongs to its owner, who did not act.
            await eventPublisher.PublishAsync(
                new TransactionChannelAuditEvent
                {
                    TransactionId = transactionId,
                    OccurredAt = timeProvider.GetUtcNow(),
                    ChannelType = channelType,
                    EventCode = OidcErrorCodes.BrowserNonceMismatch,
                    Details = null
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // An unavailable journal does not turn into a denial of the sign-in service: the caller
            // gets its 400 either way.
            logger.LogWarning(ex,
                "Failed to publish audit event {EventCode}. TransactionId: {TransactionId}",
                OidcErrorCodes.BrowserNonceMismatch,
                transactionId.ToString());
        }
    }

    /// <summary>
    /// Completes the browser side of the sign-in: signs in the intermediate cookie, clears the nonce and
    /// builds the redirect to /connect/authorize. A shared point for GET (None/InChannel) and POST (OnWebPage).
    /// </summary>
    /// <remarks>
    /// The cookie carries a reference to the transaction and no claims (SPEC-002 §4.3): the claims are
    /// built from the transaction by the authorization endpoint when it issues the code, and the
    /// transaction is marked there as having issued it — once, which is what keeps the cookie
    /// single-use while the transaction lives out its retention. A caller hands in a transaction that
    /// carries no such mark (<see cref="HasIssuedAuthorizationCode"/>). A claim set in the cookie would
    /// grow with every claim a channel reports — an avatar image alone exceeds the header limits of
    /// common proxies and servers — while the reference keeps the cookie at a fixed size. The
    /// transaction store is already shared state the callback cannot work without, so the reference
    /// adds no new requirement to a deployment.
    /// </remarks>
    internal static async Task<IResult> CompleteSignInAsync(
        HttpContext httpContext,
        Transaction transaction,
        TransactionId transactionId)
    {
        // 6. Cookie authentication carrying only the session identifier of the transaction. The value
        //    is protected by the cookie handler, so it can be neither read nor forged by the browser.
        var identity = new ClaimsIdentity(
            [new Claim(OidcConstants.SessionIdParameterName, SessionIdMapper.ToSessionId(transactionId))],
            OidcConstants.CookieAuthScheme);

        await httpContext.SignInAsync(OidcConstants.CookieAuthScheme, new ClaimsPrincipal(identity));

        // 7. Delete the browser nonce cookie (Path matches the one set in AuthorizeEndpoint —
        // including PathBase, otherwise the browser will not match the cookie for deletion)
        httpContext.Response.Cookies.Delete(OidcConstants.BrowserNonceCookieName, new CookieOptions
        {
            Path = $"{httpContext.Request.PathBase}{OidcEndpoints.AuthorizeCallback}"
        });

        // 8. Build the redirect URL to /connect/authorize with the original OIDC parameters.
        // PathBase is required: Location is returned to the browser as-is, and without the prefix behind
        // a sub-path (veriqa.app/demo) the redirect would go past the application.
        var redirectUrl = $"{httpContext.Request.PathBase}{BuildAuthorizeRedirectUrl(transaction.OidcContext!)}";

        return Results.Redirect(redirectUrl);
    }

    /// <summary>
    /// Builds the friendly "sign-in session expired" HTML response (400) for a stale/expired transaction,
    /// replacing a raw ProblemDetails so the product user sees a clear message instead of a bare core error.
    /// 400 is kept (the request references a no-longer-valid session), but the body is a user-facing page.
    /// </summary>
    /// <param name="httpContext">Current request (source of the CSP nonce of the page's script).</param>
    /// <param name="promptLocalizer">Locale-file localizer.</param>
    /// <param name="language">Detected page language.</param>
    /// <param name="expiredPage">Effective branding of the generated page, the overrides of its texts
    /// and the address its action leads to, already resolved by the caller over the context that page
    /// actually has (SPEC-007 UI-101/UI-090, UI-109).</param>
    /// <returns>An HTML content result with status 400.</returns>
    internal static IResult ExpiredSessionResult(
        HttpContext httpContext,
        IConfirmationPromptLocalizer promptLocalizer,
        string language,
        CallbackExpiredPageSetup expiredPage)
    {
        var html = CallbackExpiredPage.Build(
            promptLocalizer,
            language,
            expiredPage.Texts,
            expiredPage.Branding,
            expiredPage.RestartUrl,
            httpContext.Items[SecurityHeaderConstants.CspNonceItemKey] as string);

        return Results.Content(html, "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Builds the "sign-in session expired" response for a transaction that was found but is no longer
    /// answerable here: the page is branded by the levels of that transaction, like any other page of its
    /// sign-in (SPEC-007 UI-101), and offers the entry point of its application (UI-109).
    /// </summary>
    /// <remarks>
    /// A transaction with no OIDC context is not a sign-in: it was created server-to-server to confirm an
    /// action (SPEC-039 C19), and whoever created it reacts to its outcome. Whatever state it is in, a
    /// request of the callback path that names it is answered with the neutral page of a link that leads
    /// nowhere (<see cref="TransactionEntryUnavailableResultAsync"/>) and not with the page of an expired
    /// sign-in. The check is made here because every path of the callback that answers a found
    /// transaction with this page passes through this method.
    /// </remarks>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">The transaction the page concerns.</param>
    /// <param name="promptLocalizer">Locale-file localizer.</param>
    /// <param name="language">Detected page language.</param>
    /// <param name="brandingResolver">Resolver of the effective branding of the generated pages.</param>
    /// <returns>An HTML content result with status 400.</returns>
    internal static async Task<IResult> ExpiredSessionResultAsync(
        HttpContext httpContext,
        Transaction transaction,
        IConfirmationPromptLocalizer promptLocalizer,
        string language,
        CorePageBrandingResolver brandingResolver)
    {
        if (transaction.OidcContext is null)
        {
            return await TransactionEntryUnavailableResultAsync(
                httpContext, promptLocalizer, language, brandingResolver);
        }

        var expiredPage = await ResolveExpiredPageSetupAsync(
            httpContext, transaction, brandingResolver, httpContext.RequestAborted);

        return ExpiredSessionResult(httpContext, promptLocalizer, language, expiredPage);
    }

    /// <summary>
    /// Answers a request of the callback path that names a transaction which is not a sign-in with the
    /// neutral page of a link that leads nowhere (<see cref="TransactionEntryUnavailablePage"/>,
    /// SPEC-007 UI-109). The page is built without reading the transaction: branded by the core level, in
    /// the language of the request. The status is 400, as for the other refusals of the callback path.
    /// The transaction is not changed and nothing is recorded.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="promptLocalizer">Locale-file localizer.</param>
    /// <param name="language">Detected page language.</param>
    /// <param name="brandingResolver">Resolver of the effective branding of the generated pages.</param>
    /// <returns>An HTML content result with status 400.</returns>
    private static async Task<IResult> TransactionEntryUnavailableResultAsync(
        HttpContext httpContext,
        IConfirmationPromptLocalizer promptLocalizer,
        string language,
        CorePageBrandingResolver brandingResolver)
    {
        var branding = await brandingResolver.ResolveAsync(ResolutionContext.Core, httpContext.RequestAborted);
        var html = TransactionEntryUnavailablePage.Build(promptLocalizer, language, branding);

        return Results.Content(html, AuthPageConstants.HtmlContentType, Encoding.UTF8, StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Resolves what the "session expired" page of a FOUND transaction is rendered with — its branding,
    /// the overrides of its texts (SPEC-012 CFG-254) and the address its action leads to — in one pass
    /// over the context of that transaction.
    /// </summary>
    /// <remarks>
    /// The address is decoration of a terminal page, like the branding beside it: a failure to resolve
    /// it must not replace the page with an error. Such a failure is logged and leaves the page with
    /// its "Close" button — the very answer an application that states no address gets. A failure to
    /// resolve the text overrides leaves the page with its regular texts; the resolver of the texts
    /// answers so itself. A cancellation of the token handed in is not a failure and propagates.
    /// </remarks>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">The transaction the page concerns.</param>
    /// <param name="brandingResolver">Resolver of the effective branding of the generated pages.</param>
    /// <param name="cancellationToken">
    /// Token every resolution of this method runs under: the cancellation of the request, or none where
    /// the caller must reach a step that may not be skipped.
    /// </param>
    /// <returns>Branding, text overrides and action address of the page.</returns>
    private static async Task<CallbackExpiredPageSetup> ResolveExpiredPageSetupAsync(
        HttpContext httpContext,
        Transaction transaction,
        CorePageBrandingResolver brandingResolver,
        CancellationToken cancellationToken)
    {
        var resolution = TransactionResolutionContext.For(transaction);
        var branding = await brandingResolver.ResolveAsync(resolution, cancellationToken);

        var services = httpContext.RequestServices;
        var texts = await services.GetRequiredService<CorePageTextResolver>()
            .ResolveAsync(resolution, cancellationToken);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AuthorizeCallbackEndpoint));

        try
        {
            var restartUrl = await InitiateLoginUriRule.ResolveAdmissibleAsync(
                services.GetRequiredService<AuthPageSettingsResolver>(),
                resolution,
                resolution.ApplicationId,
                services.GetRequiredService<IHostEnvironment>(),
                logger,
                cancellationToken);

            return new CallbackExpiredPageSetup(branding, texts, restartUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "The login-initiation address of the application could not be resolved; the \"session expired\" "
                + "page offers to close the window instead. TransactionId: {TransactionId}",
                transaction.Id.ToString());

            return new CallbackExpiredPageSetup(branding, texts, RestartUrl: null);
        }
    }

    /// <summary>
    /// Validates the AntiForgery token of a state-changing POST of the callback pages and of the
    /// transaction confirmation page.
    /// </summary>
    /// <remarks>
    /// A refusal is answered with the page of an out-of-date form (<see cref="AntiforgeryRefusalPage"/>,
    /// SPEC-007 UI-126 — the answer of every POST of this shared check), status 400: the one who meets this refusal in practice is a person in a browser — a
    /// form left open too long, or a page opened inside a frame that withholds its cookies — and a raw error
    /// document tells that person nothing. The page reads nothing of the transaction, since the request may be
    /// forged; the services it is built with are taken from the request, so every caller of this check
    /// gets the page as it is.
    /// </remarks>
    /// <param name="httpContext">Current request.</param>
    /// <param name="antiforgery">AntiForgery service.</param>
    /// <returns>The refusal to return, or null when the token is valid.</returns>
    internal static async Task<IResult?> ValidateAntiforgeryAsync(HttpContext httpContext, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return await AntiforgeryRefusalResultAsync(httpContext);
        }
    }

    /// <summary>
    /// Builds the answer to a request that failed the AntiForgery check: the page of an out-of-date form in
    /// the language of the request, branded by the core level, with status 400.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <returns>An HTML content result with status 400.</returns>
    private static async Task<IResult> AntiforgeryRefusalResultAsync(HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        var promptLocalizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
        var language = DetectCallbackLanguage(
            httpContext,
            services.GetRequiredService<IOptions<VeriqaOptions>>(),
            services.GetRequiredService<IAuthPageLanguageRegistry>());
        var branding = await services.GetRequiredService<CorePageBrandingResolver>()
            .ResolveAsync(ResolutionContext.Core, httpContext.RequestAborted);
        var html = AntiforgeryRefusalPage.Build(promptLocalizer, language, branding);

        return Results.Content(html, AuthPageConstants.HtmlContentType, Encoding.UTF8, StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Detects the callback page language the same way as the sign-in window (Accept-Language + the
    /// configured default, clamped to the supported-language registry), so the web-confirmation and
    /// expired-session pages localize consistently (TASK-060).
    /// </summary>
    internal static string DetectCallbackLanguage(
        HttpContext httpContext,
        IOptions<VeriqaOptions> veriqaOptions,
        IAuthPageLanguageRegistry languageRegistry)
    {
        var acceptLanguage = httpContext.Request.Headers.AcceptLanguage.ToString();
        return AuthPageStrings.DetectLanguage(
            acceptLanguage,
            veriqaOptions.Value.Localization.DefaultLanguage,
            languageRegistry.SupportedLanguages);
    }

    /// <summary>
    /// Builds the redirect URL to authorize with the original OIDC parameters.
    /// </summary>
    /// <param name="oidcContext">The OIDC request context from the transaction.</param>
    /// <returns>The redirect URL.</returns>
    private static string BuildAuthorizeRedirectUrl(OidcContext oidcContext)
    {
        // The method builds the query string from the original OIDC parameters
        var queryParams = new Dictionary<string, string?>
        {
            [OpenIddictConstants.Parameters.ResponseType] = oidcContext.ResponseType,
            [OpenIddictConstants.Parameters.ResponseMode] = oidcContext.ResponseMode,
            [OpenIddictConstants.Parameters.ClientId] = oidcContext.ClientId,
            [OpenIddictConstants.Parameters.RedirectUri] = oidcContext.RedirectUri,
            [OpenIddictConstants.Parameters.Scope] = oidcContext.Scope,
            [OpenIddictConstants.Parameters.State] = oidcContext.State,
            [OpenIddictConstants.Parameters.Nonce] = oidcContext.Nonce,
            [OpenIddictConstants.Parameters.CodeChallenge] = oidcContext.CodeChallenge,
            [OpenIddictConstants.Parameters.CodeChallengeMethod] = oidcContext.CodeChallengeMethod
        };

        var queryString = string.Join("&",
            queryParams
                .Where(kvp => kvp.Value is not null)
                .Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value!)}"));

        return $"{OidcEndpoints.Authorize}?{queryString}";
    }
}
