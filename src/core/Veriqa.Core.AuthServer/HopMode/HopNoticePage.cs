// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The two pages of the resolve endpoint that do not lead into the channel: the refusal page — any
/// refusal, whoever presented the link (SPEC-025 HOP-070) — and the neutral page a recognized link
/// preview gets where a person would be taken into the channel (HOP-072a).
/// <para>
/// Both are built WITHOUT reading the transaction: their language comes from the request and their
/// branding from the core level. A page translated or branded by the levels of the transaction would make
/// the look of the answer a readable state of somebody else's transaction — and the neutral page is read
/// by an unauthenticated intermediary whose preview card is seen wherever the link was pasted. For the
/// same reason neither carries a button, a link, a script or any value of the caller, the title of the
/// document included.
/// </para>
/// </summary>
internal static class HopNoticePage
{
    /// <summary>
    /// Title Natural Key of the refusal page.
    /// </summary>
    private const string RefusalTitle = "This link can no longer be used";

    /// <summary>
    /// Body Natural Key of the refusal page: the way out is where the code was scanned.
    /// </summary>
    private const string RefusalMessage =
        "The link is invalid or has expired. Go back to the screen where you scanned the code and start again.";

    /// <summary>
    /// Title Natural Key of the neutral page.
    /// </summary>
    private const string NeutralTitle = "This link cannot be previewed";

    /// <summary>
    /// Body Natural Key of the neutral page. It names the way out for a person the preview heuristic took
    /// for an intermediary: start again and present the link differently (SPEC-025 HOP-072a).
    /// </summary>
    private const string NeutralMessage =
        "If you opened this link yourself, go back to the screen where you scanned the code, start again "
        + "and open the link in your phone's browser or with another scanner app.";

    /// <summary>
    /// Builds the refusal page.
    /// </summary>
    /// <param name="localizer">Locale-file localizer (Natural Key → localized text).</param>
    /// <param name="language">Language detected from the request.</param>
    /// <param name="branding">Effective branding of the core level.</param>
    /// <returns>HTML page.</returns>
    public static string BuildRefusal(
        IConfirmationPromptLocalizer localizer,
        string language,
        CorePageBranding? branding)
    {
        // WCAG 3.1.1: the page is either fully in the requested language or fully on the English base
        // text, so the lang attribute stays truthful.
        var contentLanguage = AuthPageLocalization.ResolveContentLanguage(localizer, RefusalTitle, language);

        return Build(
            AuthPageLocalization.Localize(localizer, RefusalTitle, contentLanguage),
            AuthPageLocalization.Localize(localizer, RefusalMessage, contentLanguage),
            contentLanguage,
            branding);
    }

    /// <summary>
    /// Builds the neutral page.
    /// </summary>
    /// <param name="localizer">Locale-file localizer (Natural Key → localized text).</param>
    /// <param name="language">Language detected from the request.</param>
    /// <param name="branding">Effective branding of the core level.</param>
    /// <returns>HTML page.</returns>
    public static string BuildNeutral(
        IConfirmationPromptLocalizer localizer,
        string language,
        CorePageBranding? branding)
    {
        var contentLanguage = AuthPageLocalization.ResolveContentLanguage(localizer, NeutralTitle, language);

        return Build(
            AuthPageLocalization.Localize(localizer, NeutralTitle, contentLanguage),
            AuthPageLocalization.Localize(localizer, NeutralMessage, contentLanguage),
            contentLanguage,
            branding);
    }

    /// <summary>
    /// The markup both pages share: a card with a title and one sentence.
    /// </summary>
    /// <param name="title">Localized title.</param>
    /// <param name="message">Localized body text.</param>
    /// <param name="contentLanguage">Language the texts are in.</param>
    /// <param name="branding">Effective branding of the core level.</param>
    /// <returns>HTML page.</returns>
    private static string Build(string title, string message, string contentLanguage, CorePageBranding? branding)
    {
        return $$"""
            <!DOCTYPE html>
            <html lang="{{contentLanguage}}" {{CorePageHead.BuildThemeAttribute(branding)}}>
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <meta name="robots" content="noindex" />
              <title>{{WebUtility.HtmlEncode(title)}}</title>
              {{CorePageHead.BuildServicePageHead(branding)}}
            </head>
            <body>
              <main class="veriqa-card">
                <h1 class="veriqa-title">{{WebUtility.HtmlEncode(title)}}</h1>
                <p class="veriqa-instruction">{{WebUtility.HtmlEncode(message)}}</p>
              </main>
              {{CorePageAttribution.BuildLine(contentLanguage)}}
            </body>
            </html>
            """;
    }
}
