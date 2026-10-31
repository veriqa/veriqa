// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// Inline SVG channel glyphs for the authentication page. The core contour carries no standalone
/// markup or image files of its own (SPEC-015 §1, §3.1), so each glyph must be embedded into the
/// C#-generated markup rather than referenced from an external file.
/// Path data is duplicated by the site's own icon sprite, which lives in the veriqa/site
/// repository — a change to a glyph here has to be repeated there.
/// telegram/whatsapp/instagram/messenger/wechat/viber — official simple-icons glyphs (CC0); max — a neutral
/// bold "M" lettermark (not the official logo, to avoid confusion with WhatsApp); email — an envelope.
/// Beside them stands one glyph of no channel, the QR code, which marks a tab showing a QR code
/// (<see cref="RenderQrCode"/>); the site's sprite has no copy of it.
/// The Veriqa brand mark is not here: every page of the contour draws it, so it lives in the lower
/// assembly (<see cref="Veriqa.Core.ChannelAdapter.UI.CorePageBrandMark"/>).
/// </summary>
internal static partial class ChannelIconSvg
{
    /// <summary>
    /// Allowed shape of the glyph markup declared by a third-party adapter through
    /// <c>IChannelDisplayMetadata.IconSvgPath</c>: a sequence of plain SVG shape elements
    /// (<c>path</c>, <c>circle</c>, <c>ellipse</c>, <c>rect</c>, <c>line</c>, <c>polyline</c>,
    /// <c>polygon</c>, <c>g</c>) with double-quoted attributes only. Everything else is rejected:
    /// the value is embedded into the sign-in page as markup, and an adapter whose value comes from
    /// configuration could otherwise close the <c>&lt;svg&gt;</c> and inject arbitrary markup —
    /// with <c>style-src 'unsafe-inline'</c> that is enough for a full-page overlay on the IdP
    /// origin. Event-handler (<c>on*</c>) and <c>style</c> attributes are excluded for the same
    /// reason; namespaced attributes (<c>xlink:href</c>) do not match either.
    /// </summary>
    private const string CustomPathPattern =
        """\A(?:\s*<(?:path|circle|ellipse|rect|line|polyline|polygon|g)(?:\s+(?!on|style)[a-z][a-z0-9-]*\s*=\s*"[^"<>]*")*\s*/?>|\s*</g>)+\s*\z""";

    /// <summary>
    /// Path content of the Telegram glyph (viewBox 0 0 24 24).
    /// </summary>
    private const string TelegramPath =
        """<path d="M11.944 0A12 12 0 0 0 0 12a12 12 0 0 0 12 12 12 12 0 0 0 12-12A12 12 0 0 0 12 0a12 12 0 0 0-.056 0zm4.962 7.224c.1-.002.321.023.465.14a.506.506 0 0 1 .171.325c.016.093.036.306.02.472-.18 1.898-.962 6.502-1.36 8.627-.168.9-.499 1.201-.82 1.23-.696.065-1.225-.46-1.9-.902-1.056-.693-1.653-1.124-2.678-1.8-1.185-.78-.417-1.21.258-1.91.177-.184 3.247-2.977 3.307-3.23.007-.032.014-.15-.056-.212s-.174-.041-.249-.024c-.106.024-1.793 1.14-5.061 3.345-.48.33-.913.49-1.302.48-.428-.008-1.252-.241-1.865-.44-.752-.245-1.349-.374-1.297-.789.027-.216.325-.437.893-.663 3.498-1.524 5.83-2.529 6.998-3.014 3.332-1.386 4.025-1.627 4.476-1.635z"/>""";

