// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Hubs;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// The "Yes"/"No" question page of the core (SPEC-007 UI-123), one page for both types of transaction: the
/// question of a sign-in on the <c>OnWebPage</c> surface (SPEC-007 UI-038), served by the callback, and the
/// question of a confirmation transaction (SPEC-039 R37), served by its own route. The page asks a question
/// and offers a real choice: "Yes" posts to the confirming route of its type, "No" to the declining one. Both
/// carry the AntiForgery token and session_id. Until the user answers, nothing is decided (GET decides
/// nothing).
/// <para>
/// "No" is the default answer and the more visible one: the main button of the contour, filled with the
/// primary color and focused when the page opens, so Enter without moving the focus answers "No"; "Yes" is
/// the outlined button. The order of the two stays "Yes", then "No", and is the same for both types.
/// </para>
/// <para>
/// A single "Continue" button was not a confirmation: it offered no way to refuse, so the user could
/// only acknowledge a decision that was already made. The decline branch is what makes this page a
/// confirmation surface rather than an interstitial.
/// </para>
/// <para>
/// By the type of the transaction only the words that name the action, the routes of the two answers and the
/// address the watcher leaves for differ (<see cref="WebConfirmPageVariant"/>). The routes and the address are
/// OIDC mechanics: a sign-in is answered and ended on the callback, a confirmation has no callback (SPEC-039
/// C19) and its page answers on its own address.
/// </para>
/// <para>
/// The page names no channel account of its own: the account is named only where the text of the question
/// names it, through the masked identity slots of the prompt, as in the chat.
/// </para>
/// <para>
/// The question itself is worded for both types by one method (<see cref="ResolveQuestionAsync"/>): the
/// confirmation prompt the chat would carry, asked at the surface of the core page, which a sign-in words from
/// its initiator context and a confirmation answers with its subject. The title and the two answers are texts
/// an owning level may override (SPEC-012 CFG-254), resolved over the page's own transaction.
/// </para>
/// <para>
/// While the deadline of the transaction still decides it the page counts down the time left and watches
/// the status as the page of the claims completion does, with the same script and the same countdown
/// (<see cref="ClaimCompletionPageScript"/>, <see cref="PageCountdown"/>). It is rendered for no step of
/// the completion: a finished or vanished transaction and a step of the completion that appears take the
/// browser once to the address of its variant, and the page there names the outcome. The watcher adds
/// nothing to what the holder of the page already had: the session_id is in the address and in the forms,
/// the remainder and the status are what the status surface and the group of the hub give, and the answer
/// stays an explicit post of the user.
/// </para>
/// <para>
/// A sign-in a channel completed before the question — by its sign-in confirmation event
/// (<c>AuthConfirm</c>), which the HTTP channel sends; "Yes" then issues the code — is shown in
/// <c>Completed</c>: a terminal state its deadline does not end. Such a page goes out with the watcher and
/// without the countdown (<see cref="WebConfirmPageWatch"/>): <c>Completed</c> is the state it was rendered
/// in, so that state alone changes nothing, and the page goes to the callback once the status reports the
/// authorization code issued, another state or a vanished transaction. A page brought back after "Yes" by the
/// back button thus leaves for the "session expired" page instead of offering a live question.
/// </para>
/// </summary>
internal static class WebConfirmPage
{
    /// <summary>
    /// Page title of the question of a sign-in.
    /// </summary>
    private const string SignInPageTitle = "Confirm your sign-in";

    /// <summary>
    /// Confirmation button text.
    /// </summary>
    private const string ConfirmButtonText = "Yes";

    /// <summary>
    /// Decline button text.
    /// </summary>
    private const string DeclineButtonText = "No";

    /// <summary>
    /// What separates the paragraphs of the question once its line endings are normalized to
    /// <see cref="Environment.NewLine"/>: a blank line, the one the prompt sets its anomaly note apart with
    /// (ICC-053).
    /// </summary>
    private static readonly string ParagraphSeparator = Environment.NewLine + Environment.NewLine;

