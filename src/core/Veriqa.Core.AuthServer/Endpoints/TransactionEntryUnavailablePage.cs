// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Neutral page the transaction entry surface answers with when the link leads nowhere it may lead
/// (SPEC-039 C15). One page for all three cases — no such transaction, a transaction that already
/// reached a terminal state, a transaction of another type — because telling them apart is exactly
/// what a caller must not be able to do by probing identifiers.
/// <para>
/// It carries no subject matter and no sign-in wording: this surface serves confirmations, and a
/// user who reaches it was not signing in. The texts are its own for the same reason the expired
/// callback page keeps its own — that page belongs to the sign-in path and speaks its language.
/// </para>
/// <para>
/// The page is built WITHOUT reading the transaction, even when one was found: its language comes
/// from the request alone and its branding from the core level. A page branded or translated by the
/// levels of a found transaction would answer the very question the single wording hides — the
/// caller would tell "no such transaction" from "there is one, but it is over" by the look of the
/// answer.
/// </para>
/// <para>
/// The link to the form of the claims completion on the device that confirmed the sign-in (SPEC-041 N78)
/// ends on the same page, with status 410, for the same reason: a form token that no longer acts must not
/// tell "no such transaction" from "not this token" or "no longer this surface". The wording fits that
/// link as it is — it names no sign-in and no confirmation, only a link that no longer leads anywhere.
/// </para>
/// </summary>
internal static class TransactionEntryUnavailablePage
{
    /// <summary>
    /// Page title Natural Key. Neutral to the type of the transaction and to what was being confirmed.
    /// <para>
    /// Visible to the assembly because the ENTRY page ends on the same wording when its transaction
    /// fails for a reason that is not the user's refusal (SPEC-039 R51): one neutral sentence for
    /// "this went nowhere", said in one place, rather than a second literal that would have to be
    /// kept equal to this one.
    /// </para>
    /// </summary>
    internal const string PageTitle = "This link is no longer available";

    /// <summary>
    /// Body-message Natural Key. It names no transaction, no action and no next step inside the
    /// product: the only place that knows what to do next is the application the user came from.
    /// </summary>
    private const string Message =
        "The link has expired or is no longer valid. Return to the application that opened it and try again.";

    /// <summary>
    /// Builds the neutral page HTML, localized into the recipient's language (the English base text —
    /// the key — is the fallback; the page is either fully in the requested language or fully in
    /// English, never mixed, so the html lang attribute stays truthful, WCAG 3.1.1).
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
