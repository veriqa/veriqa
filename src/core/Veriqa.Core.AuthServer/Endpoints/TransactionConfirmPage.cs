// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;

using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// The receipt of the core confirmation surface (SPEC-039 R37): the page that reports how a confirmation
/// transaction ended. The question it is answered on is the one question page of the core
/// (<see cref="WebConfirmPage"/>, SPEC-007 UI-123), shared with the sign-in.
/// <para>
/// The page words NOTHING of its own: the terminal line comes from the receipt port — so the outcome is
/// never spelled twice in two places.
/// </para>
/// </summary>
internal static class TransactionConfirmPage
{
    /// <summary>
    /// Title Natural Key of the pages of a confirmation — the question page names its confirmation with it
    /// (<see cref="WebConfirmPage.ForConfirmation"/>), and the receipt probes its language on it. Neutral to
    /// the type of the transaction and carrying no sign-in semantics: what exactly is being confirmed is
    /// said by the question below it, and that text belongs to the calling party rather than to this page.
    /// </summary>
    public const string PageTitle = "Confirmation required";

    /// <summary>
    /// Builds the receipt page — one card carrying one line: how the transaction ended, and under it the
    /// button that closes the tab (<see cref="OutcomeCloseAction"/>).
    /// </summary>
    /// <remarks>
    /// One neutral styling for all three outcomes, and the same text in the heading and in the
    /// title. A confirmed outcome painted differently from a declined one would be this page
    /// stating an outcome of its own beside the wording, and the wording is the only thing that may
    /// state it (SPEC-039 R51): a receipt is a report, not a verdict of the surface it lands on.
    /// <para>
    /// That single line is the page heading itself — one element carrying both the typographic
    /// heading and the status line, because the outcome may be spelled only once on the page
    /// (SPEC-015 §4.15: exactly one &lt;h1&gt; per page, and it is the page heading; §4.4: the
    /// receipt states its outcome by that one line, in the neutral state).
    /// </para>
    /// <para>
    /// The language is probed on the title key exactly as on the question page: both the page
    /// keys and the receipt wording are resolved out of the same host locale files by the same
    /// localizer, so a language the host carries no translation set for degrades both alike.
    /// </para>
    /// </remarks>
    /// <param name="outcomeText">Receipt text, already localized, as plain text.</param>
    /// <param name="localizer">Locale-file localizer (used only to probe the content language).</param>
    /// <param name="language">Language the receipt was rendered for.</param>
    /// <param name="branding">Effective branding of the page's own transaction.</param>
    /// <param name="close">What the action under the line is built with.</param>
    /// <returns>HTML page.</returns>
    public static string BuildReceipt(
        string outcomeText,
        IConfirmationPromptLocalizer localizer,
        string language,
        CorePageBranding? branding,
        ReceiptCloseAction close)
    {
        ArgumentNullException.ThrowIfNull(close);

        var contentLanguage = AuthPageLocalization.ResolveContentLanguage(localizer, PageTitle, language);
        var encodedOutcome = WebUtility.HtmlEncode(outcomeText);

        // The receipt is the ending itself, so the row is on screen from the load; without scripts the
        // button does nothing, and the line under it tells to close the tab by hand.
        var closeRow = OutcomeCloseAction.Row(
            close.ApplicationName, hidden: false, localizer, close.Texts, contentLanguage);

        return $$"""
            <!DOCTYPE html>
            <html lang="{{contentLanguage}}" {{CorePageHead.BuildThemeAttribute(branding)}}>
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <meta name="robots" content="noindex" />
              <title>{{encodedOutcome}}</title>
              {{CorePageHead.BuildServicePageHead(branding, CorePageCloseAction.Css)}}
            </head>
            <body>
              <main class="veriqa-card">
                <h1 class="veriqa-title veriqa-status">{{encodedOutcome}}</h1>
                {{closeRow}}
              </main>
              {{CorePageAttribution.BuildLine(contentLanguage)}}
              {{CorePageCloseAction.Script(close.CspNonce)}}
            </body>
            </html>
            """;
    }
}
