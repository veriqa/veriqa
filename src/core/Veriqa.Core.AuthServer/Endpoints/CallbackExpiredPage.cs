// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Friendly "sign-in session expired" page shown by the authorization callback when the transaction
/// is no longer valid for sign-in — it was not found (cleaned up after its retention window) or is not
/// in the Completed state. In product mode this replaces a raw RFC ProblemDetails 400 ("Transaction not
/// found"), which read as a bare core error to the end user (e.g. answering/refreshing the
/// web-confirmation page after the completed transaction was reaped, or opening a stale callback link).
///
/// The page always carries exactly one action under its message (SPEC-007 UI-109). Where the
/// application of the transaction publishes an admissible login-initiation address
/// (<c>initiate_login_uri</c>, <see cref="InitiateLoginUriRule"/>), the action is a link to it, under the
/// caption the expired sign-in window uses for the same way out. Otherwise — no address is stated, the
/// stated one is refused, or the transaction was not found and its application is unknown — it is a
/// "Close" button (<see cref="CorePageCloseAction"/>). The request that
/// produced the sign-in is never offered again here: the callback does not have it, and it is neither
/// stored on the transaction nor rebuilt from the OIDC parameters. Standalone core page that shares
/// the service-page stylesheet and branding of the contour with <see cref="WebConfirmPage"/>. Its
/// texts are subject to the text overrides of the levels of the transaction (SPEC-012 CFG-254), like
/// the texts of the sign-in window: an owning level may replace any of them.
/// </summary>
internal static class CallbackExpiredPage
{
    /// <summary>
    /// Page title Natural Key.
    /// </summary>
    private const string PageTitle = "Sign-in session expired";

    /// <summary>
    /// Body-message Natural Key.
    /// </summary>
    private const string Message =
        "This sign-in link has expired or was already used. Please start the sign-in again from the application.";

    /// <summary>
    /// Texts of the page an owning level may override (SPEC-012 CFG-254) — what the page contributes to
    /// the open list of overridable texts, the texts of its button that closes the window included. The
    /// caption of the link to the application (<see cref="AuthPageStrings.RestartSignIn"/>) is part of the
    /// list already, contributed by the sign-in window.
    /// </summary>
    internal static IReadOnlyList<string> OverridableTexts { get; } =
        [PageTitle, Message, .. CorePageCloseAction.OverridableTexts];

    /// <summary>
    /// Builds the expired-session page HTML, localized into the recipient's language (the English base
    /// text — the key — is the fallback; the page is either fully in the requested language or fully
    /// in English, never mixed, so the html lang attribute stays truthful, WCAG 3.1.1).
    /// </summary>
    /// <param name="localizer">Locale-file localizer (Natural Key → localized text).</param>
    /// <param name="language">Detected language code (ru, en, zh, …).</param>
    /// <param name="texts">Text overrides of the page, already resolved by the caller over the same
    /// context as the branding; <see cref="CorePageTexts.None"/> where there is no context to resolve
    /// against. An override replaces a text and is output HTML-encoded, like the text it replaces.</param>
    /// <param name="branding">Effective branding, already resolved by the caller. On a FOUND
    /// transaction it carries that transaction's own levels (SPEC-007 UI-101); on a transaction that
    /// was not found there is no context to resolve against and the branding is the global one, which
    /// is the norm here rather than a fallback (UI-090).</param>
    /// <param name="restartUrl">Address the page offers as the way to start the sign-in over — the
    /// admissible login-initiation address of the transaction's application, already resolved by the
    /// caller. Null or empty — there is none, and the page offers to close the window instead.</param>
    /// <param name="cspNonce">CSP nonce of this request; without one the script goes out without the
    /// attribute, as every inline script of the contour does.</param>
    /// <returns>HTML page.</returns>
    public static string Build(
        IConfirmationPromptLocalizer localizer,
        string language,
        CorePageTexts texts,
        CorePageBranding? branding,
        string? restartUrl,
        string? cspNonce)
    {
        ArgumentNullException.ThrowIfNull(texts);

        // Probed on the key added to the page's set last: a locale file that carries the older texts of
        // the page but not this one renders the whole page in English rather than a mixed one. The
        // probe asks the locale file about the ORIGINAL key: an override does not decide the language.
        var contentLanguage = AuthPageLocalization.ResolveContentLanguage(
            localizer, CorePageCloseAction.CloseHint, language);
        var pageTitle = texts.Localize(localizer, PageTitle, contentLanguage);
        var message = texts.Localize(localizer, Message, contentLanguage);

        // The script and the rules exist only for the "Close" button.
        var closes = string.IsNullOrEmpty(restartUrl);
        var action = restartUrl is { Length: > 0 } url
            ? BuildRestartLink(localizer, texts, contentLanguage, url)
            : BuildCloseAction(localizer, texts, contentLanguage);
        var script = closes ? CorePageCloseAction.Script(cspNonce) : string.Empty;

        return $$"""
            <!DOCTYPE html>
            <html lang="{{contentLanguage}}" {{CorePageHead.BuildThemeAttribute(branding)}}>
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <meta name="robots" content="noindex" />
              <title>{{WebUtility.HtmlEncode(pageTitle)}}</title>
              {{CorePageHead.BuildServicePageHead(branding, closes ? CorePageCloseAction.Css : null)}}
            </head>
            <body>
              <main class="veriqa-card">
                <h1 class="veriqa-title">{{WebUtility.HtmlEncode(pageTitle)}}</h1>
                <p class="veriqa-instruction">{{WebUtility.HtmlEncode(message)}}</p>
                {{action}}
              </main>
              {{script}}
              {{CorePageAttribution.BuildLine(contentLanguage)}}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Builds the link to the application's own entry point, under the caption the expired sign-in
    /// window gives the same way out.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="contentLanguage">Language the page is rendered in.</param>
    /// <param name="restartUrl">Admissible login-initiation address of the application.</param>
    /// <returns>Markup of the link.</returns>
    private static string BuildRestartLink(
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string contentLanguage,
        string restartUrl)
    {
        var caption = texts.Localize(localizer, AuthPageStrings.RestartSignIn, contentLanguage);

        return $"<a class=\"veriqa-btn\" href=\"{WebUtility.HtmlEncode(restartUrl)}\">{WebUtility.HtmlEncode(caption)}</a>";
    }

    /// <summary>
    /// Builds the "Close" button with the line that tells the user the tab can be closed by hand. The
    /// button and the line share one actions row, which is the last child of the card: the row owns the
    /// spacing between them, so neither the button nor a line adds a margin of its own to the padding of
    /// the card — whichever of the lines is shown, and when none is.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="contentLanguage">Language the page is rendered in.</param>
    /// <returns>Markup of the button and the line.</returns>
    private static string BuildCloseAction(
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string contentLanguage) =>
        CorePageCloseAction.Row(
            texts.Localize(localizer, CorePageCloseAction.CloseText, contentLanguage),
            texts.Localize(localizer, CorePageCloseAction.CloseHint, contentLanguage));
}