    /// <summary>
    /// Path content of the WhatsApp glyph (viewBox 0 0 24 24).
    /// </summary>
    private const string WhatsAppPath =
        """<path d="M17.472 14.382c-.297-.149-1.758-.867-2.03-.967-.273-.099-.471-.148-.67.15-.197.297-.767.966-.94 1.164-.173.199-.347.223-.644.075-.297-.15-1.255-.463-2.39-1.475-.883-.788-1.48-1.761-1.653-2.059-.173-.297-.018-.458.13-.606.134-.133.298-.347.446-.52.149-.174.198-.298.298-.497.099-.198.05-.371-.025-.52-.075-.149-.669-1.612-.916-2.207-.242-.579-.487-.5-.669-.51-.173-.008-.371-.01-.57-.01-.198 0-.52.074-.792.372-.272.297-1.04 1.016-1.04 2.479 0 1.462 1.065 2.875 1.213 3.074.149.198 2.096 3.2 5.077 4.487.709.306 1.262.489 1.694.625.712.227 1.36.195 1.871.118.571-.085 1.758-.719 2.006-1.413.248-.694.248-1.289.173-1.413-.074-.124-.272-.198-.57-.347m-5.421 7.403h-.004a9.87 9.87 0 01-5.031-1.378l-.361-.214-3.741.982.998-3.648-.235-.374a9.86 9.86 0 01-1.51-5.26c.001-5.45 4.436-9.884 9.888-9.884 2.64 0 5.122 1.03 6.988 2.898a9.825 9.825 0 012.893 6.994c-.003 5.45-4.437 9.884-9.885 9.884m8.413-18.297A11.815 11.815 0 0012.05 0C5.495 0 .16 5.335.157 11.892c0 2.096.547 4.142 1.588 5.945L.057 24l6.305-1.654a11.882 11.882 0 005.683 1.448h.005c6.554 0 11.89-5.335 11.893-11.893a11.821 11.821 0 00-3.48-8.413Z"/>""";

    /// <summary>
    /// Path content of the Instagram glyph (viewBox 0 0 24 24).
    /// </summary>
    private const string InstagramPath =
        """<path d="M7.0301.084c-1.2768.0602-2.1487.264-2.911.5634-.7888.3075-1.4575.72-2.1228 1.3877-.6652.6677-1.075 1.3368-1.3802 2.127-.2954.7638-.4956 1.6365-.552 2.914-.0564 1.2775-.0689 1.6882-.0626 4.947.0062 3.2586.0206 3.6671.0825 4.9473.061 1.2765.264 2.1482.5635 2.9107.308.7889.72 1.4573 1.388 2.1228.6679.6655 1.3365 1.0743 2.1285 1.38.7632.295 1.6361.4961 2.9134.552 1.2773.056 1.6884.069 4.9462.0627 3.2578-.0062 3.668-.0207 4.9478-.0814 1.28-.0607 2.147-.2652 2.9098-.5633.7889-.3086 1.4578-.72 2.1228-1.3881.665-.6682 1.0745-1.3378 1.3795-2.1284.2957-.7632.4966-1.636.552-2.9124.056-1.2809.0692-1.6898.063-4.948-.0063-3.2583-.021-3.6668-.0817-4.9465-.0607-1.2797-.264-2.1487-.5633-2.9117-.3084-.7889-.72-1.4568-1.3876-2.1228C21.2982 1.33 20.628.9208 19.8378.6165 19.074.321 18.2017.1197 16.9244.0645 15.6471.0093 15.236-.005 11.977.0014 8.718.0076 8.31.0215 7.0301.0839m.1402 21.6932c-1.17-.0509-1.8053-.2453-2.2287-.408-.5606-.216-.96-.4771-1.3819-.895-.422-.4178-.6811-.8186-.9-1.378-.1644-.4234-.3624-1.058-.4171-2.228-.0595-1.2645-.072-1.6442-.079-4.848-.007-3.2037.0053-3.583.0607-4.848.05-1.169.2456-1.805.408-2.2282.216-.5613.4762-.96.895-1.3816.4188-.4217.8184-.6814 1.3783-.9003.423-.1651 1.0575-.3614 2.227-.4171 1.2655-.06 1.6447-.072 4.848-.079 3.2033-.007 3.5835.005 4.8495.0608 1.169.0508 1.8053.2445 2.228.408.5608.216.96.4754 1.3816.895.4217.4194.6816.8176.9005 1.3787.1653.4217.3617 1.056.4169 2.2263.0602 1.2655.0739 1.645.0796 4.848.0058 3.203-.0055 3.5834-.061 4.848-.051 1.17-.245 1.8055-.408 2.2294-.216.5604-.4763.96-.8954 1.3814-.419.4215-.8181.6811-1.3783.9-.4224.1649-1.0577.3617-2.2262.4174-1.2656.0595-1.6448.072-4.8493.079-3.2045.007-3.5825-.006-4.848-.0608M16.953 5.5864A1.44 1.44 0 1 0 18.39 4.144a1.44 1.44 0 0 0-1.437 1.4424M5.8385 12.012c.0067 3.4032 2.7706 6.1557 6.173 6.1493 3.4026-.0065 6.157-2.7701 6.1506-6.1733-.0065-3.4032-2.771-6.1565-6.174-6.1498-3.403.0067-6.156 2.771-6.1496 6.1738M8 12.0077a4 4 0 1 1 4.008 3.9921A3.9996 3.9996 0 0 1 8 12.0077"/>""";

