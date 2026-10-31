// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Text.Json;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Endpoints;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The launch page of the <c>Interstitial</c> redirect style (SPEC-025 HOP-066): the page a person lands on
/// after scanning, which takes them into the channel by itself and keeps a visible button into it for the
/// case the automatic launch does not happen — a platform that does not open the app from a script, or
/// scripts switched off.
/// <para>
/// It belongs to its transaction: the page speaks the transaction's language and is branded by its levels
/// (the caller resolves both). The heading names the channel under its icon
/// (<see cref="HopLaunchPageHeading"/>), and the button is the button of the sign-in window — the same
/// wording and the same channel color (<see cref="ChannelButton"/>).
/// </para>
/// <para>
/// The instruction says what the person is about to see, naming the channel (<see cref="HopLaunchInstructions"/>).
/// Right under it, visible from the load, stands the hint of the channel where a level words one — what the
/// phone may show on its way into that channel; the sign-in window shows the same hint once it notices that
/// way. The hint concerns the way into the channel only, so the outcome of the transaction takes it away
/// with the controls.
/// </para>
/// <para>
/// From the start of a transition — the automatic launch or a press of the button — the page says where
/// the person went and follows the outcome of the transaction, which then takes the place of the heading
/// and the button (SPEC-025 HOP-186) by the reaction the scanner page shares (<see cref="PageStatusBlock"/>).
/// A page the person came back to after a transition does not launch again and shows that state at once
/// (<see cref="HopInterstitialScript"/>). A sign-in whose question the core took onto the page of the
/// sign-in window leaves this page nowhere to go: it points back to that window in place of the heading and
/// the button and goes on following the outcome. With scripts switched off it stays a page with a link.
/// </para>
/// <para>
/// A channel whose link carries the transaction and that also starts by a code keeps the message with the
/// code here as a way on when the chat opened and nothing started in it
/// (<see cref="HopInterstitialContent.StartMessage"/>). The page offers it behind a link that appears a
/// while after the transition began, bold in the text color of the card rather than as the muted service
/// link: the caption, the code and one button that copies it and opens the channel. While that block is
/// open the button of the page above it is out of sight, so the page offers one way into the channel, not
/// two. A sign-in and a confirmation get the same link and block; only the words of the link name the act.
/// Once the answer is awaited in the channel the spare start code leads nowhere, and the page takes it away
/// for good (SPEC-025 HOP-194). The page shows nothing of the outcome of the transaction beyond its status
/// line and nothing about who confirmed it.
/// </para>
/// </summary>
internal static class HopInterstitialPage
{
    /// <summary>
    /// Id of the hint of the channel under the instruction.
    /// </summary>
    public const string HintElementId = "veriqa-hop-channel-hint";

    /// <summary>
    /// The page's own texts an owning level may override (SPEC-012 §4.3, key <c>AuthPage.Texts</c>). The
    /// heading and the instruction contribute their own (<see cref="HopLaunchPageHeading.OverridableTexts"/>,
    /// <see cref="HopLaunchInstructions.OverridableTexts"/>). The button, the link that opens the spare start
    /// code, the caption of the code and the button of its block, the line of a begun transition into a
    /// chat, the line that sends the person back to the sign-in window and the lines the page ends on
    /// are not among them: they are texts of the sign-in window and of the block of the start by a code,
    /// which contribute them, so an override of one reaches this page as well — and the line of a begun
    /// transition is a message of the template mechanism the caller resolves.
    /// </summary>
    internal static IReadOnlyList<string> OverridableTexts { get; } =
    [
        .. HopLaunchPageHeading.OverridableTexts,
        .. HopLaunchInstructions.OverridableTexts
    ];

