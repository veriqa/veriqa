// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// The heading that names the channel a person continues in, under the icon of that channel: the heading
/// of the two launch pages of the hop (<see cref="HopMode.HopInterstitialPage"/>,
/// <see cref="HopMode.HopCodeStartPage"/>) and of the scanner page once a channel was chosen there. The
/// launch pages serve a sign-in and a confirmation, so their wording follows the type of the transaction: a
/// sign-in is named as one, a confirmation keeps the neutral wording (<see cref="Text"/>). The scanner page
/// says where the person went, whatever the transaction (<see cref="ContinueText"/>).
/// <para>
/// The channel is named by the rule the tabs of the sign-in window are named with, and its icon is the one
/// of that window — the bundled glyph of a built-in channel in its brand color, or the glyph a third-party
/// adapter declared — in the color stated for the button of the channel, when one is stated
/// (<see cref="CorePageChannelButtonColor"/>). The title of a launch page is the same text without the icon.
/// </para>
/// </summary>
internal static class HopLaunchPageHeading
{
    /// <summary>
    /// Natural Key of the heading of a sign-in; the slot is the name of the channel.
    /// </summary>
    private const string SignInTitle = "Sign in via {0}";

    /// <summary>
    /// Natural Key of the heading of every other transaction; the slot is the name of the channel.
    /// </summary>
    private const string ContinueTitle = "Continue in {0}";

    /// <summary>
    /// Resource name of the rules of the heading and of the launch pages
    /// (<c>UI/Styles/veriqa-hop-launch.css</c>).
    /// </summary>
    private const string StylesResourceName = "Veriqa.Core.AuthServer.UI.Styles.veriqa-hop-launch.css";

    /// <summary>
    /// Class of the heading.
    /// </summary>
    public const string TitleClass = "veriqa-launch-title";

    /// <summary>
    /// Class of the element that holds the spare start code of a launch page.
    /// </summary>
    public const string SpareClass = "veriqa-launch-spare";

    /// <summary>
    /// Class of the link that opens the spare start code of a launch page. The link is the way to a code the
    /// person needs right now, so it reads as the link of the same purpose on the sign-in window does — bold,
    /// in the text color, not smaller than the instruction of the card — and not as the muted service link
    /// of the contour, whose rules it does not take.
    /// </summary>
    public const string TroubleLinkClass = "veriqa-launch-trouble";

    /// <summary>
    /// The texts of the heading an owning level may override (SPEC-012 §4.3, key <c>AuthPage.Texts</c>).
    /// </summary>
    internal static IReadOnlyList<string> OverridableTexts { get; } = [SignInTitle, ContinueTitle];

    /// <summary>
    /// The rules of the heading and of the launch pages, loaded after the rules of the service pages.
    /// </summary>
    public static string Css => CorePageEmbeddedText.Load(typeof(HopLaunchPageHeading).Assembly, StylesResourceName);

    /// <summary>
    /// The content language of a launch page (WCAG 3.1.1): the language of the transaction when the
    /// heading of the page is stated in it, the English base text otherwise. It is probed on the key of
    /// the heading the page shows, not on an override of it: an override changes a text, not the language
    /// the page can speak.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="transactionType">Type of the transaction of the page.</param>
    /// <param name="language">Language of the transaction's page.</param>
    /// <returns>The language code the page is rendered in.</returns>
    public static string ContentLanguage(
        IConfirmationPromptLocalizer localizer,
        string transactionType,
        string language)
    {
        var naturalKey = IsSignIn(transactionType) ? SignInTitle : ContinueTitle;

        return AuthPageLocalization.ResolveContentLanguage(localizer, naturalKey, language);
    }

    /// <summary>
    /// Returns the text of the heading of a launch page — the title of the document as well: a sign-in is
    /// named as one, any other transaction keeps the neutral wording.
    /// </summary>
    /// <param name="channelType">Type of the channel.</param>
    /// <param name="channelDisplayName">Name the channel states for itself; null — none.</param>
    /// <param name="transactionType">Type of the transaction of the page.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides stated by the transaction's levels.</param>
    /// <param name="contentLanguage">Content language of the page.</param>
    /// <returns>The text, not encoded.</returns>
    public static string Text(
        string channelType,
        string? channelDisplayName,
        string transactionType,
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string contentLanguage)
    {
        var naturalKey = IsSignIn(transactionType) ? SignInTitle : ContinueTitle;

        return Named(naturalKey, channelType, channelDisplayName, localizer, texts, contentLanguage);
    }

    /// <summary>
    /// Returns the text of the heading that says the person continues in the channel, whatever the type of
    /// the transaction — the heading of the scanner page once a channel was chosen there.
    /// </summary>
    /// <param name="channelType">Type of the channel.</param>
    /// <param name="channelDisplayName">Name the channel states for itself; null — none.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides stated by the transaction's levels.</param>
    /// <param name="contentLanguage">Content language of the page.</param>
    /// <returns>The text, not encoded.</returns>
    public static string ContinueText(
        string channelType,
        string? channelDisplayName,
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string contentLanguage) =>
        Named(ContinueTitle, channelType, channelDisplayName, localizer, texts, contentLanguage);

    /// <summary>
    /// Builds the heading: the icon of the channel, when it has one, and the text.
    /// </summary>
    /// <param name="channelType">Type of the channel.</param>
    /// <param name="iconSvgPath">Glyph markup a third-party adapter declared for the channel; null — none.</param>
    /// <param name="text">Text of the heading, not encoded.</param>
    /// <returns>Ready markup.</returns>
    public static string Markup(string channelType, string? iconSvgPath, string text)
    {
        var icon = ChannelIconSvg.Render(
            channelType,
            CorePageChannelButtonColor.LaunchIconCssClass(channelType),
            iconSvgPath);

        return $"<h1 class=\"veriqa-title {TitleClass}\">{icon}<span>{WebUtility.HtmlEncode(text)}</span></h1>";
    }

    /// <summary>
    /// Tells whether the transaction of a launch page is a sign-in — the one type the texts that speak of
    /// signing in are shown for.
    /// </summary>
    /// <param name="transactionType">Type of the transaction.</param>
    /// <returns>true — a sign-in.</returns>
    public static bool IsSignIn(string transactionType) =>
        string.Equals(transactionType, TransactionTypes.Login, StringComparison.Ordinal);

    /// <summary>
    /// Localizes a text of the heading and puts the name of the channel into its slot. The slot is replaced
    /// as text rather than formatted: an override of the text is the integrator's and may carry braces of
    /// its own.
    /// </summary>
    /// <param name="naturalKey">Natural Key of the text.</param>
    /// <param name="channelType">Type of the channel.</param>
    /// <param name="channelDisplayName">Name the channel states for itself; null — none.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides stated by the transaction's levels.</param>
    /// <param name="contentLanguage">Content language of the page.</param>
    /// <returns>The text, not encoded.</returns>
    private static string Named(
        string naturalKey,
        string channelType,
        string? channelDisplayName,
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string contentLanguage)
    {
        ArgumentNullException.ThrowIfNull(texts);

        return texts.Localize(localizer, naturalKey, contentLanguage).Replace(
            ChannelStartCodeBlock.ChannelSlot,
            ChannelNames.LocalizeChannelName(localizer, texts, channelType, channelDisplayName, contentLanguage),
            StringComparison.Ordinal);
    }
}
