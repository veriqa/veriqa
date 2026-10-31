// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The instruction of the launch page: what the person is about to see on the way into the channel — the
/// general text naming the channel, the same on every device. The Email channel, whose target opens a mail
/// and not an app of the channel, keeps the text that names nothing. What a phone may show on the way into
/// one channel in particular is the hint of that channel, a message of the template mechanism the page
/// shows under the instruction (<see cref="CorePageChannelLineResolver.ForLaunchPageAsync"/>).
/// </summary>
internal static class HopLaunchInstructions
{
    /// <summary>
    /// Natural Key of the instruction that names no channel: the app opens by itself, the button is the
    /// way through when it does not.
    /// </summary>
    private const string General = "The app should open by itself. If it does not, tap the button below.";

    /// <summary>
    /// Natural Key of the instruction that names the channel; the slot is its name.
    /// </summary>
    private const string GeneralOfChannel = "The {0} app should open by itself. If it does not, tap the button below.";

    /// <summary>
    /// The slot of <see cref="GeneralOfChannel"/> the name of the channel goes into.
    /// </summary>
    private const string ChannelNameSlot = "{0}";

    /// <summary>
    /// The texts of the instruction an owning level may override (SPEC-012 §4.3, key <c>AuthPage.Texts</c>).
    /// </summary>
    internal static IReadOnlyList<string> OverridableTexts { get; } = [General, GeneralOfChannel];

    /// <summary>
    /// Returns the instruction for a channel. The slot is replaced as text rather than
    /// formatted — an override of a text is the integrator's and may carry braces of its own; a text
    /// without the slot is returned as it is.
    /// </summary>
    /// <param name="content">What the page shows: the channel and its name.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides stated by the transaction's levels.</param>
    /// <param name="contentLanguage">Content language of the page.</param>
    /// <returns>The instruction, not encoded.</returns>
    public static string Text(
        HopInterstitialContent content,
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string contentLanguage)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(texts);

        var naturalKey = content.ChannelType is ChannelTypes.Email ? General : GeneralOfChannel;

        return texts.Localize(localizer, naturalKey, contentLanguage).Replace(
            ChannelNameSlot,
            ChannelNames.LocalizeChannelName(
                localizer, texts, content.ChannelType, content.ChannelDisplayName, contentLanguage),
            StringComparison.Ordinal);
    }
}
