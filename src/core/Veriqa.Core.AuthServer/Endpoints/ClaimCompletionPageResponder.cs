// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using OpenIddict.Abstractions;
using Veriqa.Core.AuthServer.ClaimCompletion;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.InitiatorContext;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Answers a request on a transaction of the claims completion with the page of the completion (SPEC-041
/// L52, L108). On the device the sign-in was started on it picks the state of the page from the fields of the
/// transaction (C90), builds the form with the right of this browser to act on it (N78) and renders the page
/// in the language and the branding of the transaction; the two other answers the completion ends in on
/// that device — "session expired" and the refusal to the client — are taken from the callback, so they
/// stay the callback's own. On the device that confirmed the sign-in it renders the same page with the form
/// token as the right, and the answers that page ends in: the receipt of the outcome and the neutral page of
/// a link that no longer leads anywhere.
/// </summary>
/// <remarks>
/// One home for every answer that is this page — the callback GET, the page opened by the form token, a
/// refused submission shown again and the waiting state after the last write — so they cannot drift apart.
/// Every answer of the other device carries <c>Cache-Control: no-store</c>: its page and its address carry
/// the form token. Scoped: the form model builder it uses reads the channel adapters.
/// </remarks>
internal sealed class ClaimCompletionPageResponder
{
    /// <summary>
    /// Builder of the model of the form.
    /// </summary>
    private readonly IClaimCompletionFormModelBuilder _formModelBuilder;

    /// <summary>
    /// AntiForgery tokens the forms of the page carry.
    /// </summary>
    private readonly IAntiforgery _antiforgery;

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
    /// Resolver of the overrides of the texts of the core pages (<c>AuthPage:Texts</c>): the title and the
    /// instruction of the form, and the action under the line of the receipt.
    /// </summary>
    private readonly CorePageTextResolver _textResolver;

    /// <summary>
    /// Registry of the supported page languages.
    /// </summary>
    private readonly IAuthPageLanguageRegistry _languageRegistry;

    /// <summary>
    /// Registered channel adapters — the name of the channel of the phone step is read off them.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Port of the terminal texts — the receipts of the page of the device that confirmed the sign-in.
    /// </summary>
    private readonly IOutcomeReceiptText _receiptText;

    /// <summary>
    /// The rules of the email block the form carries.
    /// </summary>
    private readonly EmailCompletionBlock _emailBlock;

    /// <summary>
    /// Resolver of the settings of the sign-in page — the page of the completion loads the SignalR client
    /// library from where the sign-in window of its transaction does.
    /// </summary>
    private readonly AuthPageSettingsResolver _pageSettings;

    /// <summary>
    /// Clock the remainder of the transaction is measured on.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Carry of what a page shows after an action over the redirect that answers it.
    /// </summary>
    private readonly ClaimCompletionPageCarry _carry;

    /// <summary>
    /// Normalizer that tells the kind of the device the receipt is opened on.
    /// </summary>
    private readonly IUserAgentNormalizer _userAgentNormalizer;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ClaimCompletionPageResponder> _logger;

    /// <summary>
    /// Creates the responder.
    /// </summary>
    /// <param name="formModelBuilder">Builder of the model of the form.</param>
    /// <param name="antiforgery">AntiForgery service.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="veriqaOptions">Veriqa options.</param>
    /// <param name="brandingResolver">Resolver of the branding of the service pages.</param>
    /// <param name="textResolver">Resolver of the overrides of the texts of the core pages.</param>
    /// <param name="languageRegistry">Registry of the supported page languages.</param>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="receiptText">Port of the terminal texts.</param>
    /// <param name="emailBlock">The rules of the email block.</param>
    /// <param name="pageSettings">Resolver of the settings of the sign-in page.</param>
    /// <param name="timeProvider">Clock the remainder of the transaction is measured on.</param>
    /// <param name="carry">Carry of what a page shows after an action over the redirect that answers it.</param>
    /// <param name="userAgentNormalizer">Normalizer of the User-Agent of the request.</param>
    /// <param name="logger">Logger.</param>
    public ClaimCompletionPageResponder(
        IClaimCompletionFormModelBuilder formModelBuilder,
        IAntiforgery antiforgery,
        IConfirmationPromptLocalizer localizer,
        IOptions<VeriqaOptions> veriqaOptions,
        CorePageBrandingResolver brandingResolver,
        CorePageTextResolver textResolver,
        IAuthPageLanguageRegistry languageRegistry,
        IEnumerable<IChannelAdapter> adapters,
        IOutcomeReceiptText receiptText,
        EmailCompletionBlock emailBlock,
        AuthPageSettingsResolver pageSettings,
        TimeProvider timeProvider,
        ClaimCompletionPageCarry carry,
        IUserAgentNormalizer userAgentNormalizer,
        ILogger<ClaimCompletionPageResponder> logger)
    {
        _formModelBuilder = formModelBuilder ?? throw new ArgumentNullException(nameof(formModelBuilder));
        _antiforgery = antiforgery ?? throw new ArgumentNullException(nameof(antiforgery));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _veriqaOptions = veriqaOptions ?? throw new ArgumentNullException(nameof(veriqaOptions));
        _brandingResolver = brandingResolver ?? throw new ArgumentNullException(nameof(brandingResolver));
        _textResolver = textResolver ?? throw new ArgumentNullException(nameof(textResolver));
        _languageRegistry = languageRegistry ?? throw new ArgumentNullException(nameof(languageRegistry));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _receiptText = receiptText ?? throw new ArgumentNullException(nameof(receiptText));
        _emailBlock = emailBlock ?? throw new ArgumentNullException(nameof(emailBlock));
        _pageSettings = pageSettings ?? throw new ArgumentNullException(nameof(pageSettings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _carry = carry ?? throw new ArgumentNullException(nameof(carry));
        _userAgentNormalizer = userAgentNormalizer ?? throw new ArgumentNullException(nameof(userAgentNormalizer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Resolves the content language of the page of a transaction: the language of the sign-in session,
    /// the request language when the transaction states none, and English when the locale files do not
    /// carry the page in that language.
    /// </summary>
    /// <param name="transaction">Transaction of the page.</param>
    /// <param name="requestLanguage">Language detected for the current request.</param>
    /// <returns>Content language of the page.</returns>
    public string ResolveLanguage(Transaction transaction, string requestLanguage)
    {
        var pageLanguage = AuthPageLocalization.ResolveTransactionLanguage(
            transaction,
            requestLanguage,
            _veriqaOptions.Value.Localization.DefaultLanguage,
            _languageRegistry.SupportedLanguages);

        return AuthPageLocalization.ResolveContentLanguage(_localizer, ClaimCompletionPage.PageTitle, pageLanguage);
    }

    /// <summary>
    /// Builds the form of the page with the right of this browser to act on the transaction: the session
    /// identifier and the AntiForgery token of this request (SPEC-041 N78).
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">Transaction of the page.</param>
    /// <param name="language">Content language of the page (<see cref="ResolveLanguage"/>).</param>
    /// <returns>The model of the form, fields without values and errors.</returns>
    public async ValueTask<ClaimCompletionFormModel> BuildFormAsync(
        HttpContext httpContext,
        Transaction transaction,
        string language)
    {
        var model = await _formModelBuilder.BuildAsync(
            transaction, WebStepSurface.InitiatingPage, language, httpContext.RequestAborted);

        return model with { RightFields = BuildInitiatingPageRightFields(httpContext, transaction) };
    }

    /// <summary>
    /// Builds the hidden fields that give this browser the right to act on the transaction from the page of
    /// the device the sign-in was started on: the session identifier and the AntiForgery token of this
    /// request (SPEC-041 N78).
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">Transaction of the page.</param>
    /// <returns>Names and values of the fields.</returns>
    private IReadOnlyList<KeyValuePair<string, string>> BuildInitiatingPageRightFields(
        HttpContext httpContext,
        Transaction transaction)
    {
        var tokens = _antiforgery.GetAndStoreTokens(httpContext);

        return
        [
            new(OidcConstants.SessionIdParameterName, SessionIdMapper.ToSessionId(transaction.Id)),
            new(tokens.FormFieldName, tokens.RequestToken ?? string.Empty)
        ];
    }

    /// <summary>
    /// Answers with the page of the transaction as it stands: the form while the web step is on the page of
    /// this device, a waiting state otherwise (SPEC-041 L44, L108, L109). Either one offers "Cancel sign-in" while
    /// the sign-in is in the claims completion.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">Transaction of the page, in the claims completion or confirmed with it.</param>
    /// <param name="requestLanguage">Language detected for the current request.</param>
    /// <param name="form">A form to show instead of a fresh one — the refused submission with its values
    /// and errors; null — the state is taken from the transaction.</param>
    /// <param name="emailError">Localized error of the action of the email block shown again; null — none.</param>
    /// <param name="emailAddressStep">Whether the email block brings the address step of its way back over a
    /// live mail — a mark of this answer only.</param>
    /// <param name="emailEnteredAddress">The address the request typed into the email block, shown again in
    /// the input of its address step; null — the input shows the address of the last mail.</param>
    /// <returns>HTML result with status 200.</returns>
    public async Task<IResult> RespondAsync(
        HttpContext httpContext,
        Transaction transaction,
        string requestLanguage,
        ClaimCompletionFormModel? form = null,
        string? emailError = null,
        bool emailAddressStep = false,
        string? emailEnteredAddress = null)
    {
        // The method reads the current step and the surface of the web step. The form is shown on the web
        // step of this device, with the email block while the email is being obtained; the phone step waits
        // naming the channel the number is asked in; any other state — the web step on another surface, no
        // step left, a confirmed transaction — waits neutrally. A waiting state of a sign-in still in the
        // completion carries the right to cancel it: the server accepts the cancellation of this device on
        // any step (SPEC-041 L109); a confirmed transaction is past it.
        var language = ResolveLanguage(transaction, requestLanguage);

        // Every form of this page ends on the client's redirect_uri through a chain of redirects — the
        // submission that finishes the sign-in, an action that fails it, "Cancel sign-in" — and the
        // browser applies form-action to that whole chain. So the response allows the validated
        // redirect_uri as a form target (SPEC-007 UI-091), whichever state the page is rendered in. The
        // page of the device that confirmed the sign-in does not end there and keeps form-action 'self'.
        var formActionSource = SecurityHeadersMiddleware.GetFormActionSource(transaction.OidcContext?.RedirectUri);
        if (!string.IsNullOrEmpty(formActionSource))
        {
            httpContext.Items[SecurityHeaderConstants.FormActionTargetItemKey] = formActionSource;
        }
        var step = transaction.GetCurrentClaimCompletionStep();
        var surface = transaction.ClaimCompletion?.Surface;

        if (form is null && step is ClaimCompletionStep.WebStep && surface is WebStepSurface.InitiatingPage)
        {
            form = await BuildFormAsync(httpContext, transaction, language);
        }

        var phoneChannelName = form is null
            && step is ClaimCompletionStep.Phone
            && transaction.ChannelIdentitySnapshot is { } snapshot
                ? ChannelNames.NameChannel(snapshot.ChannelType, _adapters, _localizer, language, _logger)
                : null;

        // The watcher script connects to the hub of the sign-in window, so the client library comes from
        // where the settings of the transaction's own page put it.
        var pageSettings = await _pageSettings.ResolveAsync(
            TransactionResolutionContext.For(transaction), httpContext.RequestAborted);

        var state = new ClaimCompletionPageState
        {
            SessionId = SessionIdMapper.ToSessionId(transaction.Id),
            Step = step,
            Surface = surface,
            Watched = true,
            RemainingMs = PageCountdown.RemainingMs(transaction.ExpiresAt, _timeProvider.GetUtcNow()),
            SignalRClientPath = SignalRClientLocation.Resolve(
                pageSettings.Design.SignalRClientPath, httpContext.Request.PathBase.Value ?? string.Empty),
            Form = form,
            EmailBlock = form is null
                ? null
                : await _emailBlock.BuildViewAsync(
                    transaction,
                    WebStepSurface.InitiatingPage,
                    language,
                    emailError,
                    emailAddressStep,
                    emailEnteredAddress,
                    httpContext.RequestAborted),
            PhoneChannelName = phoneChannelName,
            CancelRightFields = form is null && transaction.IsCompletingClaims()
                ? BuildInitiatingPageRightFields(httpContext, transaction)
                : null
        };

        return await RenderAsync(httpContext, transaction, state, language);
    }

    /// <summary>
    /// Builds the form of the page of the device that confirmed the sign-in with the form token as the right
    /// to act (SPEC-041 N78): the session identifier and the token — no account line, no AntiForgery token.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">Transaction of the page.</param>
    /// <param name="formToken">The form token the page was opened with.</param>
    /// <param name="language">Content language of the page (<see cref="ResolveLanguage"/>).</param>
    /// <returns>The model of the form, fields without values and errors.</returns>
    public async ValueTask<ClaimCompletionFormModel> BuildCrossDeviceFormAsync(
        HttpContext httpContext,
        Transaction transaction,
        string formToken,
        string language)
    {
        var model = await _formModelBuilder.BuildAsync(
            transaction, WebStepSurface.CrossDevicePage, language, httpContext.RequestAborted);

        return model with
        {
            RightFields =
            [
                new(OidcConstants.SessionIdParameterName, SessionIdMapper.ToSessionId(transaction.Id)),
                new(ClaimCompletionFormFields.FormToken, formToken)
            ]
        };
    }

    /// <summary>
    /// Answers with the page of the device that confirmed the sign-in: the form of the web step with the form
    /// token as the right (SPEC-041 L52, N78). Its script does not watch the status: it only serves the email
    /// block, through the hub of the sign-in window. The caller has checked the right.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">Transaction of the page, on the web step of this surface.</param>
    /// <param name="formToken">The form token the page was opened with.</param>
    /// <param name="requestLanguage">Language detected for the current request.</param>
    /// <param name="form">A form to show instead of a fresh one — the refused submission with its values
    /// and errors; null — a fresh form.</param>
    /// <param name="emailError">Localized error of the action of the email block shown again; null — none.</param>
    /// <param name="emailAddressStep">Whether the email block brings the address step of its way back over a
    /// live mail — a mark of this answer only.</param>
    /// <param name="emailEnteredAddress">The address the request typed into the email block, shown again in
    /// the input of its address step; null — the input shows the address of the last mail.</param>
    /// <returns>HTML result with status 200 and <c>Cache-Control: no-store</c>.</returns>
    public async Task<IResult> RespondCrossDeviceAsync(
        HttpContext httpContext,
        Transaction transaction,
        string formToken,
        string requestLanguage,
        ClaimCompletionFormModel? form = null,
        string? emailError = null,
        bool emailAddressStep = false,
        string? emailEnteredAddress = null)
    {
        var language = ResolveLanguage(transaction, requestLanguage);

        // Only an email block still to be obtained may carry the script here, and the script connects to the
        // hub of the sign-in window while the block shows "Check status", so the client library comes from
        // where the settings of the transaction's own page put it, as on the other device. Which step the block
        // shows is known once it is rendered, so the settings are resolved for any block still to be obtained.
        var pageSettings = EmailCompletionBlock.OpenEmailItem(transaction) is null
            ? null
            : await _pageSettings.ResolveAsync(TransactionResolutionContext.For(transaction), httpContext.RequestAborted);

        var state = new ClaimCompletionPageState
        {
            SessionId = SessionIdMapper.ToSessionId(transaction.Id),
            Step = transaction.GetCurrentClaimCompletionStep(),
            Surface = transaction.ClaimCompletion?.Surface,
            Watched = false,
            SignalRClientPath = pageSettings is null
                ? string.Empty
                : SignalRClientLocation.Resolve(
                    pageSettings.Design.SignalRClientPath, httpContext.Request.PathBase.Value ?? string.Empty),
            Form = form ?? await BuildCrossDeviceFormAsync(httpContext, transaction, formToken, language),
            EmailBlock = await _emailBlock.BuildViewAsync(
                transaction,
                WebStepSurface.CrossDevicePage,
                language,
                emailError,
                emailAddressStep,
                emailEnteredAddress,
                httpContext.RequestAborted)
        };

        PreventCaching(httpContext);

        return await RenderAsync(httpContext, transaction, state, language);
    }

    /// <summary>
    /// Answers the load of the page of the device the sign-in was started on with what the action before it
    /// carried over its redirect (<see cref="ShowAgain"/>), if anything.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">Transaction of the page.</param>
    /// <param name="requestLanguage">Language detected for the current request.</param>
    /// <returns>HTML result with status 200.</returns>
    public Task<IResult> RespondCarriedAsync(HttpContext httpContext, Transaction transaction, string requestLanguage)
    {
        var carried = _carry.Take(httpContext, transaction.Id, crossDevice: false);

        return RespondAsync(
            httpContext,
            transaction,
            requestLanguage,
            emailError: carried?.EmailError,
            emailAddressStep: carried?.EmailAddressStep ?? false,
            emailEnteredAddress: carried?.EmailEnteredAddress);
    }

    /// <summary>
    /// Answers the load of the page of the device that confirmed the sign-in with what the action before it
    /// carried over its redirect (<see cref="ShowAgain"/>), if anything. The caller has checked the right.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">Transaction of the page, on the web step of this surface.</param>
    /// <param name="formToken">The form token the page was opened with.</param>
    /// <param name="requestLanguage">Language detected for the current request.</param>
    /// <returns>HTML result with status 200 and <c>Cache-Control: no-store</c>.</returns>
    public Task<IResult> RespondCrossDeviceCarriedAsync(
        HttpContext httpContext,
        Transaction transaction,
        string formToken,
        string requestLanguage)
    {
        var carried = _carry.Take(httpContext, transaction.Id, crossDevice: true);

        return RespondCrossDeviceAsync(
            httpContext,
            transaction,
            formToken,
            requestLanguage,
            emailError: carried?.EmailError,
            emailAddressStep: carried?.EmailAddressStep ?? false,
            emailEnteredAddress: carried?.EmailEnteredAddress);
    }

    /// <summary>
    /// Answers an action of the email block whose page shows more than the transaction itself — an error of
    /// the block, its address step brought back or the address typed into it: post/redirect/get, with that
    /// carried to the page over the redirect, so reloading the page loads it again rather than repeating the
    /// action — no second mail, no second attempt of a code. The values typed into the simple fields are not
    /// carried: the script of the page keeps them in the tab (<see cref="ClaimCompletionPageScript"/>).
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="shown">The transaction as it stands after the action.</param>
    /// <param name="formToken">The form token of the request; null — the page of the initiating device.</param>
    /// <param name="shownAgain">What the page shows besides the transaction.</param>
    /// <returns>An empty 303 result.</returns>
    public IResult ShowAgain(
        HttpContext httpContext,
        Transaction shown,
        string? formToken,
        ClaimCompletionShownAgain shownAgain)
    {
        ArgumentNullException.ThrowIfNull(shown);

        _carry.Issue(httpContext, shown.Id, crossDevice: formToken is not null, shownAgain);

        return ClaimCompletionActionEndpoints.SeeOtherToPage(httpContext, shown.Id, formToken);
    }

    /// <summary>
    /// Answers on the page of the device that confirmed the sign-in with the receipt of an outcome — the
    /// terminal text of the transaction on the page of the core (SPEC-036 TPL-123, SPEC-041 L52). The wording
    /// is asked of the port; the sign-in itself is finished by the browser of the device it was started on.
    /// A channel whose chat is a page of the core (<see cref="IChannelOwnPage"/>) gets no receipt here: the
    /// browser is sent back to that page with <c>303 See Other</c>, for a sign-in and a confirmation alike.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="outcome">The outcome to report.</param>
    /// <param name="transaction">The transaction the receipt belongs to.</param>
    /// <param name="outcomeMoment">Moment the outcome was written down.</param>
    /// <param name="requestLanguage">Language detected for the current request.</param>
    /// <returns>HTML result with status 200 and <c>Cache-Control: no-store</c>, or the 303 back to the page of
    /// the channel.</returns>
    public async Task<IResult> CrossDeviceReceiptAsync(
        HttpContext httpContext,
        TransactionOutcome outcome,
        Transaction transaction,
        DateTimeOffset outcomeMoment,
        string requestLanguage)
    {
        // A channel whose chat is a page of the core gets its browser back to that page, which shows the
        // outcome with the menu sent after it; a receipt here would leave the user out of the chat.
        if (OwnPageOf(transaction) is { } ownPage)
        {
            PreventCaching(httpContext);
            httpContext.Response.Headers.Location = $"{httpContext.Request.PathBase}{ownPage.PagePath()}";

            return Results.StatusCode(StatusCodes.Status303SeeOther);
        }

        // The receipt belongs to the transaction, so its wording and its branding are resolved over the
        // ownership of that transaction, as on the page of the core that answers a confirmation.
        var language = ResolveLanguage(transaction, requestLanguage);
        var ownership = TransactionResolutionContext.For(transaction);

        var text = await _receiptText.RenderAsync(
            new OutcomeReceiptAddress(
                outcome,
                OutcomeReceiptSurfaces.CorePage,
                transaction.Type,
                transaction.ConfirmationSnapshot?.ActionType,
                transaction.ChannelIdentitySnapshot?.ChannelType,
                transaction.InitiatorContextSnapshot?.DeviceType),
            language,
            transaction.GetUiTimeZone(),
            outcomeMoment,
            TransactionSlotSource.From(transaction),
            ownership,
            httpContext.RequestAborted);

        var branding = await _brandingResolver.ResolveAsync(ownership, httpContext.RequestAborted);
        var texts = await _textResolver.ResolveAsync(ownership, httpContext.RequestAborted);
        var html = TransactionConfirmPage.BuildReceipt(
            text,
            _localizer,
            language,
            branding,
            new ReceiptCloseAction(
                OutcomeCloseAction.ApplicationName(
                    transaction, OutcomeCloseAction.OpenedOn(httpContext, _userAgentNormalizer)),
                texts,
                httpContext.Items[SecurityHeaderConstants.CspNonceItemKey] as string));

        PreventCaching(httpContext);

        return Results.Content(html, AuthPageConstants.HtmlContentType, Encoding.UTF8);
    }

    /// <summary>
    /// The adapter of the channel of a transaction when its chat is a page of the core.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <returns>The adapter; <c>null</c> — the channel has no page of its own, or none is known.</returns>
    private IChannelOwnPage? OwnPageOf(Transaction transaction) =>
        transaction.ChannelIdentitySnapshot?.ChannelType is { } channelType
            ? _adapters.FirstOrDefault(adapter => string.Equals(adapter.ChannelType, channelType, StringComparison.Ordinal)) as IChannelOwnPage
            : null;

    /// <summary>
    /// Answers a form token that carries no right with the neutral page of a link that no longer leads
    /// anywhere (<see cref="TransactionEntryUnavailablePage"/>): one answer for a missing transaction, a
    /// foreign token and a transaction the token no longer acts on, so the answer tells nothing about which
    /// of them it is. Branded by the core level, in the language of the request, status 410.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="requestLanguage">Language detected for the current request.</param>
    /// <returns>HTML result with status 410 and <c>Cache-Control: no-store</c>.</returns>
    public async Task<IResult> CrossDeviceLinkUnavailableAsync(HttpContext httpContext, string requestLanguage)
    {
        var branding = await _brandingResolver.ResolveAsync(ResolutionContext.Core, httpContext.RequestAborted);
        var html = TransactionEntryUnavailablePage.Build(_localizer, requestLanguage, branding);

        PreventCaching(httpContext);

        return Results.Content(html, AuthPageConstants.HtmlContentType, Encoding.UTF8, StatusCodes.Status410Gone);
    }

    /// <summary>
    /// Sends the browser of the device that confirmed the sign-in back to its page with <c>303 See Other</c>
    /// — the page shows the transaction as it stands (post/redirect/get).
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="formToken">The form token the request carried.</param>
    /// <returns>An empty 303 result carrying the address of the page, path base included.</returns>
    public static IResult SeeOtherToCrossDevicePage(HttpContext httpContext, TransactionId transactionId, string formToken)
    {
        PreventCaching(httpContext);

        httpContext.Response.Headers.Location =
            $"{httpContext.Request.PathBase}{ClaimCompletionFormPageEndpoint.BuildPath(SessionIdMapper.ToSessionId(transactionId), formToken)}";

        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    /// <summary>
    /// Renders the page of a state in the branding and with the text overrides of its transaction.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">Transaction of the page.</param>
    /// <param name="state">What the page shows.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>HTML result with status 200.</returns>
    private async Task<IResult> RenderAsync(
        HttpContext httpContext,
        Transaction transaction,
        ClaimCompletionPageState state,
        string language)
    {
        // The page belongs to the transaction, so it is branded and worded by the levels of that transaction
        // (SPEC-007 UI-101), as the web confirmation page before it.
        var ownership = TransactionResolutionContext.For(transaction);
        var branding = await _brandingResolver.ResolveAsync(ownership, httpContext.RequestAborted);
        var texts = await _textResolver.ResolveAsync(ownership, httpContext.RequestAborted);

        var html = ClaimCompletionPage.Build(
            state,
            httpContext.Request.PathBase.Value ?? string.Empty,
            httpContext.Items[SecurityHeaderConstants.CspNonceItemKey] as string,
            _localizer,
            texts,
            language,
            branding);

        return Results.Content(html, AuthPageConstants.HtmlContentType, Encoding.UTF8);
    }

    /// <summary>
    /// Forbids every cache to keep the answer: the page of the device that confirmed the sign-in, its
    /// address and the answers to its actions carry the form token.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    private static void PreventCaching(HttpContext httpContext) =>
        httpContext.Response.Headers[HeaderNames.CacheControl] = CacheControlHeaderValue.NoStoreString;

    /// <summary>
    /// Answers with the "session expired" page, branded by the levels of the transaction it concerns — the
    /// page the callback shows a transaction that is no longer answerable. A transaction that is not a
    /// sign-in gets the neutral page of a link that leads nowhere instead, as on every path of the callback.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="requestLanguage">Language detected for the current request.</param>
    /// <returns>HTML result with status 400.</returns>
    public Task<IResult> ExpiredSessionAsync(
        HttpContext httpContext,
        Transaction transaction,
        string requestLanguage) =>
        AuthorizeCallbackEndpoint.ExpiredSessionResultAsync(
            httpContext, transaction, _localizer, requestLanguage, _brandingResolver);

    /// <summary>
    /// Answers with the refusal of the sign-in to the client — the answer the "No" of the web confirmation
    /// page gives (SPEC-041 L109: cancelling the sign-in ends it the same way).
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">The transaction the user cancelled.</param>
    /// <param name="requestLanguage">Language detected for the current request.</param>
    /// <returns>The <c>access_denied</c> answer in the response mode of the request.</returns>
    public Task<IResult> AccessDeniedAsync(
        HttpContext httpContext,
        Transaction transaction,
        string requestLanguage) =>
        AuthorizeCallbackEndpoint.SignInErrorResultAsync(
            httpContext, transaction, OpenIddictConstants.Errors.AccessDenied,
            _localizer, requestLanguage, _veriqaOptions, _brandingResolver, _languageRegistry);
}