    /// <summary>
    /// Texts of the page an owning level may override (SPEC-012 CFG-254) — what the page contributes to the
    /// open list of overridable texts: the two titles and the two answers. The question is a message of the
    /// template mechanism and is reworded there.
    /// </summary>
    internal static IReadOnlyList<string> OverridableTexts { get; } =
        [SignInPageTitle, TransactionConfirmPage.PageTitle, ConfirmButtonText, DeclineButtonText];

    /// <summary>
    /// The question of a sign-in: its title, the routes of the callback and the callback as the address to
    /// leave for.
    /// </summary>
    /// <param name="sessionId">Session identifier of the sign-in.</param>
    /// <param name="question">The question, worded by <see cref="ResolveQuestionAsync"/>.</param>
    /// <returns>The variant of the page.</returns>
    public static WebConfirmPageVariant ForSignIn(string sessionId, string question) =>
        new(
            IsSignIn: true,
            Question: question,
            ConfirmPath: OidcEndpoints.AuthorizeCallbackConfirm,
            DeclinePath: OidcEndpoints.AuthorizeCallbackDecline,
            LeavePath: TransactionStatusMessageFactory.BuildCallbackUrl(sessionId));

    /// <summary>
    /// The question of a confirmation transaction: the neutral title, the question worded from the subject
    /// of the transaction (<see cref="ResolveQuestionAsync"/>), the routes of its own page and its own address
    /// to leave for — answered there by the rules of that page (SPEC-039 C15, R51).
    /// </summary>
    /// <remarks>
    /// The subject arrives as PLAIN text and is HTML-encoded by the page — that, and only that, is this
    /// page's share of the render protections: the substitution, the sanitizing and the degradation
    /// between wording variants all happened inside the engine that produced
    /// <paramref name="question"/>, and a second render here would be a second place to keep them
    /// correct (SPEC-039 R8).
    /// </remarks>
    /// <param name="transaction">The confirmation transaction.</param>
    /// <param name="question">The question, already worded and localized.</param>
    /// <returns>The variant of the page.</returns>
    public static WebConfirmPageVariant ForConfirmation(Transaction transaction, string question) =>
        new(
            IsSignIn: false,
            Question: question,
            ConfirmPath: OidcEndpoints.TransactionConfirmAccept,
            DeclinePath: OidcEndpoints.TransactionConfirmDecline,
            LeavePath: TransactionPageContextBuilder.ConfirmationQuestionUrl(transaction)
                ?? throw new ArgumentException("The question of a confirmation addresses a confirmation transaction.", nameof(transaction)));

    /// <summary>
    /// Words the question of the page for either type of transaction (SPEC-007 UI-123) — the one place the
    /// question is worded: the context of the confirmation prompt the factory builds, the prompt resolved at
    /// the surface of the core page and the channel of the snapshot over the ownership the context carries
    /// (SPEC-036 TPL-116), and the shared render. A sign-in is worded from its initiator context, or with the
    /// default text of the prompt when that context is suppressed; a confirmation is its subject, as the
    /// render would return it whatever the prompt, so the prompt is not resolved for it. Without a step of the
    /// prompt declared for the core page the ladder answers with the wording the chat carries.
    /// </summary>
    /// <remarks>
    /// A sign-in whose context cannot be built or whose prompt cannot be resolved does not fail the page: it
    /// asks with the default text of the prompt, and the failure is logged. A confirmation whose subject cannot
    /// be worded is not asked at all: no sign-in text may stand in for a subject (SPEC-039 E28), so the caller
    /// ends it instead.
    /// </remarks>
    /// <param name="httpContext">Current request — the source of the services and of the log.</param>
    /// <param name="transaction">The transaction the page asks about.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="language">Language of the page.</param>
    /// <returns>The question as plain text, its paragraphs separated by a blank line; null only for a
    /// confirmation whose subject could not be worded.</returns>
    public static async Task<string?> ResolveQuestionAsync(
        HttpContext httpContext,
        Transaction transaction,
        IConfirmationPromptLocalizer localizer,
        string language)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(transaction);

