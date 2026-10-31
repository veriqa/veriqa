// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// The block that shows the message of the start by a code, as every page of the contour shows it: the
/// same element, the same rules and the same copying function, so one message never looks or behaves
/// differently on two surfaces.
/// <para>
/// Norms the script and the rules implement. They are named here and not in the files themselves, whose
/// comments are inlined into the page and reach the end user's browser (see
/// <see cref="CorePageEmbeddedText"/>):
/// </para>
/// <list type="bullet">
/// <item><description>the message of the block that stands where the button of a channel would is on
/// screen from the load and stays there: plain text in the monospace face of the contour, selectable as a
/// whole, never revealed by a press;</description></item>
/// <item><description>a control carrying <see cref="MessageAttribute"/> puts the message into the clipboard
/// when pressed. The value of the attribute is a key of the map the page hands over, never the message
/// itself. A link among such controls is followed by the browser as any link is: the script neither delays
/// nor cancels the navigation, so a clipboard that refuses — an insecure context, a denied permission —
/// costs the person the copy and nothing else;</description></item>
/// <item><description>the outcome of the copy is told in the status line of the block, a live region that
/// is empty and on screen from the load; a refused copy leaves it empty;</description></item>
/// <item><description>the spare form of the block (<see cref="SpareBlockClass"/>) stands under the button of
/// a channel whose link does carry the transaction: the link is the way in, the message is what is left
/// when the link did not deliver. So it is the lesser thing of its panel — set apart by a rule, in the
/// muted color and a smaller size, with an outlined button that only copies — and the channel button
/// stays the main action. The message and its button share one line; a message too long for it wraps
/// beside the button rather than pushing it out of the card. It carries the caption of the block itself,
/// and the page that holds it decides when it is on screen: it is offered only while the bot of the
/// channel has not answered, and a page that learns of the answer takes it away.</description></item>
/// </list>
/// </summary>
internal static class ChannelStartCodeBlock
{
    /// <summary>
    /// Resource name of the copying function (<c>UI/Scripts/veriqa-start-code.js</c>).
    /// </summary>
    private const string ScriptResourceName = "Veriqa.Core.AuthServer.UI.Scripts.veriqa-start-code.js";

    /// <summary>
    /// Resource name of the rules (<c>UI/Styles/veriqa-start-code.css</c>).
    /// </summary>
    private const string StylesResourceName = "Veriqa.Core.AuthServer.UI.Styles.veriqa-start-code.css";

    /// <summary>
    /// Slot of the channel name in the texts of the block and of the pages that name the channel around it.
    /// </summary>
    public const string ChannelSlot = "{0}";

    /// <summary>
    /// Slot of the name of the bot in <see cref="SendCodeToBotCaption"/>.
    /// </summary>
    private const string BotNameSlot = "{0}";

    /// <summary>
    /// Natural Key of the caption over the message when the name of the chat is known; the slot is that
    /// name.
    /// </summary>
    public const string SendCodeToBotCaption =
        "Send the code in the chat with the bot {0} if the bot has not sent you a confirmation";

    /// <summary>
    /// Natural Key of the caption over the message when the channel states no name of its chat.
    /// </summary>
    public const string SendCodeCaption =
        "Send the code in the chat with the bot if the bot has not sent you a confirmation";

    /// <summary>
    /// Natural Key of the button that copies the message and opens the channel; the slot is the name of
    /// the channel.
    /// </summary>
    public const string CopyAndOpen = "Copy and open {0}";

    /// <summary>
    /// Natural Key of the status line after a copy that succeeded.
    /// </summary>
    public const string Copied = "Copied";

    /// <summary>
    /// Natural Key of the button of the spare form, which copies the message and leads nowhere.
    /// </summary>
    public const string Copy = "Copy";

    /// <summary>
    /// Attribute of a control that copies the message. Its value is the key of the message in
    /// <see cref="ChannelStartCodeScriptSettings.Messages"/>.
    /// </summary>
    public const string MessageAttribute = "data-veriqa-start-message";

    /// <summary>
    /// Class of the block — the element the copying function looks the status line up in.
    /// </summary>
    public const string BlockClass = "veriqa-start-code";

    /// <summary>
    /// Class of the caption over the message.
    /// </summary>
    public const string CaptionClass = "veriqa-start-code-caption";

    /// <summary>
    /// Class of the message.
    /// </summary>
    public const string MessageClass = "veriqa-start-code-message";

    /// <summary>
    /// Class of the status line of the block.
    /// </summary>
    public const string StatusClass = "veriqa-start-code-status";

    /// <summary>
    /// Modifier of the spare form of the block.
    /// </summary>
    public const string SpareBlockClass = "veriqa-start-code--spare";

