// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Page a state-changing POST of a core page answers with when its AntiForgery token is missing or does
/// not match the cookie of the browser — every POST of the shared AntiForgery check (SPEC-007 UI-126): the
/// form was rendered too long ago, in another browser, or inside a frame whose cookies the browser
/// withholds. Nothing was changed by the request.
/// <para>
/// The page is built WITHOUT reading the transaction: a request that failed the AntiForgery check may be
/// forged, so nothing it names is trusted — the language comes from the request alone and the branding
/// from the core level. For the same reason it carries no button and no link.
/// </para>
/// <para>
/// The way out it names is the previous page, reloaded: only that page issues a fresh token. Reloading
/// this page instead would repeat the same POST with the same stale token.
/// </para>
/// </summary>
internal static class AntiforgeryRefusalPage
{
    /// <summary>
    /// Page title Natural Key.
    /// </summary>
    private const string PageTitle = "This page is out of date";

    /// <summary>
    /// Body-message Natural Key: the one step that issues a fresh token.
    /// </summary>
    private const string Message = "Go back to the previous page, reload it and try again.";

    /// <summary>
    /// Builds the page HTML, localized into the language of the request (the English base text — the
    /// key — is the fallback; the page is either fully in the requested language or fully in English,
    /// never mixed, so the html lang attribute stays truthful, WCAG 3.1.1).
    /// </summary>
    /// <param name="localizer">Locale-file localizer (Natural Key → localized text).</param>
    /// <param name="language">Language detected from the request.</param>
    /// <param name="branding">Effective branding of the core level.</param>
    /// <returns>HTML page.</returns>
    public static string Build(
        IConfirmationPromptLocalizer localizer,
        string language,
        CorePageBranding? branding)
    {
        var contentLanguage = AuthPageLocalization.ResolveContentLanguage(localizer, PageTitle, language);
        var pageTitle = AuthPageLocalization.Localize(localizer, PageTitle, contentLanguage);
        var message = AuthPageLocalization.Localize(localizer, Message, contentLanguage);

        return $$"""
            <!DOCTYPE html>
            <html lang="{{contentLanguage}}" {{CorePageHead.BuildThemeAttribute(branding)}}>
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <meta name="robots" content="noindex" />
              <title>{{WebUtility.HtmlEncode(pageTitle)}}</title>
              {{CorePageHead.BuildServicePageHead(branding)}}
            </head>
            <body>
              <main class="veriqa-card">
                <h1 class="veriqa-title">{{WebUtility.HtmlEncode(pageTitle)}}</h1>
                <p class="veriqa-instruction">{{WebUtility.HtmlEncode(message)}}</p>
              </main>
              {{CorePageAttribution.BuildLine(contentLanguage)}}
            </body>
            </html>
            """;
    }
}
