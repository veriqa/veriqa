// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Veriqa.Core.AuthServer.ClaimCompletion;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// The one point where an action of the page of the claims completion gets its transaction (SPEC-041 C39,
/// N78): it checks the right of the browser to act and hands out either the transaction or the ready answer
/// to a request that has no such right.
/// </summary>
/// <remarks>
/// On the page of the device the sign-in was started on the right is the binding of that page — the one the
/// web confirmation page has: the AntiForgery token, the session identifier of the form and the browser
/// nonce cookie of the callback, which reaches the actions because their routes lie under the callback
/// path. The right holds while the transaction is in the claims completion; submitting and the actions
/// of the email block also need the current step to be the web step shown on this page, while
/// cancelling is accepted on any step.
/// No action spends the right: a page shown again after a refused submission accepts the next one.
/// <para>
/// Out of the completion the right no longer holds, and the answer is what the callback itself would give
/// the transaction: a completed sign-in is completed, a confirmed one waits on the page, a sign-in the user
/// cancelled in the channel is sent to the callback, anything else is the "session expired" page. The
/// transaction is never changed on the way.
/// </para>
/// <para>
/// On the page of the device that confirmed the sign-in the right is the form token (N78): a request whose
/// form carries the token field is checked by the token alone, with neither the AntiForgery token nor the
/// browser nonce (C39) — that device never had them. The token acts only while it is the path of the web
/// step: the transaction is in the completion on the web step, the surface of the step is that page and the
/// token matches the stored fingerprint — for every action, cancelling included. The
/// page opened by the link goes through the same check. Out of it, a sign-in the token did lead to — the
/// fingerprint matches on that surface — gets the receipt of its confirmation; everything else gets one
/// neutral "link no longer available" page, whatever the reason.
/// </para>
/// </remarks>
internal sealed class ClaimCompletionActionRight
{
    /// <summary>
    /// AntiForgery service.
    /// </summary>
    private readonly IAntiforgery _antiforgery;

    /// <summary>
    /// Transaction service.
    /// </summary>
    private readonly ITransactionService _transactionService;

    /// <summary>
    /// Locale-file localizer.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _localizer;

    /// <summary>
    /// Veriqa options — the configured default language.
    /// </summary>
    private readonly IOptions<VeriqaOptions> _veriqaOptions;

    /// <summary>
    /// Resolver of the branding of the service pages.
    /// </summary>
    private readonly CorePageBrandingResolver _brandingResolver;

    /// <summary>
    /// Registry of the supported page languages.
    /// </summary>
    private readonly IAuthPageLanguageRegistry _languageRegistry;

    /// <summary>
    /// The page of the claims completion — the answer to a confirmed transaction.
    /// </summary>
    private readonly ClaimCompletionPageResponder _pages;

    /// <summary>
    /// Creates the check.
    /// </summary>
    /// <param name="antiforgery">AntiForgery service.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="veriqaOptions">Veriqa options.</param>
    /// <param name="brandingResolver">Resolver of the branding of the service pages.</param>
    /// <param name="languageRegistry">Registry of the supported page languages.</param>
    /// <param name="pages">The page of the claims completion.</param>
    public ClaimCompletionActionRight(
        IAntiforgery antiforgery,
        ITransactionService transactionService,
        IConfirmationPromptLocalizer localizer,
        IOptions<VeriqaOptions> veriqaOptions,
        CorePageBrandingResolver brandingResolver,
        IAuthPageLanguageRegistry languageRegistry,
        ClaimCompletionPageResponder pages)
    {
        _antiforgery = antiforgery ?? throw new ArgumentNullException(nameof(antiforgery));
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _veriqaOptions = veriqaOptions ?? throw new ArgumentNullException(nameof(veriqaOptions));
        _brandingResolver = brandingResolver ?? throw new ArgumentNullException(nameof(brandingResolver));
        _languageRegistry = languageRegistry ?? throw new ArgumentNullException(nameof(languageRegistry));
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
    }

    /// <summary>
    /// Checks the right of the request to perform an action on its transaction.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="form">The form of the request, already read.</param>
    /// <param name="action">The action asked for.</param>
    /// <returns>
    /// The transaction when the right holds, with what carried the right and the language detected for the
    /// request; otherwise the ready answer to the request (the transaction is then null).
    /// </returns>
    public async Task<(Transaction? Transaction, ClaimCompletionRightCarrier Carrier, string RequestLanguage, IResult? Refusal)> CheckAsync(
        HttpContext httpContext,
        IFormCollection form,
        ClaimCompletionAction action)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(form);

        // 0. A form that carries the form token is the page of the device that confirmed the sign-in: the
        //    token is its only right, and the binding of the initiating page is not asked for (C39).
        if (form.ContainsKey(ClaimCompletionFormFields.FormToken))
        {
            var (tokenTransaction, tokenLanguage, tokenRefusal) = await CheckFormTokenAsync(
                httpContext,
                form[OidcConstants.SessionIdParameterName].FirstOrDefault(),
                form[ClaimCompletionFormFields.FormToken].FirstOrDefault());

            return (tokenTransaction, ClaimCompletionRightCarrier.FormToken, tokenLanguage, tokenRefusal);
        }

        var (transaction, language, refusal) = await CheckInitiatingPageAsync(httpContext, form, action);

