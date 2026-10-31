// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Pipeline;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The one rule a channel is named by to the user, and the one table of the names of the built-in
/// messengers. The pages of the core name a channel by it on its button, its tab and in the instruction;
/// the text of a confirmation question names the channel it is asked in by the same rule, so a channel
/// is called the same in the chat and on a page.
/// </summary>
internal static class ChannelNames
{
    /// <summary>
    /// Name of the Telegram channel — on its button, its tab, in the instruction and in the text of a
    /// question. A key with a context label: its base text is the brand name, and a language may name the
    /// channel its own way.
    /// </summary>
    public const string TelegramChannelName = "Telegram | channel";

    /// <summary>
    /// Name of the WhatsApp channel, as <see cref="TelegramChannelName"/>.
    /// </summary>
    public const string WhatsAppChannelName = "WhatsApp | channel";

    /// <summary>
    /// Name of the Instagram channel, as <see cref="TelegramChannelName"/>.
    /// </summary>
    public const string InstagramChannelName = "Instagram | channel";

    /// <summary>
    /// Name of the Messenger channel, as <see cref="TelegramChannelName"/>.
    /// </summary>
    public const string MessengerChannelName = "Messenger | channel";

    /// <summary>
    /// Name of the WeChat channel, as <see cref="TelegramChannelName"/>.
    /// </summary>
    public const string WeChatChannelName = "WeChat | channel";

    /// <summary>
    /// Name of the Viber channel, as <see cref="TelegramChannelName"/>.
    /// </summary>
    public const string ViberChannelName = "Viber | channel";

    /// <summary>
    /// Name of the MAX channel, as <see cref="TelegramChannelName"/>.
    /// </summary>
    public const string MaxChannelName = "MAX | channel";

    /// <summary>
    /// Untranslated name of the Email channel. Not a Natural Key: the Email channel has no name key, its
    /// button names an act rather than a channel.
    /// </summary>
    private const string EmailChannelName = "Email";

    /// <summary>
    /// Context label of the key of the name of a channel (<see cref="NameKeyOf"/>): the key is the
    /// untranslated name followed by this label, as the constants of the built-in messengers spell it. A
    /// read-only field rather than a constant: every constant of this class is a Natural Key the locale
    /// files hold, and a context label is not one.
    /// </summary>
    private static readonly string ChannelNameContext = "channel";

    /// <summary>
    /// The names of the built-in messengers (<see cref="TelegramChannelName"/> and the rest), each with its
    /// channel type: the name a built-in type is called by when its adapter declares no label of its own.
    /// Email is not one of them: its button names an act, not a channel. The one map of a type to its default
    /// name: <see cref="GetChannelTabName"/> reads it, and so do the overridable texts of the pages.
    /// </summary>
    public static IReadOnlyList<(string ChannelType, string NameKey)> BuiltInChannelNames { get; } =
    [
        (ChannelTypes.Telegram, TelegramChannelName),
        (ChannelTypes.WhatsApp, WhatsAppChannelName),
        (ChannelTypes.Instagram, InstagramChannelName),
        (ChannelTypes.Messenger, MessengerChannelName),
        (ChannelTypes.WeChat, WeChatChannelName),
        (ChannelTypes.Viber, ViberChannelName),
        (ChannelTypes.Max, MaxChannelName)
    ];

    /// <summary>
    /// Returns the Natural Key of the name of a channel — the one place the key is built: the untranslated
    /// name (<see cref="GetChannelName"/>) followed by the context label <c>channel</c>, so its base text is
    /// the name itself and a channel nobody translated is called as declared. For a built-in messenger without
    /// a declared label the key is its constant (<see cref="TelegramChannelName"/> and the rest); for any other
    /// channel it is a key a locale file of the deployment, or an override of a level, may state. Null for
    /// Email, whose button names an act rather than a channel.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <param name="displayName">Label the adapter declared; null — none.</param>
    /// <returns>Natural Key of the name; null for Email.</returns>
    public static string? NameKeyOf(string channelType, string? displayName)
    {
        if (channelType is ChannelTypes.Email)
        {
            return null;
        }

        return GetChannelName(channelType, displayName) + NaturalKeyContext.Separator + ChannelNameContext;
    }

    /// <summary>
    /// Tells whether the name of a channel is a text of the core's own: the default name of a built-in
    /// messenger (<see cref="BuiltInChannelNames"/>) its adapter declared no label over, or the Email channel.
    /// Any other name comes from the adapter — a declared label or the raw type.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <param name="displayName">Label the adapter declared; null — none.</param>
    /// <returns><c>true</c> when the core owns the name.</returns>
    public static bool IsCoreOwnName(string channelType, string? displayName)
    {
        if (channelType is ChannelTypes.Email)
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(displayName) && BuiltInNameKeyOf(channelType) is not null;
    }

    /// <summary>
    /// Returns the untranslated name of a channel type: the base text of the name of a built-in messenger
    /// (<see cref="BuiltInChannelNames"/>), <c>Email</c> for the Email channel, the raw type of any other channel.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <returns>Channel display name.</returns>
    public static string GetChannelTabName(string channelType)
    {
        if (BuiltInNameKeyOf(channelType) is { } nameKey)
        {
            return NaturalKeyContext.BaseTextOf(nameKey);
        }

        return channelType is ChannelTypes.Email ? EmailChannelName : channelType;
    }