        var services = httpContext.RequestServices;
        var cancellationToken = httpContext.RequestAborted;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WebConfirmPage));
        var channelType = transaction.ChannelIdentitySnapshot?.ChannelType;

        var isConfirmation = string.Equals(transaction.Type, TransactionTypes.Confirmation, StringComparison.Ordinal);

        // The one asked is the account of the snapshot the channel attached, as it is for the chat: the
        // channel names the channel in the text, the snapshot fills its masked identity slots.
        ConfirmationPromptContext context;
        try
        {
            context = await services.GetRequiredService<IConfirmationPromptContextFactory>().CreateAsync(
                transaction,
                ConfirmationSubjectText.RecipientOf(transaction),
                language,
                MessageSurfaces.CorePage,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!isConfirmation)
        {
            // A sign-in whose initiator context cannot be built is asked as a suppressed context is: with
            // the default text. The failure of a confirmation is not caught: its subject is not guessed, and a
            // failure of the infrastructure must not end it as a subject that cannot be worded (SPEC-039 E28).
            logger.LogWarning(
                exception,
                "The confirmation context of the question page could not be built; the page asks with the "
                + "default text of the prompt.");

            return localizer.ResolveOrBaseText(MessageTemplateNaturalKeys.PromptDefault, language);
        }

        // A confirmation is its subject, whatever the prompt resolved at the surface would say: the render
        // returns the subject as it is, so the prompt is not resolved for it at all.
        if (isConfirmation)
        {
            return context is SubjectConfirmationPromptContext subject ? subject.PromptText : null;
        }

        ResolvedMessage prompt;
        try
        {
            prompt = await services.GetRequiredService<IMessageTemplateAccessor>().RequireAsync(
                TransactionMessageAddress.Of(
                    MessageKinds.ConfirmationPrompt, MessageSurfaces.CorePage, transaction, channelType),
                context.Ownership,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "The confirmation prompt of the question page could not be resolved; the page asks with the "
                + "default text of the prompt.");

            return localizer.ResolveOrBaseText(MessageTemplateNaturalKeys.PromptDefault, context.RecipientLocale);
        }

        return ConfirmationPromptRenderer.Render(
            context, MessageTemplateNaturalKeys.PromptDefault, prompt, localizer, logger);
    }

    /// <summary>
    /// Builds the question page HTML. All dynamic values are HTML-encoded. The page texts are
    /// resolved data-driven from the host locale files via <see cref="AuthPageLocalization"/> —
    /// the same rule and the same source the sign-in window uses; the English base text (the key)
    /// is the fallback. The title and the two answers take the overrides an owning level states
    /// (<paramref name="texts"/>). Each paragraph of the question — the parts a blank line separates,
    /// such as the anomaly note under the prompt (ICC-053) — is a paragraph of its own.
    /// </summary>
    /// <param name="variant">What of the page depends on the type of the transaction.</param>
    /// <param name="sessionId">Transaction session_id (carried into the POST for re-validation and, when the
    /// page is watched, watched by its script).</param>
    /// <param name="antiforgeryFormFieldName">AntiForgery token field name.</param>
    /// <param name="antiforgeryToken">AntiForgery request token value.</param>
    /// <param name="pathBase">Path base the application is hosted under (empty — the site root).</param>
    /// <param name="cspNonce">CSP nonce of this request; without one the scripts go out without the
    /// attribute, as every inline script of the contour does.</param>
    /// <param name="watch">The watcher of the page and, while the deadline decides the transaction, its
    /// countdown; null — the page shows the question and the buttons alone.</param>
    /// <param name="localizer">Locale-file localizer (Natural Key → localized text).</param>
    /// <param name="language">Detected language code (ru, en, zh, …).</param>
    /// <param name="texts">Text overrides of the page, already resolved by the caller over the context of
    /// the page's own transaction, like the branding (SPEC-012 CFG-254).</param>
    /// <param name="branding">Effective branding, already resolved over the context of the page's own
    /// transaction (SPEC-007 UI-054, UI-101).</param>
    /// <returns>HTML page.</returns>
    public static string Build(
        WebConfirmPageVariant variant,
        string sessionId,
        string? antiforgeryFormFieldName,
        string? antiforgeryToken,
        string pathBase,
        string? cspNonce,
        WebConfirmPageWatch? watch,
        IConfirmationPromptLocalizer localizer,
        string language,
        CorePageTexts texts,
        CorePageBranding? branding)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(texts);

        // WCAG 3.1.1: the html lang attribute must match the language of the rendered text. The
        // requested locale is honoured only if the localizer actually resolves it — otherwise the
        // page degrades to the English base text and lang must be "en", not the requested code.
        // Resolved once and used for lang and for every string: the page is either fully in the
        // requested language or fully on the English base — never mixed. The probe asks the locale file
        // about the ORIGINAL key: an override does not decide the language.
        var titleKey = variant.IsSignIn ? SignInPageTitle : TransactionConfirmPage.PageTitle;
        var contentLanguage = AuthPageLocalization.ResolveContentLanguage(localizer, titleKey, language);
        var pageTitle = texts.Localize(localizer, titleKey, contentLanguage);
        var questionParagraphs = BuildQuestionParagraphs(variant.Question, language, contentLanguage);
        var confirmButtonText = texts.Localize(localizer, ConfirmButtonText, contentLanguage);
        var declineButtonText = texts.Localize(localizer, DeclineButtonText, contentLanguage);

        var encodedSessionId = WebUtility.HtmlEncode(sessionId);
        var encodedFieldName = WebUtility.HtmlEncode(antiforgeryFormFieldName ?? string.Empty);
        var encodedToken = WebUtility.HtmlEncode(antiforgeryToken ?? string.Empty);
        // The form targets carry the path base: this application may be hosted under a sub-path, and
        // a root-relative action would post past it.
        var encodedAction = WebUtility.HtmlEncode(pathBase + variant.ConfirmPath);
        var encodedDeclineAction = WebUtility.HtmlEncode(pathBase + variant.DeclinePath);
        var encodedSessionParam = WebUtility.HtmlEncode(OidcConstants.SessionIdParameterName);

        // The page is rendered for no step and no surface of the claims completion: any step that appears
        // is a change, and the page at the address of the variant shows it. The state it is rendered in tells
        // the watcher what is not a change. The element, the rules and the script of the countdown go out
        // together or not at all — with a remainder only.
        var countsDown = watch?.RemainingMs is not null;
        var countdownCss = countsDown ? PageCountdown.Css : null;
        var countdownMarkup = countsDown ? PageCountdown.Markup : string.Empty;
        var watcher = watch is null
            ? string.Empty
            : ClaimCompletionPageScript.BuildWatcher(
                ClaimCompletionPageScript.BuildConfig(
                    sessionId, pathBase, watch.RemainingMs, renderedStep: null, renderedSurface: null) with
                {
                    RenderedState = watch.RenderedState,
                    CallbackUrl = variant.LeavePath,
                },
                watch.SignalRClientPath,
                cspNonce);

        return $$"""
            <!DOCTYPE html>
            <html lang="{{contentLanguage}}" {{CorePageHead.BuildThemeAttribute(branding)}}>
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <meta name="robots" content="noindex" />
              <title>{{WebUtility.HtmlEncode(pageTitle)}}</title>
              {{CorePageHead.BuildServicePageHead(branding, countdownCss)}}
            </head>
            <body>
              <main class="veriqa-card">
                <h1 class="veriqa-title">{{WebUtility.HtmlEncode(pageTitle)}}</h1>
                {{questionParagraphs}}
                <div class="veriqa-actions">
                  <form method="post" action="{{encodedAction}}">
                    <input type="hidden" name="{{encodedSessionParam}}" value="{{encodedSessionId}}" />
                    <input type="hidden" name="{{encodedFieldName}}" value="{{encodedToken}}" />
                    <button type="submit" class="veriqa-btn veriqa-btn-decline">{{WebUtility.HtmlEncode(confirmButtonText)}}</button>
                  </form>
                  <form method="post" action="{{encodedDeclineAction}}">
                    <input type="hidden" name="{{encodedSessionParam}}" value="{{encodedSessionId}}" />
                    <input type="hidden" name="{{encodedFieldName}}" value="{{encodedToken}}" />
                    <button type="submit" class="veriqa-btn" autofocus>{{WebUtility.HtmlEncode(declineButtonText)}}</button>
                  </form>
                </div>
                {{countdownMarkup}}
              </main>
              {{CorePageAttribution.BuildLine(contentLanguage)}}
              {{watcher}}
              {{CorePageDefaultAnswerFocus.Script(cspNonce)}}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Builds what the question page watches and counts down with: the state it is rendered in, the client
    /// library of the hub from where the settings of the transaction's own page put it and, for a transaction
    /// its deadline still decides (<see cref="Transaction.IsSubjectToExpiry"/>), the remainder of the TTL. A
    /// <c>Completed</c> sign-in — its code not issued yet, the validation of the callback lets no other
    /// through — is watched without the remainder: it has no time to count, but the code "Yes" issues is what
    /// ends the page. None for any other state.
    /// </summary>
    /// <param name="httpContext">Current request.</param>
    /// <param name="transaction">The transaction the page is shown for.</param>
    /// <param name="pageSettings">Resolver of the settings of the transaction's page.</param>
    /// <param name="pathBase">Path base the application is hosted under.</param>
    /// <param name="timeProvider">Clock the remainder is counted by.</param>
    /// <returns>The watch of the page; null — the page goes out without it.</returns>
    public static async Task<WebConfirmPageWatch?> ResolveWatchAsync(
        HttpContext httpContext,
        Transaction transaction,
        AuthPageSettingsResolver pageSettings,
        string pathBase,
        TimeProvider timeProvider)
    {
        var countsDown = transaction.IsSubjectToExpiry();
        if (!countsDown && transaction.State is not TransactionState.Completed)
        {
            return null;
        }

        var settings = await pageSettings.ResolveAsync(
            TransactionResolutionContext.For(transaction), httpContext.RequestAborted);

        return new WebConfirmPageWatch(
            RemainingMs: countsDown
                ? PageCountdown.RemainingMs(transaction.ExpiresAt, timeProvider.GetUtcNow())
                : null,
            RenderedState: transaction.State.ToString(),
            SignalRClientPath: SignalRClientLocation.Resolve(settings.Design.SignalRClientPath, pathBase));
    }

    /// <summary>
    /// Builds the paragraphs of the question: each part of the text a blank line separates is a paragraph of
    /// its own, so a note the prompt sets apart under the question (ICC-053) stands as its own paragraph,
    /// whatever the line endings the text was worded with.
    /// </summary>
    /// <remarks>
    /// The question is worded in the language the page was asked in, while the page's own texts fall back to
    /// the English base text as a whole when the locale files do not carry them. So when the two languages
    /// differ, the paragraphs state their own <c>lang</c> (WCAG 3.1.2) — as the line of the subject does on
    /// the other pages of the contour (<see cref="ConfirmationSubjectText.Markup"/>).
    /// </remarks>
    /// <param name="question">The question as plain text.</param>
    /// <param name="questionLanguage">Language the question was worded in.</param>
    /// <param name="pageLanguage">Language of the page's own texts (the <c>lang</c> of its root).</param>
    /// <returns>The encoded paragraphs.</returns>
    private static string BuildQuestionParagraphs(string question, string questionLanguage, string pageLanguage)
    {
        var paragraphs = question
            .ReplaceLineEndings()
            .Split(ParagraphSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var languageAttribute = string.Equals(questionLanguage, pageLanguage, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" lang=\"{WebUtility.HtmlEncode(questionLanguage)}\"";

        return string.Join(
            Environment.NewLine,
            paragraphs.Select(paragraph =>
                $"<p class=\"veriqa-instruction\"{languageAttribute}>{WebUtility.HtmlEncode(paragraph)}</p>"));
    }
}