        return (transaction, ClaimCompletionRightCarrier.InitiatingPage, language, refusal);
    }

    /// <summary>
    /// Checks the right of the form token to show the form of the web step or to act on it (SPEC-041 N78).
    /// Reads only: nothing is written, whatever the answer.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="sessionId">External session identifier the request names.</param>
    /// <param name="formToken">The form token the request carries.</param>
    /// <returns>
    /// The transaction when the right holds, with the language detected for the request; otherwise the
    /// ready answer to the request (the transaction is then null).
    /// </returns>
    public async Task<(Transaction? Transaction, string RequestLanguage, IResult? Refusal)> CheckFormTokenAsync(
        HttpContext httpContext,
        string? sessionId,
        string? formToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // The method answers "no right" with one neutral page for every reason — no such transaction, a
        // token that does not match, a surface or a step the token no longer leads to, a failed or expired
        // sign-in — so the answer tells a holder of a foreign session identifier nothing. The only other
        // answer is to the holder of the matching token: the receipt of a sign-in that went through.
        var language = AuthorizeCallbackEndpoint.DetectCallbackLanguage(httpContext, _veriqaOptions, _languageRegistry);

        if (SessionIdMapper.ToTransactionId(sessionId) is not { } transactionId || string.IsNullOrEmpty(formToken))
        {
            return (null, language, await _pages.CrossDeviceLinkUnavailableAsync(httpContext, language));
        }

        var found = await _transactionService.GetTransactionAsync(transactionId, httpContext.RequestAborted);
        if (found.IsFailure
            || found.Value.ClaimCompletion is not { Surface: WebStepSurface.CrossDevicePage } completion
            || !ClaimFormToken.Matches(formToken, completion.FormTokenHash))
        {
            return (null, language, await _pages.CrossDeviceLinkUnavailableAsync(httpContext, language));
        }

        var transaction = found.Value;

        if (transaction.IsCompletingClaims()
            && transaction.GetCurrentClaimCompletionStep() is ClaimCompletionStep.WebStep)
        {
            return (transaction, language, null);
        }

        // A confirmed transaction is finalized by the request that wrote the last claim; a completed one is
        // over. Either way the sign-in went through, and the browser of the device it was started on finishes
        // it — this page only reports it.
        if (transaction.State is TransactionState.Completed or TransactionState.Confirmed)
        {
            return (null, language, await _pages.CrossDeviceReceiptAsync(
                httpContext, TransactionOutcome.Confirmed, transaction, transaction.UpdatedAt, language));
        }

        return (null, language, await _pages.CrossDeviceLinkUnavailableAsync(httpContext, language));
    }

    /// <summary>
    /// Checks the right of the page of the device the sign-in was started on — the binding of the callback.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="form">The form of the request, already read.</param>
    /// <param name="action">The action asked for.</param>
    /// <returns>The transaction when the right holds, the language, or the ready answer.</returns>
    private async Task<(Transaction? Transaction, string RequestLanguage, IResult? Refusal)> CheckInitiatingPageAsync(
        HttpContext httpContext,
        IFormCollection form,
        ClaimCompletionAction action)
    {
        var language = AuthorizeCallbackEndpoint.DetectCallbackLanguage(httpContext, _veriqaOptions, _languageRegistry);

        // 1. AntiForgery — as for the answers of the web confirmation page: missing or invalid → 400, and
        //    nothing is read off the transaction.
        var antiforgeryFailure = await AuthorizeCallbackEndpoint.ValidateAntiforgeryAsync(httpContext, _antiforgery);
        if (antiforgeryFailure is not null)
        {
            return (null, language, antiforgeryFailure);
        }

        // 2. The callback binding: the session identifier of the form, the transaction and the browser nonce.
        var validation = await AuthorizeCallbackEndpoint.ValidateCallbackAsync(
            httpContext,
            form[OidcConstants.SessionIdParameterName].FirstOrDefault(),
            _transactionService,
            _localizer,
            language,
            _brandingResolver);
        if (validation.Error is not null)
        {
            return (null, language, validation.Error);
        }

        var transaction = validation.Transaction!;

        // 3. The right holds in the claims completion. Cancelling is accepted on any step; submitting and the
        //    actions of the email block only on the web step shown on this page — another step or surface is
        //    sent to the callback, which shows the page of the state the transaction is in.
        if (transaction.IsCompletingClaims())
        {
            var onThisPage = transaction.GetCurrentClaimCompletionStep() is ClaimCompletionStep.WebStep
                && transaction.ClaimCompletion!.Surface is WebStepSurface.InitiatingPage;

            if (action is ClaimCompletionAction.Cancel || onThisPage)
            {
                return (transaction, language, null);
            }

            return (null, language, AuthorizeCallbackEndpoint.SeeOtherToCallback(httpContext, transaction.Id));
        }

        // 4. Out of the completion: the answer the callback gives the transaction.
        return (null, language, await AnswerWithoutRightAsync(httpContext, transaction, language));
    }

    /// <summary>
    /// Answers an action on a transaction that is no longer in the claims completion.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">The transaction, validated by the callback binding.</param>
    /// <param name="language">Language detected for the request.</param>
    /// <returns>The answer.</returns>
    private async Task<IResult> AnswerWithoutRightAsync(HttpContext httpContext, Transaction transaction, string language)
    {
        if (transaction.State is TransactionState.Completed)
        {
            return await AuthorizeCallbackEndpoint.CompleteSignInAsync(httpContext, transaction, transaction.Id);
        }

        // Confirmed with the completion mark: the last write was made elsewhere and the finalization runs.
        if (transaction.State is TransactionState.Confirmed)
        {
            return await _pages.RespondAsync(httpContext, transaction, language);
        }

        // A failed sign-in: the callback returns its ending to the client.
        if (AuthorizeCallbackEndpoint.IsFailedSignIn(transaction))
        {
            return AuthorizeCallbackEndpoint.SeeOtherToCallback(httpContext, transaction.Id);
        }

        return await _pages.ExpiredSessionAsync(httpContext, transaction, language);
    }
}
