// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Antiforgery;

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.ChannelAdapter.WebProviders.Constants;
using Veriqa.Core.ChannelAdapter.WebProviders.State;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Pages;

/// <summary>
/// Renders the pages of a web provider channel. The page stands in for the chat of a messenger: its
/// head is the brand name with the channel under it; under them the messages of the
/// core to the account, and at the bottom the main block — the question with its two answers, the
/// waiting line, the outcome or the line of an ended request. There is no account line and no title
/// by the type of the transaction: a sign-in and a confirmation get the same page, and only the texts
/// the core renders tell them apart.
/// <para>
/// The page is built from the primitives of the "Core" contour — the card, the title, the button, the
/// status plate, the spinner — over the service-page stylesheet, and every interpolated value is
/// HTML-encoded. Without a script the page shows its state as rendered and answers by plain forms; the
/// script only reloads the page when its state changes. Every page where the request is over — an
/// outcome, a message the core sent at the end (a receipt, the menu or the button after a sign-in), the
/// line of an ended request, a refusal — ends with the button that closes its tab.
/// </para>
/// </summary>
internal static class WebProviderPageRenderer
{
    /// <summary>
    /// Resource name of the rules of the page (<c>UI/Styles/veriqa-web-provider-page.css</c>).
    /// </summary>
    private const string StylesResourceName = "Veriqa.Core.ChannelAdapter.WebProviders.UI.Styles.veriqa-web-provider-page.css";

    /// <summary>
    /// Resource name of the script of the page (<c>UI/Scripts/veriqa-web-provider-page.js</c>).
    /// </summary>
    private const string ScriptResourceName = "Veriqa.Core.ChannelAdapter.WebProviders.UI.Scripts.veriqa-web-provider-page.js";

    /// <summary>
    /// Number of random bytes of the identifier of a press.
    /// </summary>
    private const int PressIdLengthBytes = 16;

    /// <summary>
    /// Classes of the first action of a message — the main one, as the sign-in window has one.
    /// </summary>
    private const string MainActionClass = "veriqa-btn veriqa-wp-action";

    /// <summary>
    /// Classes of every other action of a message.
    /// </summary>
    private const string OtherActionClass = "veriqa-btn veriqa-btn-decline veriqa-wp-action";

    /// <summary>
    /// Arrow that ends the caption of an action of a message: it says the action leads somewhere and is
    /// hidden from assistive technology, so the name of the action stays its caption.
    /// </summary>
    private const string ActionArrow = "<span class=\"veriqa-wp-arrow\" aria-hidden=\"true\">\u2192</span>";

    /// <summary>
    /// Slot of the name of the channel in <see cref="WebProviderPageStrings.Via"/>.
    /// </summary>
    private const string ChannelNameSlot = "{0}";

    /// <summary>
    /// Markup of a line break within a paragraph.
    /// </summary>
    private const string LineBreak = "<br />";

    /// <summary>
    /// What separates the paragraphs of a text of the core once its line endings are normalized: a
    /// blank line, the one the question sets its anomaly note apart with.
    /// </summary>
    private static readonly string ParagraphSeparator = Environment.NewLine + Environment.NewLine;

    /// <summary>
    /// Builds the page of a session.
    /// </summary>
    /// <param name="frame">What the page is rendered within.</param>
    /// <param name="view">What the page shows.</param>
    /// <param name="transactionId">The transaction of the session, as the answer form names it.</param>
    /// <param name="antiforgery">Anti-forgery tokens of the forms of the page.</param>
    /// <param name="remainingMs">Milliseconds left of the session — the time the script watches the
    /// state for.</param>
    /// <returns>The HTML of the page.</returns>
    public static string BuildSessionPage(
        WebProviderPageFrame frame,
        WebProviderPageView view,
        string transactionId,
        AntiforgeryTokenSet antiforgery,
        long remainingMs)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(antiforgery);

        var body = string.Concat(
            BuildFeed(frame, view.Feed, antiforgery),
            BuildMainBlock(frame, view, transactionId, antiforgery),
            view.OffersClose
                ? CorePageCloseAction.Row(frame.Localize(CorePageCloseAction.CloseText), frame.Localize(CorePageCloseAction.CloseHint))
                : string.Empty);