    /// <summary>
    /// Path content of the Messenger glyph (viewBox 0 0 24 24).
    /// </summary>
    private const string MessengerPath =
        """<path d="M12 0C5.24 0 0 4.952 0 11.64c0 3.499 1.434 6.521 3.769 8.61a.96.96 0 0 1 .323.683l.065 2.135a.96.96 0 0 0 1.347.85l2.381-1.053a.96.96 0 0 1 .641-.046A13 13 0 0 0 12 23.28c6.76 0 12-4.952 12-11.64S18.76 0 12 0m6.806 7.44c.522-.03.971.567.63 1.094l-4.178 6.457a.707.707 0 0 1-.977.208l-3.87-2.504a.44.44 0 0 0-.49.007l-4.363 3.01c-.637.438-1.415-.317-.995-.966l4.179-6.457a.706.706 0 0 1 .977-.21l3.87 2.505c.15.097.344.094.491-.007l4.362-3.008a.7.7 0 0 1 .364-.13"/>""";

    /// <summary>
    /// Path content of the WeChat glyph (viewBox 0 0 24 24).
    /// </summary>
    private const string WeChatPath =
        """<path d="M8.691 2.188C3.891 2.188 0 5.476 0 9.53c0 2.212 1.17 4.203 3.002 5.55a.59.59 0 0 1 .213.665l-.39 1.48c-.019.07-.048.141-.048.213 0 .163.13.295.29.295a.326.326 0 0 0 .167-.054l1.903-1.114a.864.864 0 0 1 .717-.098 10.16 10.16 0 0 0 2.837.403c.276 0 .543-.027.811-.05-.857-2.578.157-4.972 1.932-6.446 1.703-1.415 3.882-1.98 5.853-1.838-.576-3.583-4.196-6.348-8.596-6.348zM5.785 5.991c.642 0 1.162.529 1.162 1.18a1.17 1.17 0 0 1-1.162 1.178A1.17 1.17 0 0 1 4.623 7.17c0-.651.52-1.18 1.162-1.18zm5.813 0c.642 0 1.162.529 1.162 1.18a1.17 1.17 0 0 1-1.162 1.178 1.17 1.17 0 0 1-1.162-1.178c0-.651.52-1.18 1.162-1.18zm5.34 2.867c-1.797-.052-3.746.512-5.28 1.786-1.72 1.428-2.687 3.72-1.78 6.22.942 2.453 3.666 4.229 6.884 4.229.826 0 1.622-.12 2.361-.336a.722.722 0 0 1 .598.082l1.584.926a.272.272 0 0 0 .14.047c.134 0 .24-.111.24-.247 0-.06-.023-.12-.038-.177l-.327-1.233a.582.582 0 0 1-.023-.156.49.49 0 0 1 .201-.398C23.024 18.48 24 16.82 24 14.98c0-3.21-2.931-5.837-6.656-6.088V8.89c-.135-.01-.27-.027-.407-.03zm-2.53 3.274c.535 0 .969.44.969.982a.976.976 0 0 1-.969.983.976.976 0 0 1-.969-.983c0-.542.434-.982.97-.982zm4.844 0c.535 0 .969.44.969.982a.976.976 0 0 1-.969.983.976.976 0 0 1-.969-.983c0-.542.434-.982.969-.982z"/>""";

