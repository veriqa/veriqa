// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// The entry of the code of a sign-in mail (<c>POST /auth/email/code</c>, SPEC-016 §4): the page the address was
/// typed into posts the code there, and a code that matches does what the link of the same mail does — it is
/// decided by the same branches (<see cref="DecideLetterAsync"/>) and answered as JSON instead of a page.
/// </summary>
public static partial class EmailAuthEndpoints
{
    /// <summary>
    /// Handles the entry of the code of a sign-in mail. The body is JSON: <c>session_id</c> and <c>code</c>.
    /// </summary>
    /// <remarks>
    /// The code is checked by the norms of the code of the claims completion (SPEC-041 R12, E61): compared with
    /// its fingerprint in constant time, one attempt spent per entry by a compare-and-swap, every attempt left
    /// spent by a match. An entry that is not a code, a transaction that is no longer pending and a letter that
    /// is no longer live spend nothing. The letter the code was sent in is the letter decided: its action token
    /// is the key the gate of a letter counts the attempt by (SPEC-016 EM-208), so the code and the link of one
    /// mail are one attempt, and whichever is used first leaves the other without its letter. The code is
    /// neither logged nor answered.
    /// </remarks>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <returns>The JSON answer.</returns>
    private static async Task<IResult> HandleEmailCodeAsync(HttpContext httpContext)
    {
        // The request is one action of the channel, as the POST of the page of the link is: the admission of
        // the sender is decided once for the gate of the letter and the write that follows it.
        using var action = ChannelInboundProcessor.OpenAction();

        var services = httpContext.RequestServices;
        var cancellationToken = httpContext.RequestAborted;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EmailAuthEndpoints));
        var pageLocalizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
        var requestLocale = ChannelRequestLocale.Detect(httpContext.Request.Headers.AcceptLanguage.ToString());

        if (httpContext.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) is not true)
        {
            return CodeRefusal(pageLocalizer, EmailAdapterMailStrings.ApiErrorUnsupportedContentType, requestLocale, StatusCodes.Status415UnsupportedMediaType);
        }

        var body = await ReadJsonBodyAsync(httpContext.Request, cancellationToken);
        var sessionId = JsonStringOf(body, EmailAdapterConstants.SessionIdQueryParam);
        var enteredCode = JsonStringOf(body, EmailPullJsonFields.Code);

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return CodeRefusal(pageLocalizer, EmailAdapterMailStrings.ApiErrorMissingSessionId, requestLocale, StatusCodes.Status400BadRequest);
        }

        if (ParseTransactionId(sessionId) is not { } transactionId)
        {
            return CodeRefusal(pageLocalizer, EmailAdapterMailStrings.ApiErrorInvalidSessionId, requestLocale, StatusCodes.Status400BadRequest);
        }

        // The guards of the start, in its order: the channel and its Pull mode at the core level, the
        // transaction, and the choice of its tenant and application.
        if (TryReadEmailOptions(services, logger, transactionId) is not { Enabled: true, PullEnabled: true })
        {
            return CodeRefusal(pageLocalizer, EmailAdapterMailStrings.ApiErrorChannelUnavailable, requestLocale, StatusCodes.Status503ServiceUnavailable);
        }

        var txResult = await services.GetRequiredService<ITransactionService>().GetTransactionAsync(transactionId, cancellationToken);
        if (txResult.IsFailure)
        {
            return CodeRefusal(pageLocalizer, EmailAdapterMailStrings.ApiErrorSessionInvalid, requestLocale, StatusCodes.Status400BadRequest);
        }

        var transaction = txResult.Value;
        var locale = PreferStoredLocale(transaction, requestLocale);

        using var tenantScope = ChannelTenantContext.BeginScope(transaction.GetTenantId());

        var signInModes = await services.GetRequiredService<IEmailSignInModes>()
            .ResolveAsync(TransactionResolutionContext.For(transaction), cancellationToken);

        var store = services.GetRequiredService<IEmailClaimCompletionStore>();
        var codeTransactionId = transactionId.ToString();

        // Code left out of the pull methods is answered as Pull switched off — unless a code is still live in
        // the transaction: a mail sent before the methods changed keeps its code until it expires.
        if (!signInModes.Pull
            || (!signInModes.PullCarriesCode && (await store.GetAsync(codeTransactionId, cancellationToken))?.SignInCode is null))
        {
            logger.LogWarning(
                "A code of a sign-in mail was entered, but the transaction is offered no code by its sign-in modes. TransactionId: {TransactionId}",
                transactionId);

            return CodeRefusal(pageLocalizer, EmailAdapterMailStrings.ApiErrorChannelUnavailable, locale, StatusCodes.Status503ServiceUnavailable);
        }

        if (!ClaimEmailCode.TryClean(enteredCode, out var code))
        {
            return CodeRefusal(pageLocalizer, EmailAdapterMailStrings.ApiErrorCodeShape, locale, StatusCodes.Status400BadRequest);
        }

        if (transaction.State is not TransactionState.Pending)
        {
            return CodeRefusal(pageLocalizer, EmailAdapterMailStrings.ApiErrorSessionAlreadyProcessed, locale, StatusCodes.Status409Conflict);
        }

        SignInCodeCheck check;
        try
        {
            check = await CheckSignInCodeAsync(services, store, codeTransactionId, code, logger, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // As the check of the code of the claims completion: a code that cannot be checked is no code.
            logger.LogWarning(
                ex,
                "A code of a sign-in mail could not be checked. TransactionId: {TransactionId}",
                transactionId);
            check = new SignInCodeCheck(ClaimEmailCodeStatus.NoCode);
        }

        if (check is not { Status: ClaimEmailCodeStatus.Matched, Token: { } token, TokenData: { } tokenData })
        {
            return check.Status switch
            {
                ClaimEmailCodeStatus.Mismatch => Results.Json(
                    new EmailCodeMismatch(
                        Success: false,
                        Error: pageLocalizer.ResolveOrBaseText(EmailAdapterMailStrings.ApiErrorWrongCode, locale)
                            .Replace(
                                EmailAdapterMailStrings.ApiErrorAttemptsSlot,
                                check.AttemptsLeft.ToString(CultureInfo.InvariantCulture),
                                StringComparison.Ordinal),
                        AttemptsLeft: check.AttemptsLeft),
                    statusCode: StatusCodes.Status400BadRequest),
                ClaimEmailCodeStatus.Exhausted => CodeRefusal(
                    pageLocalizer, EmailAdapterMailStrings.ApiErrorCodeExhausted, locale, StatusCodes.Status409Conflict),
                ClaimEmailCodeStatus.Expired => CodeRefusal(
                    pageLocalizer, EmailAdapterMailStrings.ApiErrorCodeExpired, locale, StatusCodes.Status409Conflict),
                _ => CodeRefusal(
                    pageLocalizer, EmailAdapterMailStrings.ApiErrorNoCode, locale, StatusCodes.Status409Conflict)
            };
        }

        // From here on the code is spent, and the letter it was sent in is decided as its link would be.
        var letter = await ReadLetterPageAsync(httpContext, tokenData);

        if (EmailAdapterOf(services) is not { } adapter)
        {
            logger.LogError(
                "Email adapter not found in DI while handling a code of a sign-in mail. TransactionId: {TransactionId}",
                transactionId);

            return CodeRefusal(pageLocalizer, EmailAdapterMailStrings.ConfirmPageError, locale, StatusCodes.Status500InternalServerError);
        }

        return await RenderCodeAnswerAsync(
            httpContext,
            letter,
            await DecideLetterAsync(httpContext, token, tokenData, letter, adapter));
    }

    /// <summary>
    /// Checks an entered code against the live code of the sign-in mail of a transaction and spends its attempt
    /// by the check the code of the claims completion goes through (<see cref="EmailCodeCheck"/>). A code whose
    /// letter is no longer live — answered by its link, past its deadline — is no code, and nothing is spent.
    /// </summary>
    /// <param name="services">The request service provider.</param>
    /// <param name="store">The store the code is kept in.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="code">The entered code, in the shape of a code.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome of the check; on a match — the letter of the code.</returns>
    private static async Task<SignInCodeCheck> CheckSignInCodeAsync(
        IServiceProvider services,
        IEmailClaimCompletionStore store,
        string transactionId,
        string code,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // The letter of the code is read once a round, before the attempt is spent: a match hands over the data
        // read in the round that matched.
        EmailActionToken? tokenData = null;

        var check = await EmailCodeCheck.RunAsync(
            transactionId,
            code,
            async ct => (await store.GetAsync(transactionId, ct))?.SignInCode,
            async (live, now, ct) =>
            {
                tokenData = await ReadActionTokenAsync(services, live.LetterToken, ct);
                return IsLiveToken(live.LetterToken, tokenData, now);
            },
            (live, matched, ct) => store.TrySpendSignInCodeAttemptAsync(transactionId, live.Hash, live.AttemptsLeft, matched, ct),
            services.GetRequiredService<TimeProvider>(),
            logger,
            cancellationToken);

        return check.Status switch
        {
            ClaimEmailCodeStatus.Matched => new SignInCodeCheck(check.Status, check.Code?.LetterToken, tokenData),
            ClaimEmailCodeStatus.Mismatch => new SignInCodeCheck(check.Status, AttemptsLeft: check.AttemptsLeft),
            _ => new SignInCodeCheck(check.Status)
        };
    }

    /// <summary>
    /// Answers the entry of a code with what its letter came to — the same outcome the page of the link renders
    /// (<see cref="RenderLetterPageAsync"/>), said as JSON: where the page goes by itself (<c>next_url</c>), or
    /// the line the page of the link shows for the same outcome (<c>message</c>) when the page stays and follows
    /// the status of the transaction.
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="letter">What the pages of the letter are rendered from.</param>
    /// <param name="outcome">What the letter came to.</param>
    /// <returns>The JSON answer.</returns>
    private static async Task<IResult> RenderCodeAnswerAsync(
        HttpContext httpContext,
        EmailLetterPage letter,
        EmailLetterOutcome outcome)
    {
        var services = httpContext.RequestServices;
        var pageLocalizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
        var htmlLang = ResolveServedHtmlLang(pageLocalizer, letter.Locale);

        return outcome switch
        {
            // The text of the refusal is the one the page of the link shows for it. A refusal of the gate is a
            // conflict with the state of the transaction; a failure of the server stays one.
            EmailLetterOutcome.Refused refused => Results.Json(
                new EmailCodeRefused(
                    Success: false,
                    Error: letter.Texts.Localize(
                        pageLocalizer,
                        refused.LetterUsed ? EmailAdapterMailStrings.ConfirmPageTokenUsed : EmailAdapterMailStrings.ConfirmPageError,
                        htmlLang)),
                statusCode: refused.StatusCode >= StatusCodes.Status500InternalServerError
                    ? refused.StatusCode
                    : StatusCodes.Status409Conflict),
            EmailLetterOutcome.SignInContinued => CodeAccepted(
                nextUrl: null,
                message: letter.Texts.Localize(pageLocalizer, EmailAdapterMailStrings.SignInRequestContinued, htmlLang)),
            EmailLetterOutcome.Completed completed => CodeAccepted(nextUrl: null, message: completed.ReceiptText),

            // The browser the code was entered in is the device that confirmed the sign-in (SPEC-041 N78), so the
            // form opens in it. CancellationToken.None: the letter is spent — the form must be issued even if
            // the browser goes away, the way the confirmation itself is finished.
            EmailLetterOutcome.ClaimFormOnThisDevice form =>
                (services.GetService<IClaimCompletionCrossDevicePage>() is { } crossDevicePage
                    ? await crossDevicePage.TryIssueFormPathAsync(httpContext, form.TransactionId, CancellationToken.None)
                    : null) is { } formPath
                    ? CodeAccepted(nextUrl: formPath, message: null)
                    : CodeAccepted(nextUrl: null, message: StartDeviceText(pageLocalizer, letter)),
            EmailLetterOutcome.ContinueOnStartDevice => CodeAccepted(nextUrl: null, message: StartDeviceText(pageLocalizer, letter)),
            EmailLetterOutcome.HandedOverToQuestion handedOver => CodeAccepted(
                nextUrl: QuestionPath(httpContext, handedOver.TransactionId),
                message: null),

            // The "Continue" page is the fast link's alone: a code is never the GET of a link.
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not an outcome of a code.")
        };
    }

    /// <summary>
    /// The answer of an accepted code.
    /// </summary>
    /// <param name="nextUrl">Where the page goes by itself; null — it stays.</param>
    /// <param name="message">The line the page shows when it stays; null — none.</param>
    /// <returns>The JSON answer with status 200.</returns>
    private static IResult CodeAccepted(string? nextUrl, string? message) =>
        Results.Json(new EmailCodeAccepted(Success: true, NextUrl: nextUrl, Message: message));

    /// <summary>
    /// The refusal of an entry of a code, worded in the language of the answer.
    /// </summary>
    /// <param name="pageLocalizer">Localizer of the texts.</param>
    /// <param name="errorKey">The text of the refusal (a Natural Key).</param>
    /// <param name="locale">Language of the answer; null — the base language.</param>
    /// <param name="statusCode">The status of the answer.</param>
    /// <returns>The JSON answer.</returns>
    private static IResult CodeRefusal(
        IConfirmationPromptLocalizer pageLocalizer,
        string errorKey,
        string? locale,
        int statusCode) =>
        Results.Json(
            new EmailCodeRefused(Success: false, Error: ResolveServedText(pageLocalizer, errorKey, locale)),
            statusCode: statusCode);

    /// <summary>
    /// Reads the JSON body of a request. A body that is not JSON is read as no body: the members it lacks are
    /// then refused by the checks of the request with 400, as an absent member is, instead of failing it.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The root of the body; undefined — the body is not JSON.</returns>
    private static async Task<JsonElement> ReadJsonBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);

            return body.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// A string member of a JSON object; anything else is no value.
    /// </summary>
    /// <param name="root">The object.</param>
    /// <param name="name">Name of the member.</param>
    /// <returns>The string, or null.</returns>
    private static string? JsonStringOf(JsonElement root, string name) =>
        root.ValueKind is JsonValueKind.Object
        && root.TryGetProperty(name, out var element)
        && element.ValueKind is JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>
    /// What a check of a code came to.
    /// </summary>
    /// <param name="Status">The outcome of the check.</param>
    /// <param name="Token">On a match — the action token of the letter the code was sent in.</param>
    /// <param name="TokenData">On a match — the data of that token, live.</param>
    /// <param name="AttemptsLeft">On a mismatch — the attempts left of the code.</param>
    private sealed record SignInCodeCheck(
        ClaimEmailCodeStatus Status,
        string? Token = null,
        EmailActionToken? TokenData = null,
        int AttemptsLeft = 0);

    /// <summary>
    /// The answer of an accepted code.
    /// </summary>
    /// <param name="Success">Always <c>true</c>.</param>
    /// <param name="NextUrl">Where the page goes by itself; null — it stays and follows the status of the transaction.</param>
    /// <param name="Message">The line the page shows when it stays; null — none.</param>
    private sealed record EmailCodeAccepted(
        [property: JsonPropertyName(EmailPullJsonFields.Success)] bool Success,
        [property: JsonPropertyName(EmailPullJsonFields.NextUrl)] string? NextUrl,
        [property: JsonPropertyName(EmailPullJsonFields.Message)] string? Message);

    /// <summary>
    /// The refusal of an entry of a code.
    /// </summary>
    /// <param name="Success">Always <c>false</c>.</param>
    /// <param name="Error">The text of the refusal, localized.</param>
    private sealed record EmailCodeRefused(
        [property: JsonPropertyName(EmailPullJsonFields.Success)] bool Success,
        [property: JsonPropertyName(EmailPullJsonFields.Error)] string Error);

    /// <summary>
    /// The answer of a wrong code.
    /// </summary>
    /// <param name="Success">Always <c>false</c>.</param>
    /// <param name="Error">The text of the refusal, localized.</param>
    /// <param name="AttemptsLeft">Attempts left of the code.</param>
    private sealed record EmailCodeMismatch(
        [property: JsonPropertyName(EmailPullJsonFields.Success)] bool Success,
        [property: JsonPropertyName(EmailPullJsonFields.Error)] string Error,
        [property: JsonPropertyName(EmailPullJsonFields.AttemptsLeft)] int AttemptsLeft);
}
