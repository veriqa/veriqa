// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Text.Json;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The launch page of a channel whose link carries no transaction: the page a person lands on after
/// scanning the QR code of such a channel. The link of the channel only opens its chat, so the page shows
/// what starts the transaction there — the message with the code — by the very block the sign-in window
/// shows it with (<see cref="ChannelStartCodeBlock"/>), and its button copies the message and opens the
/// chat. The heading is the one of the other launch page (<see cref="HopLaunchPageHeading"/>).
/// <para>
/// Unlike the launch page of the <c>Interstitial</c> redirect style (<see cref="HopInterstitialPage"/>),
/// this page never takes the person into the channel by itself: they have a message to read first, and a
/// page that left at once would leave them in a chat with nothing to send.
/// </para>
/// <para>
/// The button opens the chat in a new browsing context, as the same button of the sign-in window does, and
/// the page stays behind it. That is what lets the copy finish — a write to the clipboard started by a
/// press may be dropped when the document that started it is unloaded by the very same press — and what
/// keeps the message on screen for a person whose clipboard refused. A browser that does not open a new
/// context follows the link in place: the copy may then be lost, the way into the chat is not.
/// </para>
/// <para>
/// It belongs to its transaction: the page speaks the transaction's language and is branded by its levels
/// (the caller resolves both).
/// </para>
/// </summary>
internal static class HopCodeStartPage
{
    /// <summary>
    /// The page's own texts an owning level may override (SPEC-012 §4.3, key <c>AuthPage.Texts</c>): the
    /// heading, which the page shares with the other launch page. The caption, the button and the status
    /// line are not among them: they are the texts of the block, and the block contributes them.
    /// </summary>
    internal static IReadOnlyList<string> OverridableTexts { get; } = HopLaunchPageHeading.OverridableTexts;

    /// <summary>
    /// Builds the page. Every text and attribute value is HTML-encoded; the message reaches the copying
    /// function as a JSON literal and is not written into the markup of the button.
    /// </summary>
    /// <param name="content">
    /// Where the button leads, which channel that is and how it is named, and the subject of a
    /// confirmation. Its <see cref="HopInterstitialContent.StartMessage"/> is not read: this page shows
    /// the message as its only start, and takes it as <paramref name="message"/>.
    /// </param>
    /// <param name="message">The message with the code of the transaction, in its two forms.</param>
    /// <param name="signalScript">What the page sends back to the core, as the server decided it for this
    /// presentation (SPEC-025 HOP-066c). It adds no visible element or text.</param>
    /// <param name="cspNonce">CSP nonce of this request. The scripts go out either way — the nonce
    /// attribute is simply left off without one, as every other inline script of the contour does; the
    /// message and the button stay on the page regardless.</param>
    /// <param name="localizer">Locale-file localizer (Natural Key → localized text).</param>
    /// <param name="language">Language of the transaction's page — the language the subject is worded in.</param>
    /// <param name="texts">Text overrides stated by the transaction's levels.</param>
    /// <param name="branding">Effective branding of the transaction's levels.</param>
    /// <returns>HTML page.</returns>
    public static string Build(
        HopInterstitialContent content,
        ChannelStartMessageText message,
        HopSignalScript signalScript,
        string? cspNonce,
        IConfirmationPromptLocalizer localizer,
        string language,
        CorePageTexts texts,
        CorePageBranding? branding)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(texts);

        // WCAG 3.1.1: the page's own texts are either all in the requested language or all on the English
        // base text, the texts of the block included — they are resolved in the same content language.
        var contentLanguage = HopLaunchPageHeading.ContentLanguage(localizer, content.TransactionType, language);

        // By the rule the tabs of the sign-in window are named with, so the two surfaces never call one
        // channel two names.
        var channelName = ChannelNames.LocalizeChannelName(
            localizer, texts, content.ChannelType, content.ChannelDisplayName, contentLanguage);
        var pageTitle = HopLaunchPageHeading.Text(
            content.ChannelType, content.ChannelDisplayName, content.TransactionType, localizer, texts, contentLanguage);

        var subjectLine = ConfirmationSubjectText.Markup(content.SubjectText, language, contentLanguage);

        // The button of the block, with the id a press on which sends the signal of the started
        // transition — the same id and the same signal as the button of the launch page.
        var block = ChannelStartCodeBlock.OpenBlockMarkup(
            ChannelStartCodeBlock.CaptionText(localizer, texts, content.BotName, contentLanguage),
            message.DisplayText,
            content.Target,
            content.ChannelType,
            ChannelStartCodeBlock.CopyAndOpenText(localizer, texts, channelName, contentLanguage),
            $" id=\"{HopInterstitialScript.LaunchButtonId}\"");

        var nonceAttribute = string.IsNullOrEmpty(cspNonce)
            ? string.Empty
            : $" nonce=\"{WebUtility.HtmlEncode(cspNonce)}\"";

        // The settings of the copying function as a JSON literal, written with the encoder of the data
        // blocks of the contour: it escapes what could end the script element, whatever the message holds.
        var copySettings = JsonSerializer.Serialize(
            ChannelStartCodeBlock.ScriptSettings(
                localizer,
                texts,
                new Dictionary<string, string>(StringComparer.Ordinal) { [content.ChannelType] = message.Text },
                contentLanguage),
            AuthPageScript.ConfigSerializerOptions);

        // The signals go in an element of their own, so that neither script can stop the other.
        var signals = HopInterstitialScript.BuildWithoutLaunch(signalScript);
        var signalsElement = signals.Length == 0
            ? string.Empty
            : $"<script{nonceAttribute}>{signals}</script>";

        return $$"""
            <!DOCTYPE html>
            <html lang="{{contentLanguage}}" {{CorePageHead.BuildThemeAttribute(branding)}}>
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <meta name="robots" content="noindex" />
              <title>{{WebUtility.HtmlEncode(pageTitle)}}</title>
              {{CorePageHead.BuildServicePageHead(branding, HopLaunchPageHeading.Css + ChannelStartCodeBlock.Css)}}
            </head>
            <body>
              <main class="veriqa-card">
                {{HopLaunchPageHeading.Markup(content.ChannelType, content.IconSvgPath, pageTitle)}}
                {{subjectLine}}
                {{block}}
              </main>
              {{CorePageAttribution.BuildLine(contentLanguage)}}
              <script{{nonceAttribute}}>{{ChannelStartCodeBlock.Js}}</script>
              <script{{nonceAttribute}}>window.veriqaStartMessage(document, {{copySettings}});</script>
              {{signalsElement}}
            </body>
            </html>
            """;
    }
}