    /// <summary>
    /// Path content of the Viber glyph (viewBox 0 0 24 24).
    /// </summary>
    private const string ViberPath =
        """<path d="M11.4 0C9.473.028 5.333.344 3.02 2.467 1.302 4.187.696 6.7.633 9.817.57 12.933.488 18.776 6.12 20.36h.003l-.004 2.416s-.037.977.61 1.177c.777.242 1.234-.5 1.98-1.302.407-.44.972-1.084 1.397-1.58 3.85.326 6.812-.416 7.15-.525.776-.252 5.176-.816 5.892-6.657.74-6.02-.36-9.83-2.34-11.546-.596-.55-3.006-2.3-8.375-2.323 0 0-.395-.025-1.037-.017zm.058 1.693c.545-.004.88.017.88.017 4.542.02 6.717 1.388 7.222 1.846 1.675 1.435 2.53 4.868 1.906 9.897v.002c-.604 4.878-4.174 5.184-4.832 5.395-.28.09-2.882.737-6.153.524 0 0-2.436 2.94-3.197 3.704-.12.12-.26.167-.352.144-.13-.033-.166-.188-.165-.414l.02-4.018c-4.762-1.32-4.485-6.292-4.43-8.895.054-2.604.543-4.738 1.996-6.173 1.96-1.773 5.474-2.018 7.11-2.03zm.38 2.602c-.167 0-.303.135-.304.302 0 .167.133.303.3.305 1.624.01 2.946.537 4.028 1.592 1.073 1.046 1.62 2.468 1.633 4.334.002.167.14.3.307.3.166-.002.3-.138.3-.304-.014-1.984-.618-3.596-1.816-4.764-1.19-1.16-2.692-1.753-4.447-1.765zm-3.96.695c-.19-.032-.4.005-.616.117l-.01.002c-.43.247-.816.562-1.146.932-.002.004-.006.004-.008.008-.267.323-.42.638-.46.948-.008.046-.01.093-.007.14 0 .136.022.27.065.4l.013.01c.135.48.473 1.276 1.205 2.604.42.768.903 1.5 1.446 2.186.27.344.56.673.87.984l.132.132c.31.308.64.6.984.87.686.543 1.418 1.027 2.186 1.447 1.328.733 2.126 1.07 2.604 1.206l.01.014c.13.042.265.064.402.063.046.002.092 0 .138-.008.31-.036.627-.19.948-.46.004 0 .003-.002.008-.005.37-.33.683-.72.93-1.148l.003-.01c.225-.432.15-.842-.18-1.12-.004 0-.698-.58-1.037-.83-.36-.255-.73-.492-1.113-.71-.51-.285-1.032-.106-1.248.174l-.447.564c-.23.283-.657.246-.657.246-3.12-.796-3.955-3.955-3.955-3.955s-.037-.426.248-.656l.563-.448c.277-.215.456-.737.17-1.248-.217-.383-.454-.756-.71-1.115-.25-.34-.826-1.033-.83-1.035-.137-.165-.31-.265-.502-.297zm4.49.88c-.158.002-.29.124-.3.282-.01.167.115.312.282.324 1.16.085 2.017.466 2.645 1.15.63.688.93 1.524.906 2.57-.002.168.13.306.3.31.166.003.305-.13.31-.297.025-1.175-.334-2.193-1.067-2.994-.74-.81-1.777-1.253-3.05-1.346h-.024zm.463 1.63c-.16.002-.29.127-.3.287-.008.167.12.31.288.32.523.028.875.175 1.113.422.24.245.388.62.416 1.164.01.167.15.295.318.287.167-.008.295-.15.287-.317-.03-.644-.215-1.178-.58-1.557-.367-.378-.893-.574-1.52-.607h-.018z"/>""";

    /// <summary>
    /// Path content of the MAX glyph (viewBox 0 0 24 24). A neutral bold "M" lettermark
    /// (not the official logo; the chat bubble was removed to avoid confusion with WhatsApp).
    /// </summary>
    private const string MaxPath =
        """<path d="M2 21V3h5l5 8 5-8h5v18h-5V11l-5 8-5-8v10z"/>""";

    /// <summary>
    /// Path content of the Email glyph — an envelope (viewBox 0 0 24 24).
    /// </summary>
    private const string EmailPath =
        """<path d="M20 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 4-8 5-8-5V6l8 5 8-5v2z"/>""";