    /// <summary>
    /// Class of the button of the spare form.
    /// </summary>
    public const string CopyClass = "veriqa-start-code-copy";

    /// <summary>
    /// The texts of the block an owning level may override (SPEC-012 §4.3, key <c>AuthPage.Texts</c>).
    /// </summary>
    internal static IReadOnlyList<string> OverridableTexts { get; } =
        [SendCodeToBotCaption, SendCodeCaption, CopyAndOpen, Copied, Copy];

    /// <summary>
    /// The copying function, inlined into a page ahead of the script that calls it:
    /// <c>window.veriqaStartMessage(root, settings)</c>, where <c>settings</c> is
    /// <see cref="ChannelStartCodeScriptSettings"/> in camelCase.
    /// </summary>
    public static string Js => CorePageEmbeddedText.Load(typeof(ChannelStartCodeBlock).Assembly, ScriptResourceName);

    /// <summary>
    /// The rules of the block, loaded after the token layer of the contour next to the rules of the page.
    /// </summary>
    public static string Css => CorePageEmbeddedText.Load(typeof(ChannelStartCodeBlock).Assembly, StylesResourceName);

    /// <summary>
    /// Returns the caption over the message, the same in both forms of the block: where to send the code,
    /// naming the chat when its name is known. Without a name the caption is worded without one — the name of the channel is
    /// not put in its place. The slot is replaced as text rather than formatted: an override of the text
    /// is the integrator's and may carry braces of its own.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="botName">Name of the chat, as configured or as the channel states it; null or blank — none.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>Caption text, not encoded.</returns>
    public static string CaptionText(
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string? botName,
        string language)
    {
        ArgumentNullException.ThrowIfNull(texts);

        return string.IsNullOrWhiteSpace(botName)
            ? texts.Localize(localizer, SendCodeCaption, language)
            : texts.Localize(localizer, SendCodeToBotCaption, language)
                .Replace(BotNameSlot, botName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the text of the button that copies the message and opens the channel.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="channelName">Name of the channel, by the rule the channel tabs are named with.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>Button text, not encoded.</returns>
    public static string CopyAndOpenText(
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string channelName,
        string language) =>
        Named(localizer, texts, CopyAndOpen, channelName, language);

    /// <summary>
    /// Returns the text of the button of the spare form.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>Button text, not encoded.</returns>
    public static string CopyText(
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string language)
    {
        ArgumentNullException.ThrowIfNull(texts);

        return texts.Localize(localizer, Copy, language);
    }

    /// <summary>
    /// Builds what the copying function needs to know about a page.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="messages">Messages of the page by the key their controls carry.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>Settings of the copying function.</returns>
    public static ChannelStartCodeScriptSettings ScriptSettings(
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        IReadOnlyDictionary<string, string> messages,
        string language)
    {
        ArgumentNullException.ThrowIfNull(texts);

        return new ChannelStartCodeScriptSettings(
            Attribute: MessageAttribute,
            BlockSelector: "." + BlockClass,
            StatusSelector: "." + StatusClass,
            CopiedText: texts.Localize(localizer, Copied, language),
            Messages: messages);
    }

    /// <summary>
    /// Builds the block: the caption, the message and, under them, the action and the status line. Every
    /// text is HTML-encoded here; the action arrives as ready markup.
    /// </summary>
    /// <param name="captionText">Caption over the message, not encoded.</param>
    /// <param name="displayMessage">The message as a person reads it, not encoded.</param>
    /// <param name="actionMarkup">
    /// Ready markup of the control of the block (<see cref="OpenAction"/> or <see cref="CopyAction"/>).
    /// </param>
    /// <param name="isSpare">
    /// <c>true</c> — the spare form of the block, shown under the button of a channel whose link carries
    /// the transaction; <c>false</c> — the block that stands where the button of a channel would.
    /// </param>
    /// <returns>Ready markup of the block.</returns>
    public static string Markup(string captionText, string displayMessage, string actionMarkup, bool isSpare = false)
    {
        var blockClasses = isSpare ? $"{BlockClass} {SpareBlockClass}" : BlockClass;

        // The message is not translated by the browser: it is a command of the channel and a code.
        return $"""
            <div class="{blockClasses}">
            <p class="{CaptionClass}">{WebUtility.HtmlEncode(captionText)}</p>
            <p class="{MessageClass}" translate="no">{WebUtility.HtmlEncode(displayMessage)}</p>
            {actionMarkup}
            <p class="{StatusClass}" role="status" aria-live="polite"></p>
            </div>
            """;
    }

    /// <summary>
    /// Builds the block whose one action copies the message and opens the channel, as a page held by the phone
    /// that sends the code offers it — a launch page of the hop, and the scanner page under its status line:
    /// the caption, the message and a link styled as the button of the channel,
    /// worded "Copy and open" with the name of the channel. The key of the message is the channel type.
    /// </summary>
    /// <param name="captionText">Caption over the message, not encoded (<see cref="CaptionText"/>).</param>
    /// <param name="displayMessage">The message as a person reads it, not encoded.</param>
    /// <param name="href">Link of the channel, not encoded.</param>
    /// <param name="channelType">Type of the channel — the class of its button and the key of the message.</param>
    /// <param name="copyAndOpenText">Text of the button, not encoded (<see cref="CopyAndOpenText"/>).</param>
    /// <param name="extraAttributes">
    /// Ready markup of further attributes of the link, leading space included; empty — none.
    /// </param>
    /// <returns>Ready markup of the block.</returns>
    public static string OpenBlockMarkup(
        string captionText,
        string displayMessage,
        string href,
        string channelType,
        string copyAndOpenText,
        string extraAttributes)
    {
        var action = OpenAction(
            href,
            $"veriqa-btn {ChannelButton.CssClass(channelType)}",
            WebUtility.HtmlEncode(copyAndOpenText),
            channelType,
            extraAttributes);

        return Markup(captionText, displayMessage, action);
    }

    /// <summary>
    /// Builds the action that copies the message and opens the channel: a link to the channel that also
    /// carries the key of the message.
    /// </summary>
    /// <param name="href">Link of the channel, not encoded.</param>
    /// <param name="cssClass">Classes of the link, as the page styles its channel button.</param>
    /// <param name="innerMarkup">Ready inner markup of the link — the icon and the encoded text.</param>
    /// <param name="messageKey">Key of the message in the map the page hands to the copying function.</param>
    /// <param name="extraAttributes">
    /// Ready markup of further attributes of the link, leading space included; empty — none.
    /// </param>
    /// <returns>Ready markup of the action.</returns>
    public static string OpenAction(
        string href,
        string cssClass,
        string innerMarkup,
        string messageKey,
        string extraAttributes)
    {
        return $"<a href=\"{WebUtility.HtmlEncode(href)}\" class=\"{cssClass}\" target=\"_blank\" rel=\"noopener noreferrer\""
            + $" {MessageAttribute}=\"{WebUtility.HtmlEncode(messageKey)}\"{extraAttributes}>{innerMarkup}</a>";
    }

    /// <summary>
    /// Builds the action of the spare form: a button that copies the message and does nothing else. It is
    /// a button and not a link, so a press never leaves the page.
    /// </summary>
    /// <param name="text">Text of the button, not encoded.</param>
    /// <param name="messageKey">Key of the message in the map the page hands to the copying function.</param>
    /// <returns>Ready markup of the action.</returns>
    public static string CopyAction(string text, string messageKey)
    {
        return $"<button type=\"button\" class=\"veriqa-btn veriqa-btn-decline {CopyClass}\""
            + $" {MessageAttribute}=\"{WebUtility.HtmlEncode(messageKey)}\">{WebUtility.HtmlEncode(text)}</button>";
    }

    /// <summary>
    /// Localizes a text of the block and puts the name of the channel into its slot. The slot is replaced
    /// as text rather than formatted: an override of the text is the integrator's and may carry braces
    /// of its own.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="naturalKey">Natural Key of the text.</param>
    /// <param name="channelName">Name of the channel.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>Text naming the channel, not encoded.</returns>
    private static string Named(
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string naturalKey,
        string channelName,
        string language)
    {
        ArgumentNullException.ThrowIfNull(texts);

        return texts.Localize(localizer, naturalKey, language)
            .Replace(ChannelSlot, channelName, StringComparison.Ordinal);
    }
}

/// <summary>
/// What the copying function of <see cref="ChannelStartCodeBlock"/> is told about a page. It travels into
/// the page inside the JSON data of the page script, in camelCase; a renamed member renames the field the
/// function reads, and the pair of names is checked by reading the two files together.
/// </summary>
/// <param name="Attribute">Name of the attribute of a control that copies; its value is a key of <paramref name="Messages"/>.</param>
/// <param name="BlockSelector">Selector of the block a control belongs to.</param>
/// <param name="StatusSelector">Selector of the status line inside the block.</param>
/// <param name="CopiedText">Localized text of the status line after a copy that succeeded.</param>
/// <param name="Messages">
/// The messages by key, each with its code written without separators — what goes to the clipboard. A
/// message addresses its transaction and is handed over as data only, never as part of the markup of a
/// control.
/// </param>
internal sealed record ChannelStartCodeScriptSettings(
    string Attribute,
    string BlockSelector,
    string StatusSelector,
    string CopiedText,
    IReadOnlyDictionary<string, string> Messages);
