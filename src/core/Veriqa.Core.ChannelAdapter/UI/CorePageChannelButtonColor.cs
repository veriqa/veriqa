// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text;
using System.Text.RegularExpressions;

using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The classes of the button that leads into a channel and of the icons that stand for the channel, and the
/// color a deployment states for the button of one channel (SPEC-012 CFG-269, key
/// <c>AuthPageDesign.ChannelButtonColor</c>). The button of a built-in channel is addressed by the class of
/// its color, which no other channel shares; the button of a third-party channel shares
/// <c>veriqa-btn-default</c> with every other one and carries a class naming its channel besides. An icon
/// of a channel carries the modifier naming its channel — the icon of a tab, of the heading of a panel of
/// the grid layout and of the instruction of a page with one channel, and the icon of the heading of a
/// launch page of the hop — and the stated color paints it as it paints the button. Both sides live here,
/// so the class a page puts on a button or an icon and the selector the head paints can never be spelled
/// apart.
/// </summary>
internal static partial class CorePageChannelButtonColor
{
    /// <summary>
    /// Prefix of the class that names the channel of a third-party button; the channel type follows it.
    /// </summary>
    private const string ThirdPartyClassPrefix = "veriqa-btn-of-";

    /// <summary>
    /// Class of the neutral color every third-party channel shares.
    /// </summary>
    private const string ThirdPartyColorClass = "veriqa-btn-default";

    /// <summary>
    /// Class of the icon that stands beside the name of a channel on the sign-in window: in its tab, over
    /// its panel in the grid layout and in the instruction of a page with one channel.
    /// </summary>
    private const string TabIconClass = "veriqa-tab-icon";

    /// <summary>
    /// Class of the icon of the channel in the heading of a launch page of the hop and of the scanner page
    /// once a channel was chosen there.
    /// </summary>
    private const string LaunchIconClass = "veriqa-launch-icon";

    /// <summary>
    /// Returns the CSS modifiers of the button: the canonical brand color of a built-in messenger, the
    /// neutral primary for Email and for a third-party channel — and, for a third-party channel, the class
    /// that names it. A third-party type that could not stand in a class name gets the color class alone:
    /// no adapter may register such a type, so no color can be stated for it either.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <returns>CSS classes of the button.</returns>
    public static string CssClass(string channelType)
    {
        if (BuiltInClass(channelType) is { } builtIn)
        {
            return builtIn;
        }

        return IsAddressable(channelType)
            ? $"{ThirdPartyColorClass} {ThirdPartyClassPrefix}{channelType}"
            : ThirdPartyColorClass;
    }

    /// <summary>
    /// Returns the CSS classes of the icon that stands beside the name of a channel on the sign-in window:
    /// the class of the icon and the modifier naming the channel. A type that could not stand in a class name
    /// gets the class of the icon alone, as its button gets no class naming it.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <returns>CSS classes of the icon.</returns>
    public static string TabIconCssClass(string channelType) => IconCssClass(TabIconClass, channelType);

    /// <summary>
    /// Returns the CSS classes of the icon of the channel in the heading of a launch page of the hop: the
    /// class of the icon and the modifier naming the channel, by the rule of
    /// <see cref="TabIconCssClass(string)"/>.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <returns>CSS classes of the icon.</returns>
    public static string LaunchIconCssClass(string channelType) => IconCssClass(LaunchIconClass, channelType);

    /// <summary>
    /// Builds the rules that paint the buttons and the icons of the channels a level stated a color for. The
    /// button takes the color as its fill; an icon takes it as its color, which its glyph is painted by
    /// (<c>fill: currentColor</c>) — a third-party glyph that states a fill of its own keeps it. Each rule
    /// stands at the specificity of two classes, so it outranks the one-class color of the channel in the
    /// stylesheets of the pages whatever the order of the two. A pair whose channel cannot name a class, or
    /// whose color is not <c>#RRGGBB</c>, is left out — the color is written into the stylesheet as it is.
    /// </summary>
    /// <param name="colors">Colors by channel type; null — none stated.</param>
    /// <returns>The CSS rules, or an empty string.</returns>
    public static string BuildRules(IReadOnlyDictionary<string, string>? colors)
    {
        if (colors is null || colors.Count == 0)
        {
            return string.Empty;
        }

        var css = new StringBuilder();
        foreach (var (channelType, color) in colors)
        {
            // A built-in type passes the pattern as well, so one check settles every channel.
            if (!CorePageHead.IsValidBrandColor(color) || !IsAddressable(channelType))
            {
                continue;
            }

            var channelClass = BuiltInClass(channelType) ?? ThirdPartyClassPrefix + channelType;
            var tabIcon = IconModifier(TabIconClass, channelType);
            var launchIcon = IconModifier(LaunchIconClass, channelType);

            css.Append($"\n.veriqa-btn.{channelClass} {{ background: {color}; border-color: {color}; color: {CorePageHead.BrandedOnPrimary}; }}");
            css.Append($"\n.{TabIconClass}.{tabIcon}, .{LaunchIconClass}.{launchIcon} {{ color: {color}; }}");
        }

        return css.ToString();
    }

    /// <summary>
    /// Returns the color class of a built-in channel — the class only its buttons carry.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <returns>The class; null for a third-party channel.</returns>
    private static string? BuiltInClass(string channelType) => channelType switch
    {
        ChannelTypes.Telegram => "veriqa-btn-telegram",
        ChannelTypes.WhatsApp => "veriqa-btn-whatsapp",
        ChannelTypes.Instagram => "veriqa-btn-instagram",
        ChannelTypes.Messenger => "veriqa-btn-messenger",
        ChannelTypes.WeChat => "veriqa-btn-wechat",
        ChannelTypes.Viber => "veriqa-btn-viber",
        ChannelTypes.Max => "veriqa-btn-max",
        ChannelTypes.Email => "veriqa-btn-email",
        _ => null
    };

    /// <summary>
    /// Returns the classes of an icon of a channel: the class of the icon and, for a type that can stand in
    /// a class name, the modifier naming the channel.
    /// </summary>
    /// <param name="iconClass">Class of the icon.</param>
    /// <param name="channelType">Channel type.</param>
    /// <returns>CSS classes of the icon.</returns>
    private static string IconCssClass(string iconClass, string channelType) =>
        IsAddressable(channelType) ? $"{iconClass} {IconModifier(iconClass, channelType)}" : iconClass;

    /// <summary>
    /// Returns the modifier of an icon class that names a channel: the class followed by two hyphens and the
    /// channel type.
    /// </summary>
    /// <param name="iconClass">Class of the icon.</param>
    /// <param name="channelType">Channel type, one that can stand in a class name.</param>
    /// <returns>The modifier class.</returns>
    private static string IconModifier(string iconClass, string channelType) => $"{iconClass}--{channelType}";

    /// <summary>
    /// Tells whether a channel type can stand in a class name: the pattern every adapter's type is held to.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <returns>true — the type can name a class.</returns>
    private static bool IsAddressable(string? channelType) =>
        channelType is not null && ChannelTypeRegex().IsMatch(channelType);

    /// <summary>
    /// Pattern of a channel type.
    /// </summary>
    /// <returns>The compiled pattern.</returns>
    [GeneratedRegex(CustomChannelConstants.ChannelTypePattern)]
    private static partial Regex ChannelTypeRegex();
}
