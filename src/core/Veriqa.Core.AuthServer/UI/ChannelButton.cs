// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// The button that leads into a channel, as every page of the contour words and paints it: the
/// sign-in window and the hop launch page ask here, so one channel is never named or colored
/// differently on the two surfaces.
/// </summary>
internal static class ChannelButton
{
    /// <summary>
    /// Returns the text of the button, through the text overrides of the page — so one override renames the
    /// button on every surface that shows it. A messenger is named in a template that follows the
    /// transaction — <see cref="AuthPageStrings.SignInWithChannel"/> for a sign-in,
    /// <see cref="AuthPageStrings.ConfirmInChannel"/> for any other — whose slot takes the name of the
    /// channel on the pages (<see cref="ChannelNames.LocalizeChannelName"/>): the name of the channel — the
    /// name of a built-in messenger, the label an adapter declared through <c>IChannelDisplayMetadata</c>, the
    /// raw channel type without one — in the language of the page.
    /// <para>
    /// SPEC-007 UI-121: the phrase of one channel outranks the template: the template with the untranslated name in its slot
    /// (<c>Confirm in WhatsApp</c>) is a key of its own, looked up first. The order — an override of the
    /// phrase, an override of the template, a translation of the phrase, a translation of the template — lets
    /// an integrator reword the button of one channel and a translator word a language whose grammar the
    /// template cannot hold.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The Email button names what the user does with it — sends the prepared confirmation email — so the
    /// same words fit a sign-in and a confirmation alike. Only an absolute target of the channel reaches a
    /// button through this rule (the form of the Pull mode has its own panel on the sign-in window), and
    /// that target is a letter to send, the same one the sign-in window offers under the same words.
    /// </remarks>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides of the page.</param>
    /// <param name="channelType">Channel type.</param>
    /// <param name="displayName">Label the adapter declared; null — none.</param>
    /// <param name="isSignIn">true — the transaction of the page is a sign-in.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>Button text.</returns>
    public static string Text(
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string channelType,
        string? displayName,
        bool isSignIn,
        string language)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(texts);

        if (channelType is ChannelTypes.Email)
        {
            return texts.Localize(localizer, AuthPageStrings.SendConfirmationEmail, language);
        }

        var template = isSignIn ? AuthPageStrings.SignInWithChannel : AuthPageStrings.ConfirmInChannel;
        var phrase = Phrase(template, ChannelNames.GetChannelName(channelType, displayName));

        if (texts.TryOverride(localizer, phrase, language, out var statedPhrase))
        {
            return statedPhrase;
        }

        var name = ChannelNames.LocalizeChannelName(localizer, texts, channelType, displayName, language);

        if (texts.TryOverride(localizer, template, language, out var statedTemplate))
        {
            return Phrase(statedTemplate, name);
        }

        // A phrase of one channel is optional in a locale file: the template covers every language that has
        // it, so the phrase counts only where it states a wording of its own.
        if (localizer.Resolve(phrase, language) is { } translatedPhrase
            && !string.Equals(translatedPhrase, phrase, StringComparison.Ordinal))
        {
            return translatedPhrase;
        }

        return Phrase(localizer.ResolveOrBaseText(template, language), name);
    }

    /// <summary>
    /// Returns the texts <see cref="Text"/> builds the button from besides the name of the channel — each a
    /// key an override of a level may state: the text of the Email button; for any other channel the phrase
    /// of the channel and the template of the transaction.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <param name="displayName">Label the adapter declared; null — none.</param>
    /// <param name="isSignIn">true — the transaction of the page is a sign-in.</param>
    /// <returns>Natural Keys of the texts of the button.</returns>
    internal static IReadOnlyList<string> SourceTexts(string channelType, string? displayName, bool isSignIn)
    {
        if (channelType is ChannelTypes.Email)
        {
            return [AuthPageStrings.SendConfirmationEmail];
        }

        var template = isSignIn ? AuthPageStrings.SignInWithChannel : AuthPageStrings.ConfirmInChannel;

        return [Phrase(template, ChannelNames.GetChannelName(channelType, displayName)), template];
    }

    /// <summary>
    /// Puts the name of a channel into the slot of a template of the button. The slot is replaced as text
    /// rather than formatted: an override of the template is the integrator's and may carry braces of its
    /// own.
    /// </summary>
    /// <param name="template">Template of the button.</param>
    /// <param name="channelName">Name of the channel.</param>
    /// <returns>The phrase.</returns>
    internal static string Phrase(string template, string channelName) =>
        template.Replace(ChannelStartCodeBlock.ChannelSlot, channelName, StringComparison.Ordinal);

    /// <summary>
    /// Returns the CSS modifiers of the button: the canonical brand color of a built-in messenger, the
    /// neutral primary for Email and for a third-party channel, and the class by which a color a level
    /// states for that one button reaches it (<see cref="CorePageChannelButtonColor"/>).
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <returns>CSS classes of the button.</returns>
    public static string CssClass(string channelType) => CorePageChannelButtonColor.CssClass(channelType);
}