    /// <summary>
    /// Builds the launch page. Every text and attribute value is HTML-encoded; the scripts carry every
    /// value as a JSON literal.
    /// </summary>
    /// <param name="content">What the page shows.</param>
    /// <param name="outcomeWatch">What the page follows the outcome of its transaction by once a transition began.</param>
    /// <param name="transitionLine">The line of a begun transition into the channel, ready and worded in the
    /// content language of the page — the line the sign-in window shows once the answer is awaited in that
    /// channel, resolved by the caller over the levels of the transaction; empty — the page has none.</param>
    /// <param name="hint">The hint of the channel, ready and worded in the content language of the page —
    /// the hint the sign-in window shows once the phone is on its way into that channel, resolved by the
    /// caller over the levels of the transaction; empty — the channel has none.</param>
    /// <param name="signalScript">What the page sends back to the core, as the server decided it for this
    /// presentation (SPEC-025 HOP-066c). It adds no visible element or text.</param>
    /// <param name="cspNonce">CSP nonce of this request. The scripts go out either way — the nonce
    /// attribute is simply left off without one, as every other inline script of the contour does; the
    /// button stays visible regardless.</param>
    /// <param name="localizer">Locale-file localizer (Natural Key → localized text).</param>
    /// <param name="language">Language of the transaction's page — the language the subject is worded in.</param>
    /// <param name="texts">Text overrides stated by the transaction's levels.</param>
    /// <param name="branding">Effective branding of the transaction's levels.</param>
    /// <returns>HTML page.</returns>
    public static string Build(
        HopInterstitialContent content,
        HopInterstitialOutcomeWatch outcomeWatch,
        string transitionLine,
        string hint,
        HopSignalScript signalScript,
        string? cspNonce,
        IConfirmationPromptLocalizer localizer,
        string language,
        CorePageTexts texts,
        CorePageBranding? branding)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(outcomeWatch);
        ArgumentNullException.ThrowIfNull(transitionLine);
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(texts);

        // WCAG 3.1.1: the page's own texts are either all in the requested language or all on the English
        // base text — the button text included, it is resolved in the same content language. The subject
        // is not a text of the page and does not follow that fallback (see below).
        var contentLanguage = HopLaunchPageHeading.ContentLanguage(localizer, content.TransactionType, language);
        var pageTitle = HopLaunchPageHeading.Text(
            content.ChannelType, content.ChannelDisplayName, content.TransactionType, localizer, texts, contentLanguage);
        var message = HopLaunchInstructions.Text(content, localizer, texts, contentLanguage);
        var buttonText = ChannelButton.Text(
            localizer,
            texts,
            content.ChannelType,
            content.ChannelDisplayName,
            HopLaunchPageHeading.IsSignIn(content.TransactionType),
            contentLanguage);

        // What is being confirmed, right under the title, by the same line the entry page of the
        // confirmation uses: worded in the transaction's language, it keeps that language when the page
        // falls back to the base text, and then states it with its own lang.
        var subjectLine = ConfirmationSubjectText.Markup(content.SubjectText, language, contentLanguage);

        // The hint of the channel stands as a paragraph of its own under the instruction. It tells what the
        // phone may show on its way into the channel, so the outcome takes it away with the controls.
        var hintLine = hint.Length is 0
            ? string.Empty
            : $"<p id=\"{HintElementId}\" class=\"veriqa-instruction\" {HopInterstitialWatchScript.ControlAttribute}>{WebUtility.HtmlEncode(hint)}</p>";

        var nonceAttribute = string.IsNullOrEmpty(cspNonce)
            ? string.Empty
            : $" nonce=\"{WebUtility.HtmlEncode(cspNonce)}\"";

        var script = HopInterstitialScript.Build(content.Target, signalScript);
        var watchScript = HopInterstitialWatchScript.Build(
            outcomeWatch.StatusPath,
            outcomeWatch.QuestionPath,
            outcomeWatch.ConfirmedWatchLimit,
            WatchTexts(content, outcomeWatch.Lines, transitionLine, localizer, texts, contentLanguage),
            SpareStartCodeDelays.AfterTransitionMilliseconds);

        var spare = SpareStartCode(content, nonceAttribute, localizer, texts, contentLanguage);

        // The action under the outcome, ready and hidden: the status block shows it with an ending of the
        // transaction, and a page that never saw a transition never shows one.
        var close = OutcomeCloseAction.Row(
            outcomeWatch.CloseApplicationName, hidden: true, localizer, texts, contentLanguage);