    /// <summary>
    /// Path content of the QR code glyph (viewBox 0 0 24 24): three finder squares and a few modules,
    /// drawn for this page. It belongs to no channel — it marks a tab whose panel shows a QR code — so
    /// it is rendered by <see cref="RenderQrCode"/> and never chosen by a channel type.
    /// </summary>
    private const string QrCodePath =
        """<path d="M3 3h8v8H3zm2 2v4h4V5zm1 1h2v2H6zM13 3h8v8h-8zm2 2v4h4V5zm1 1h2v2h-2zM3 13h8v8H3zm2 2v4h4v-4zm1 1h2v2H6zM13 13h2v2h-2zm4 0h4v2h-4zm-2 2h2v2h-2zm-2 2h2v4h-2zm4 0h2v2h-2zm2 2h2v2h-2zm-4 0h2v2h-2z"/>""";

    /// <summary>
    /// Returns the path content of the channel glyph, or null for an unknown channel
    /// (the caller must work correctly without an icon).
    /// </summary>
    /// <param name="channelType">Channel type (<see cref="ChannelTypes"/>).</param>
    /// <returns>Glyph path markup, or null.</returns>
    public static string? GetPath(string channelType)
    {
        return channelType switch
        {
            ChannelTypes.Telegram => TelegramPath,
            ChannelTypes.WhatsApp => WhatsAppPath,
            ChannelTypes.Instagram => InstagramPath,
            ChannelTypes.Messenger => MessengerPath,
            ChannelTypes.WeChat => WeChatPath,
            ChannelTypes.Viber => ViberPath,
            ChannelTypes.Max => MaxPath,
            ChannelTypes.Email => EmailPath,
            _ => null
        };
    }

    /// <summary>
    /// Renders the inline SVG channel icon with the given CSS class. For an unknown
    /// channel returns an empty string (the page stays valid without an icon).
    /// </summary>
    /// <param name="channelType">Channel type (<see cref="ChannelTypes"/>).</param>
    /// <param name="cssClass">CSS class of the SVG element (e.g. veriqa-ch-icon).</param>
    /// <param name="customPath">
    /// Glyph markup declared by a third-party adapter through <c>IChannelDisplayMetadata</c>
    /// (public SPI) — used only for channels that have no bundled glyph. Null — no icon.
    /// </param>
    /// <returns>SVG markup, or an empty string.</returns>
    public static string Render(string channelType, string cssClass, string? customPath = null)
    {
        // A bundled glyph wins; a third-party one is embedded only if it passes the allowlist
        // (the check is repeated here so the sink itself can never emit unvalidated markup).
        var path = GetPath(channelType) ?? (IsValidCustomPath(customPath) ? customPath : null);
        return path is null
            ? string.Empty
            : $"""<svg class="{cssClass}" viewBox="0 0 24 24" aria-hidden="true" focusable="false">{path}</svg>""";
    }

    /// <summary>
    /// Renders the inline SVG QR code glyph with the given CSS class — decorative, painted by the text
    /// color of its element (<c>currentColor</c>) as the glyphs of the buttons are.
    /// </summary>
    /// <param name="cssClass">CSS class of the SVG element.</param>
    /// <returns>SVG markup.</returns>
    public static string RenderQrCode(string cssClass)
    {
        return $"""<svg class="{cssClass}" viewBox="0 0 24 24" aria-hidden="true" focusable="false">{QrCodePath}</svg>""";
    }

    /// <summary>
    /// Checks the glyph markup declared by a third-party adapter against
    /// <see cref="CustomPathPattern"/>. Invalid markup is dropped by the caller (the channel is
    /// rendered without an icon — the page stays valid, per the SPI degradation contract).
    /// </summary>
    /// <param name="customPath">Glyph markup declared by the adapter, or null.</param>
    /// <returns><c>true</c> if the markup may be embedded into the page.</returns>
    public static bool IsValidCustomPath([NotNullWhen(true)] string? customPath)
    {
        return !string.IsNullOrWhiteSpace(customPath) && CustomPathRegex().IsMatch(customPath);
    }

    /// <summary>
    /// Compiled matcher of the third-party glyph markup contract.
    /// </summary>
    [GeneratedRegex(CustomPathPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CustomPathRegex();
}