        var cardAttributes =
            $""" id="{WebProviderPageFields.PageElementId}" data-state-url="{Encode(frame.RouteUrl(WebProviderRoutes.PageStateSegment))}" data-fingerprint="{Encode(view.Fingerprint())}" data-remaining-ms="{remainingMs.ToString(CultureInfo.InvariantCulture)}" """;

        var script = view.OffersClose
            ? Script(frame.CspNonce) + CorePageCloseAction.Script(frame.CspNonce)
            : Script(frame.CspNonce);

        return BuildDocument(frame, cardAttributes, body, script);
    }

    /// <summary>
    /// Builds a page of one line — a refusal: an unknown link, a sign-in the provider did not confirm,
    /// a browser without the mark, an out-of-date form. It shows nothing of a session; nothing is left to
    /// do on it, so it offers to close the tab, as every page where the request is over does.
    /// </summary>
    /// <param name="frame">What the page is rendered within.</param>
    /// <param name="naturalKey">Natural Key of the line.</param>
    /// <returns>The HTML of the page.</returns>
    public static string BuildNoticePage(WebProviderPageFrame frame, string naturalKey)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var body = string.Concat(
            $"""<div class="veriqa-status error" role="status">{Encode(frame.Localize(naturalKey))}</div>""",
            CorePageCloseAction.Row(frame.Localize(CorePageCloseAction.CloseText), frame.Localize(CorePageCloseAction.CloseHint)));

        return BuildDocument(frame, cardAttributes: string.Empty, body, CorePageCloseAction.Script(frame.CspNonce));
    }

    /// <summary>
    /// Builds the document around the card.
    /// </summary>
    /// <param name="frame">What the page is rendered within.</param>
    /// <param name="cardAttributes">Attributes of the card beyond its class, leading space included;
    /// empty — none.</param>
    /// <param name="body">Markup inside the card under its head.</param>
    /// <param name="script">The script element of the page; empty — none.</param>
    /// <returns>The HTML of the page.</returns>
    private static string BuildDocument(WebProviderPageFrame frame, string cardAttributes, string body, string script)
    {
        var title = Encode(frame.BrandName ?? frame.ChannelName);

        return $$"""
            <!DOCTYPE html>
            <html lang="{{Encode(frame.Language)}}" {{CorePageHead.BuildThemeAttribute(frame.Branding)}}>
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <meta name="robots" content="noindex" />
              <title>{{title}}</title>
              {{CorePageHead.BuildServicePageHead(frame.Branding, Styles)}}
            </head>
            <body>
              <main class="veriqa-card"{{cardAttributes}}>
                {{BuildHead(frame)}}
                {{body}}
              </main>
              {{CorePageAttribution.BuildLine(frame.Language)}}
              {{script}}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// The rules of the page over the rules of the service pages, the close action included: a page
    /// may come to offer it on a reload, and the rules cost less than a second stylesheet would.
    /// </summary>
    private static string Styles =>
        CorePageEmbeddedText.Load(typeof(WebProviderPageRenderer).Assembly, StylesResourceName) + CorePageCloseAction.Css;

    /// <summary>
    /// Builds the head of the card. The user signs in to the application, not to the provider, so the
    /// head carries the brand name the sign-in window shows, and the channel is the line under it that
    /// says what the brand is reached through; with no brand name stated the head is the channel alone.
    /// The brand comes from the levels of the transaction, so a sign-in and a confirmation are headed
    /// alike.
    /// </summary>
    /// <param name="frame">What the page is rendered within.</param>
    /// <returns>The markup of the head.</returns>
    private static string BuildHead(WebProviderPageFrame frame)
    {
        if (frame.BrandName is null)
        {
            return $"""<h1 class="veriqa-title">{Encode(frame.ChannelName)}</h1>""";
        }

        // Replaced rather than formatted: an override of the line may carry braces of its own.
        var via = frame.Localize(WebProviderPageStrings.Via)
            .Replace(ChannelNameSlot, frame.ChannelName, StringComparison.Ordinal);

        return $"""
            <h1 class="veriqa-title">{Encode(frame.BrandName)}</h1>
            <p class="veriqa-wp-via">{Encode(via)}</p>
            """;
    }

    /// <summary>
    /// Builds the main block of the page.
    /// </summary>
    /// <param name="frame">What the page is rendered within.</param>
    /// <param name="view">What the page shows.</param>
    /// <param name="transactionId">The transaction of the session.</param>
    /// <param name="antiforgery">Anti-forgery tokens of the forms.</param>
    /// <returns>The markup of the block.</returns>
    private static string BuildMainBlock(
        WebProviderPageFrame frame,
        WebProviderPageView view,
        string transactionId,
        AntiforgeryTokenSet antiforgery) =>
        view.Block switch
        {
            WebProviderPageBlock.Outcome =>
                $"""<div class="veriqa-status" role="status">{Lines(view.Text)}</div>""",
            WebProviderPageBlock.Closed => string.Empty,
            WebProviderPageBlock.Ended =>
                $"""<p class="veriqa-instruction" role="status">{Encode(frame.Localize(WebProviderPageStrings.RequestEnded))}</p>""",
            WebProviderPageBlock.Question => BuildQuestion(frame, view.Text ?? string.Empty, transactionId, antiforgery),
            _ => $"""
                <div class="veriqa-wp-waiting">
                  <div class="veriqa-spinner" aria-hidden="true"></div>
                  <p class="veriqa-instruction" role="status" aria-live="polite">{Encode(frame.Localize(WebProviderPageStrings.Waiting))}</p>
                </div>
                """
        };

    /// <summary>
    /// Builds the question with its two answers. Both answers post the transaction the question
    /// belongs to, so an answer from a browser whose mark has since moved to another transaction is
    /// refused.
    /// </summary>
    /// <param name="frame">What the page is rendered within.</param>
    /// <param name="question">The question as the core rendered it.</param>
    /// <param name="transactionId">The transaction of the question.</param>
    /// <param name="antiforgery">Anti-forgery tokens of the forms.</param>
    /// <returns>The markup of the question.</returns>
    private static string BuildQuestion(
        WebProviderPageFrame frame,
        string question,
        string transactionId,
        AntiforgeryTokenSet antiforgery)
    {
        var action = Encode(frame.RouteUrl(WebProviderRoutes.AnswerSegment));
        var fields = $"""
            <input type="hidden" name="{WebProviderPageFields.Transaction}" value="{Encode(transactionId)}" />
            {AntiforgeryField(antiforgery)}
            """;

        return $"""
            {Paragraphs(question)}
            <div class="veriqa-actions">
              <form method="post" action="{action}">
                {fields}
                <button type="submit" class="veriqa-btn" name="{WebProviderPageFields.Answer}" value="{WebProviderPageFields.AnswerConfirm}">{Encode(frame.Localize(WebProviderPageStrings.ConfirmButton))}</button>
              </form>
              <form method="post" action="{action}">
                {fields}
                <button type="submit" class="veriqa-btn veriqa-btn-decline" name="{WebProviderPageFields.Answer}" value="{WebProviderPageFields.AnswerDecline}">{Encode(frame.Localize(WebProviderPageStrings.DeclineButton))}</button>
              </form>
            </div>
            """;
    }

    /// <summary>
    /// Builds the current message of the feed (<see cref="WebProviderPageView.CurrentFeed"/>): its text,
    /// and under it the link to the claim form or the buttons — a link button as a link, an item of the
    /// menu as a form that posts its press. Empty when there is no current message.
    /// </summary>
    /// <param name="frame">What the page is rendered within.</param>
    /// <param name="feed">The current message: none or one.</param>
    /// <param name="antiforgery">Anti-forgery tokens of the press forms.</param>
    /// <returns>The markup of the feed.</returns>
    private static string BuildFeed(
        WebProviderPageFrame frame,
        IReadOnlyList<WebProviderFeedMessage> feed,
        AntiforgeryTokenSet antiforgery)
    {
        if (feed.Count is 0)
        {
            return string.Empty;
        }

        var markup = new StringBuilder();
        markup.Append("""<div class="veriqa-wp-feed">""");

        foreach (var message in feed)
        {
            markup.Append("""<div class="veriqa-wp-message">""");
            markup.Append(Paragraphs(message.Text));

            if (message.ClaimFormLink is { } formLink && WebProviderFeedRules.IsDrawable(formLink))
            {
                markup.Append(
                    $"""<div class="veriqa-actions"><a class="{MainActionClass}" href="{Encode(formLink.Url)}">{Encode(formLink.ButtonLabel)}{ActionArrow}</a></div>""");
            }

            if (message.ActionButtons is { } actions)
            {
                markup.Append("""<div class="veriqa-actions">""");

                var first = true;
                foreach (var button in actions.Buttons.Where(WebProviderFeedRules.IsDrawable))
                {
                    markup.Append(BuildButton(frame, button, first ? MainActionClass : OtherActionClass, antiforgery));
                    first = false;
                }

                markup.Append("</div>");
            }

            markup.Append("</div>");
        }

        markup.Append("</div>");

        return markup.ToString();
    }

    /// <summary>
    /// Builds one button of a message: a link to its address, or a form posting the press of its item.
    /// The press is given its identifier here, so sending the same form again carries the same one.
    /// </summary>
    /// <param name="frame">What the page is rendered within.</param>
    /// <param name="button">The button, one the page can draw (<see cref="WebProviderFeedRules.IsDrawable(ChannelActionButton)"/>).</param>
    /// <param name="cssClass">Classes of the button: the first action of a message is the main one.</param>
    /// <param name="antiforgery">Anti-forgery tokens of the press form.</param>
    /// <returns>The markup of the button.</returns>
    private static string BuildButton(
        WebProviderPageFrame frame,
        ChannelActionButton button,
        string cssClass,
        AntiforgeryTokenSet antiforgery)
    {
        if (WebProviderFeedRules.IsLink(button))
        {
            return $"""<a class="{cssClass}" href="{Encode(button.Url)}">{Encode(button.Label)}{ActionArrow}</a>""";
        }

        var pressId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(PressIdLengthBytes));

        return $"""
            <form method="post" action="{Encode(frame.RouteUrl(WebProviderRoutes.PressSegment))}">
              <input type="hidden" name="{WebProviderPageFields.Item}" value="{Encode(button.CallbackData)}" />
              <input type="hidden" name="{WebProviderPageFields.PressId}" value="{pressId}" />
              {AntiforgeryField(antiforgery)}
              <button type="submit" class="{cssClass}">{Encode(button.Label)}{ActionArrow}</button>
            </form>
            """;
    }

    /// <summary>
    /// Builds the hidden anti-forgery field of a form.
    /// </summary>
    /// <param name="antiforgery">Anti-forgery tokens.</param>
    /// <returns>The markup of the field.</returns>
    private static string AntiforgeryField(AntiforgeryTokenSet antiforgery) =>
        $"""<input type="hidden" name="{Encode(antiforgery.FormFieldName)}" value="{Encode(antiforgery.RequestToken)}" />""";

    /// <summary>
    /// Builds the paragraphs of a text of the core: each part a blank line separates is a paragraph of
    /// its own, and a single line ending within a part breaks its line, whatever the line endings the
    /// text was worded with.
    /// </summary>
    /// <param name="text">The text, plain.</param>
    /// <returns>The encoded paragraphs.</returns>
    private static string Paragraphs(string text) =>
        string.Join(
            Environment.NewLine,
            text.ReplaceLineEndings()
                .Split(ParagraphSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(paragraph => $"""<p class="veriqa-instruction">{Lines(paragraph)}</p>"""));

    /// <summary>
    /// Encodes a text of the core with its line endings kept as line breaks.
    /// </summary>
    /// <param name="text">The text, plain.</param>
    /// <returns>The encoded lines, separated by line breaks.</returns>
    private static string Lines(string? text) =>
        string.Join(
            LineBreak,
            (text ?? string.Empty).ReplaceLineEndings()
                .Split(Environment.NewLine, StringSplitOptions.TrimEntries)
                .Select(Encode));

    /// <summary>
    /// Builds the script element of the page, inlined under the CSP nonce of the request.
    /// </summary>
    /// <param name="cspNonce">CSP nonce of the request.</param>
    /// <returns>Markup of the script.</returns>
    private static string Script(string? cspNonce)
    {
        var nonceAttribute = string.IsNullOrEmpty(cspNonce)
            ? string.Empty
            : $" nonce=\"{Encode(cspNonce)}\"";

        return $"<script{nonceAttribute}>{CorePageEmbeddedText.Load(typeof(WebProviderPageRenderer).Assembly, ScriptResourceName)}</script>";
    }

    /// <summary>
    /// HTML-encodes a value; <c>null</c> — empty.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The encoded value.</returns>
    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