    /// <summary>
    /// Returns the name a channel is labelled with, untranslated: the label a third-party adapter declared
    /// through <c>IChannelDisplayMetadata</c>, otherwise the brand name of a built-in channel, otherwise the
    /// raw channel type. The entries of the confirmation creation answer read this rule as it is; the pages
    /// and the text of a question read the same name translated (<see cref="LocalizeChannelName"/>), so a
    /// channel is named by one rule on every surface.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <param name="displayName">Label the adapter declared; null — none.</param>
    /// <returns>Channel name.</returns>
    public static string GetChannelName(string channelType, string? displayName) =>
        string.IsNullOrWhiteSpace(displayName)
            ? GetChannelTabName(channelType)
            : displayName;

    /// <summary>
    /// Returns the name a channel is labelled with in a language: the name of <see cref="GetChannelName"/>
    /// taken in that language by its key (<see cref="NameKeyOf"/>), through the text overrides of a page — so
    /// a language, or an owning level, that renames the channel renames it on its button, its tab and in the
    /// instruction at once. That holds for every channel but Email, the declared label of a third-party
    /// channel included: a key no locale file states resolves to its base text, the name as declared.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="texts">Text overrides of the page; <see cref="CorePageTexts.None"/> — none.</param>
    /// <param name="channelType">Channel type.</param>
    /// <param name="displayName">Label the adapter declared; null — none.</param>
    /// <param name="language">Language of the text.</param>
    /// <returns>Channel name.</returns>
    public static string LocalizeChannelName(
        IConfirmationPromptLocalizer localizer,
        CorePageTexts texts,
        string channelType,
        string? displayName,
        string language)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var nameKey = NameKeyOf(channelType, displayName);

        return nameKey is null
            ? GetChannelName(channelType, displayName)
            : texts.Localize(localizer, nameKey, language);
    }

    /// <summary>
    /// Names a channel by the rule of <see cref="LocalizeChannelName"/> without text overrides: the name of
    /// the channel — the label the registered adapter of the channel declared for itself
    /// (<see cref="ReadDeclaredMetadata"/>), otherwise the name of a built-in messenger, otherwise the channel
    /// type — in the language of the text.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="language">Language of the text.</param>
    /// <param name="logger">Logger the rejection of an unusable declared label is reported to.</param>
    /// <returns>Name of the channel for display.</returns>
    public static string NameChannel(
        string channelType,
        IEnumerable<IChannelAdapter> adapters,
        IConfirmationPromptLocalizer localizer,
        string language,
        ILogger logger)
    {
        var adapter = adapters.FirstOrDefault(
            candidate => string.Equals(candidate.ChannelType, channelType, StringComparison.Ordinal));
        var declaredName = adapter is null ? null : ReadDeclaredMetadata(adapter, logger).DisplayName;

        return LocalizeChannelName(localizer, CorePageTexts.None, channelType, declaredName, language);
    }

    /// <summary>
    /// Reads the display metadata a third-party adapter declared for itself (public SPI) — the one reading
    /// of it, so a channel is named the same on every surface: the label degraded to a safe value
    /// (<see cref="DeclaredNameOrNull"/>), the glyph markup as declared, for the pages to check. The
    /// properties are third-party code, so nothing they throw is let through: a failure of either is logged
    /// and the channel goes without both — including a spurious timeout, which is why the catch is
    /// unfiltered (there is no cancellation token on this path).
    /// </summary>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="logger">Logger a rejected label or a failure is reported to.</param>
    /// <returns>The label and the glyph markup; null for each the adapter declares none of, and both when
    /// reading them failed.</returns>
    public static (string? DisplayName, string? IconSvgPath) ReadDeclaredMetadata(
        IChannelAdapter adapter,
        ILogger logger)
    {
        if (adapter is not IChannelDisplayMetadata displayMetadata)
        {
            return (null, null);
        }

        string? displayName;
        string? iconSvgPath;

        try
        {
            displayName = displayMetadata.DisplayName;
            iconSvgPath = displayMetadata.IconSvgPath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Channel {ChannelType} failed to provide display metadata — rendering without it",
                adapter.ChannelType);
            return (null, null);
        }

        return (DeclaredNameOrNull(adapter.ChannelType, displayName, logger), iconSvgPath);
    }

    /// <summary>
    /// Degrades a label an adapter declared to a safe value: a blank label becomes null (the caller falls
    /// back to the channel type — an empty string would render a nameless tab and button), and an over-long
    /// label is dropped (an unbounded label stretches the CTA past the card).
    /// </summary>
    /// <param name="channelType">Channel type the label belongs to.</param>
    /// <param name="displayName">Label as the adapter declared it.</param>
    /// <param name="logger">Logger a rejected label is reported to.</param>
    /// <returns>The label; null when it is absent or rejected.</returns>
    public static string? DeclaredNameOrNull(string channelType, string? displayName, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        if (displayName.Length > CustomChannelConstants.MaxDisplayNameLength)
        {
            logger.LogWarning(
                "Channel {ChannelType} declared a display label of {Length} characters, over the " +
                "{Limit}-character limit — falling back to the channel type",
                channelType,
                displayName.Length,
                CustomChannelConstants.MaxDisplayNameLength);
            return null;
        }

        return displayName;
    }

    /// <summary>
    /// Returns the constant of the name of a built-in messenger (<see cref="BuiltInChannelNames"/>); null for
    /// a type the catalog does not name.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <returns>Natural Key of the default name; null — none.</returns>
    private static string? BuiltInNameKeyOf(string channelType)
    {
        foreach (var (type, nameKey) in BuiltInChannelNames)
        {
            if (string.Equals(type, channelType, StringComparison.Ordinal))
            {
                return nameKey;
            }
        }

        return null;
    }
}