        // The button is a plain link in the document, not a fallback hidden behind <noscript>: a script
        // that ran is no proof the app opened, and the person must still have a way into the channel.
        // The script of the status line goes first, so its listeners are in place before the automatic
        // launch starts, and only the shared reaction of the status block it calls stands ahead of it; the
        // copying function of the spare start code stands between the status line and the launch, and each
        // script is an element of its own, so a failure of one stops none of the others — the script of the
        // status line alone stands down without the reaction it calls, and the page stays as it was loaded.
        return $$"""
            <!DOCTYPE html>
            <html lang="{{contentLanguage}}" {{CorePageHead.BuildThemeAttribute(branding)}}>
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <meta name="robots" content="noindex" />
              <title>{{WebUtility.HtmlEncode(pageTitle)}}</title>
              {{CorePageHead.BuildServicePageHead(branding, HopLaunchPageHeading.Css + spare.Css + CorePageCloseAction.Css)}}
            </head>
            <body>
              <main class="veriqa-card">
                {{HopLaunchPageHeading.Markup(content.ChannelType, content.IconSvgPath, pageTitle)}}
                {{subjectLine}}
                <p class="veriqa-instruction" {{HopInterstitialWatchScript.ControlAttribute}}>{{WebUtility.HtmlEncode(message)}}</p>
                {{hintLine}}
                <a id="{{HopInterstitialScript.LaunchButtonId}}" href="{{WebUtility.HtmlEncode(content.Target)}}" class="veriqa-btn {{ChannelButton.CssClass(content.ChannelType)}}" {{HopInterstitialWatchScript.ControlAttribute}}>{{WebUtility.HtmlEncode(buttonText)}}</a>
                <div id="{{HopInterstitialWatchScript.StatusElementId}}" class="veriqa-status" role="status" aria-live="polite" hidden></div>
                {{close}}
                {{spare.Markup}}
              </main>
              {{CorePageAttribution.BuildLine(contentLanguage)}}
              <script{{nonceAttribute}}>{{PageStatusBlock.Js}}</script>
              <script{{nonceAttribute}}>{{watchScript}}</script>
              {{CorePageCloseAction.Script(cspNonce)}}
              {{spare.Scripts}}
              <script{{nonceAttribute}}>{{script}}</script>
            </body>
            </html>
            """;
    }

    /// <summary>
    /// The spare start code of the page: its markup, the rules of the block and the scripts that bind the
    /// copying function. A page whose channel keeps no spare message, or whose code could not be read,
    /// has none of the three and is the page it would be without the feature.
    /// <para>
    /// A link, hidden until the script of the status line shows it, and the block it opens — the caption
    /// naming the chat, the code and one button that copies the code and opens the channel, worded as the
    /// button of the page of the start by a code (<see cref="HopCodeStartPage"/>). It is the same for a
    /// sign-in and a confirmation; only the words of the link name the act. The block is on screen in the
    /// markup and hidden by the script, so a browser that runs no script shows the code from the load and
    /// never the link, and the button of the block is a plain link into the channel there. Both elements
    /// carry the attribute of the controls the outcome of the transaction takes the place of.
    /// </para>
    /// </summary>
    /// <param name="content">What the page shows.</param>
    /// <param name="nonceAttribute">Ready nonce attribute of an inline script, leading space included; empty — none.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides stated by the transaction's levels.</param>
    /// <param name="contentLanguage">Content language of the page.</param>
    /// <returns>The three parts; all empty — the page shows no spare start code.</returns>
    private static SpareStartCodeParts SpareStartCode(
        HopInterstitialContent content,
        string nonceAttribute,
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string contentLanguage)
    {
        if (content.StartMessage is not { } startMessage)
        {
            return new SpareStartCodeParts(string.Empty, string.Empty, string.Empty);
        }

        var wrapperAttributes =
            $"id=\"{HopInterstitialWatchScript.SpareElementId}\" class=\"{HopLaunchPageHeading.SpareClass}\" {HopInterstitialWatchScript.ControlAttribute}";

        // Named by the rule the tabs of the sign-in window are named with, as on the page of the start
        // by a code, so the two pages never call one channel two names.
        var channelName = ChannelNames.LocalizeChannelName(
            localizer, texts, content.ChannelType, content.ChannelDisplayName, contentLanguage);
        var block = ChannelStartCodeBlock.OpenBlockMarkup(
            ChannelStartCodeBlock.CaptionText(localizer, texts, content.BotName, contentLanguage),
            startMessage.DisplayText,
            content.Target,
            content.ChannelType,
            ChannelStartCodeBlock.CopyAndOpenText(localizer, texts, channelName, contentLanguage),
            string.Empty);

        // The block and the link that opens it are one for every transaction; only the words of the link
        // name the act.
        var linkKey = HopLaunchPageHeading.IsSignIn(content.TransactionType)
            ? AuthPageStrings.ProblemSigningIn
            : AuthPageStrings.ProblemConfirming;
        var linkText = WebUtility.HtmlEncode(texts.Localize(localizer, linkKey, contentLanguage));

        var markup = $"""
            <div id="{HopInterstitialWatchScript.SpareRevealElementId}" {HopInterstitialWatchScript.ControlAttribute} hidden><a id="{HopInterstitialWatchScript.SpareLinkId}" class="{HopLaunchPageHeading.TroubleLinkClass}" href="#{HopInterstitialWatchScript.SpareElementId}" aria-controls="{HopInterstitialWatchScript.SpareElementId}" aria-expanded="false">{linkText}</a></div>
            <div {wrapperAttributes}>{block}</div>
            """;

        // The settings of the copying function as a JSON literal, written with the encoder of the data
        // blocks of the contour: it escapes what could end the script element, whatever the message holds.
        var copySettings = JsonSerializer.Serialize(
            ChannelStartCodeBlock.ScriptSettings(
                localizer,
                texts,
                new Dictionary<string, string>(StringComparer.Ordinal) { [content.ChannelType] = startMessage.Text },
                contentLanguage),
            AuthPageScript.ConfigSerializerOptions);

        var scripts = $"""
            <script{nonceAttribute}>{ChannelStartCodeBlock.Js}</script>
              <script{nonceAttribute}>window.veriqaStartMessage(document, {copySettings});</script>
            """;

        return new SpareStartCodeParts(markup, ChannelStartCodeBlock.Css, scripts);
    }

    /// <summary>
    /// The lines of the status line, in the content language of the page. The line of a begun transition
    /// arrives ready (the line of the channel once the answer is awaited in it, for a sign-in and a
    /// confirmation alike); once the person is back on the page, the Email channel — whose target here is
    /// the mail itself — asks to wait for the confirmation, and any other channel keeps that line. The line
    /// that sends the person back to the sign-in window once the core asks its question there is the one the
    /// scanner page shows for that state. The line of a confirmed transaction still being completed is the
    /// one the sign-in window shows for that state. A refusal by the user reads as a refusal for a sign-in
    /// and a confirmation alike, and a failure the transaction states no wording for takes the line of the
    /// sign-in window — as on the page of the channel buttons.
    /// </summary>
    /// <param name="content">What the page shows.</param>
    /// <param name="lines">The lines the page ends on, worded in the content language of the page.</param>
    /// <param name="launched">The line of a begun transition, ready; empty — none.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides stated by the transaction's levels.</param>
    /// <param name="contentLanguage">Content language of the page.</param>
    /// <returns>The lines.</returns>
    private static HopInterstitialWatchTexts WatchTexts(
        HopInterstitialContent content,
        TransactionOutcomeLines lines,
        string launched,
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string contentLanguage)
    {
        var error = lines.Error ?? texts.Localize(localizer, AuthPageStrings.LoginError, contentLanguage);

        // Back from the mail client the mail may already be on its way, so the page asks to wait for the
        // confirmation instead of asking again to send it; any other channel has nothing new to say then.
        var returned = content.ChannelType is ChannelTypes.Email
            ? texts.Localize(localizer, AuthPageStrings.EmailReturnWait, contentLanguage)
            : launched;

        // The question is somewhere else: a sign-in asks it on the sign-in window; a confirmation leaves for
        // it, unless the answer is taken only from the browser the link of the letter was opened in — the
        // question is open there already, and the page says so.
        var questionElsewhere = HopLaunchPageHeading.IsSignIn(content.TransactionType)
            ? texts.Localize(localizer, AuthPageStrings.ReturnToSignInWindow, contentLanguage)
            : texts.Localize(localizer, AuthPageStrings.EmailLinkOpenedConfirmation, contentLanguage);

        return new HopInterstitialWatchTexts(
            launched,
            returned,
            questionElsewhere,
            texts.Localize(localizer, AuthPageStrings.ConfirmedProcessing, contentLanguage),
            lines.Confirmed,
            lines.Expired,
            lines.Declined,
            error);
    }

    /// <summary>
    /// The parts of the spare start code of the page.
    /// </summary>
    /// <param name="Markup">Ready markup, placed under the status line; empty — none.</param>
    /// <param name="Css">Rules of the block of the start by a code; empty — the page does not show it.</param>
    /// <param name="Scripts">Ready script elements that bind the copying function; empty — none.</param>
    private sealed record SpareStartCodeParts(string Markup, string Css, string Scripts);
}
