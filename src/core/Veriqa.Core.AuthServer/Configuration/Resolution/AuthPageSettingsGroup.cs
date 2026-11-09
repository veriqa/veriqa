// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// Effective settings of one sign-in page request: every value already resolved by the canonical
/// resolver, so that neither the endpoint nor the renderer merges levels of its own (SPEC-012
/// CFG-231, CFG-235).
/// </summary>
/// <param name="Design">Effective design of the page.</param>
/// <param name="Title">Page title stated by an owning level; null — the renderer's localized default.</param>
/// <param name="Instruction">Page instruction stated by an owning level; null — the renderer's localized default.</param>
/// <param name="InstructionWithoutQr">Wording of the stated instruction for a page that hides the QR code;
/// null — <paramref name="Instruction"/>.</param>
/// <param name="InstructionWithoutButton">Wording of the stated instruction for a page that shows the QR
/// code and hides the channel buttons; null — <paramref name="Instruction"/>.</param>
/// <param name="Channels">Channel codes stated by an owning level; null — the global display set.</param>
/// <param name="ChannelDisplayMode">Effective channel display mode: the mode of the <c>ui_config</c>
/// record where it states one, otherwise the global mode of the core level. It stays nullable because
/// the key is asked for optionally — a deployment whose catalogs are not registered states none.</param>
/// <param name="TtlSeconds">Transaction lifetime stated by an owning level; null — the configured lifetime.</param>
/// <param name="DefaultTimeZone">Time zone stated by an owning level for the moments of this
/// transaction's messages; null — no level states one and the moments stay in UTC.</param>
/// <param name="QrVisibility">Effective QR visibility of the page.</param>
/// <param name="CancelEnabled">Whether the cancel button is offered — on the sign-in window and on the
/// entry page of a confirmation. The scanner page of either type renders no such button whatever the
/// value.</param>
/// <param name="StatedChannelButtonVisibility">Visibility of the channel button AS THE LEVELS STATE IT.
/// It is what the consistency rule of the group reads; a page is rendered with
/// <see cref="AuthPageSettings.EffectiveChannelButtonVisibility"/> instead.</param>
/// <param name="ChannelLayout">Layout of the channels where the page shows the QR code of each of
/// them: behind tabs or all at once. Whether a given page has anything to arrange — several channels,
/// QR codes of their own — is the renderer's to tell, not the aggregate's.</param>
/// <param name="RememberChannelHint">Whether the page hints at the channel the user went through last
/// time. Which page has a choice to put that channel forward in is the renderer's to tell.</param>
/// <param name="RememberChannelCheckbox">Whether a page with the unified QR code offers the checkbox that
/// remembers the channel. Whether a given page shows the unified QR code is the renderer's to tell.</param>
internal sealed record AuthPageSettings(
    AuthPageDesignOptions Design,
    string? Title,
    string? Instruction,
    string? InstructionWithoutQr,
    string? InstructionWithoutButton,
    IReadOnlyList<string>? Channels,
    ChannelDisplayMode? ChannelDisplayMode,
    int? TtlSeconds,
    string? DefaultTimeZone,
    QrCodeVisibility QrVisibility,
    bool CancelEnabled,
    ChannelButtonVisibility StatedChannelButtonVisibility,
    ChannelLayout ChannelLayout,
    bool RememberChannelHint,
    bool RememberChannelCheckbox)
{
    /// <summary>
    /// Whether the stated pair of visibilities leaves a class of devices with NO way into a channel —
    /// neither the QR code nor the channel button. The QR code is absent on a phone under
    /// <see cref="QrCodeVisibility.DesktopOnly"/> and everywhere under <see cref="QrCodeVisibility.Never"/>;
    /// the button is absent off a phone under <see cref="ChannelButtonVisibility.MobileOnly"/> and
    /// everywhere under <see cref="ChannelButtonVisibility.Never"/>. Which channels the page carries is
    /// not looked at: the relation is one between the two settings.
    /// <para>
    /// This is the ONE statement of the combinations: the consistency rule of the group reports by it,
    /// and <see cref="EffectiveChannelButtonVisibility"/> is derived from it.
    /// </para>
    /// </summary>
    public bool LeavesDeviceWithoutEntry => QrVisibility switch
    {
        QrCodeVisibility.DesktopOnly => StatedChannelButtonVisibility is ChannelButtonVisibility.Never,
        QrCodeVisibility.Never => StatedChannelButtonVisibility is not ChannelButtonVisibility.Always,
        _ => false
    };

    /// <summary>
    /// Visibility of the channel button the page is rendered with: the stated one, except where the
    /// stated pair leaves a device with no way in (<see cref="LeavesDeviceWithoutEntry"/>) — there the
    /// button is shown on every device. It is computed here, over the stated value the aggregate keeps,
    /// so that the rule of the group still sees the conflict it reports, and both builders of the page
    /// context read one answer.
    /// </summary>
    public ChannelButtonVisibility EffectiveChannelButtonVisibility => LeavesDeviceWithoutEntry
        ? ChannelButtonVisibility.Always
        : StatedChannelButtonVisibility;
}

/// <summary>
/// The sign-in page as a DOMAIN GROUP of the configuration mechanism (SPEC-012 §10.6, CFG-231): the
/// keys the page is made of, the aggregate they fill and the rule of consistency between them, all
/// declared in one place and filled by a single call of the resolver.
/// <para>
/// The filling is a plain delegate over the group's reader: each value is asked for by its own key
/// and lands in its own field, and the compiler checks both. There is no reflection here and
/// nothing generated.
/// </para>
/// <para>
/// Every key below is resolved inside ONE scope: they are served by the same records — the global
/// section, the tenant record, the client entry, the <c>ui_config</c> record — and a scope per key
/// would read each of those once per key rather than once per page.
/// </para>
/// </summary>
internal static class AuthPageSettingsGroup
{
    /// <summary>
    /// Name of the group, as it appears in the message of a failed consistency rule.
    /// </summary>
    private const string GroupName = "AuthPage";

    /// <summary>
    /// The sign-in page group: what the page's settings are made of.
    /// </summary>
    public static ConfigKeyGroup<AuthPageSettings> Group { get; } =
        ConfigKeyGroup<AuthPageSettings>.Of(GroupName, AssembleAsync, CheckConsistency);

    /// <summary>
    /// Fills the settings of the page from the resolved values of its keys.
    /// </summary>
    /// <param name="resolution">Reader of the group's keys inside the shared scope.</param>
    /// <returns>Effective settings of the page.</returns>
    private static async ValueTask<AuthPageSettings> AssembleAsync(ConfigGroupResolution resolution)
    {
        var qrVisibility = await resolution.ValueOfAsync(AuthServerConfigKeys.QrCodeShowQrCode);

        // Each custom resource is resolved as ONE value carrying its path and its SRI hash, so the two
        // halves cannot come from different levels (SPEC-012 §4.3): a hash of a file some other level
        // named would make the browser drop the resource without a word.
        var customCss = await resolution.ValueOfAsync(CorePageBrandingConfigKeys.CustomCss);
        var customJs = await resolution.ValueOfAsync(AuthServerConfigKeys.PageCustomJs);

        var design = new AuthPageDesignOptions
        {
            Preset = await resolution.ValueOfAsync(CorePageBrandingConfigKeys.Preset),
            Theme = await resolution.ValueOfAsync(CorePageBrandingConfigKeys.Theme),

            // The logo and the primary color are cut by theme, and the page mode Auto leaves the choice
            // of the theme to the browser — so both themes are resolved rather than one: at Auto the
            // server does not know which theme the page will end up in, and both values have to reach
            // it. A theme no level stated anything for is simply absent from the map, and the consumer
            // reads the pair through AuthPageDesignOptions.GetLogoUrl / GetPrimaryColor.
            LogoUrlByTheme = await resolution.TextByDimensionAsync(
                AuthServerConfigKeys.PageLogoUrl,
                CorePageBrandingConfigKeys.ThemeDimensionName,
                CorePageThemes.Painted),
            PrimaryColorByTheme = await resolution.TextByDimensionAsync(
                CorePageBrandingConfigKeys.PrimaryColor,
                CorePageBrandingConfigKeys.ThemeDimensionName,
                CorePageThemes.Painted),

            // The brand name stands beside the logo but is asked for on its own: it is a text, cut by no
            // theme, and the page reads the two values apart (a level may state one of them alone).
            BrandName = await resolution.ValueOfAsync(CorePageBrandingConfigKeys.BrandName),

            CustomCssPath = customCss?.Path,
            CustomJsPath = customJs?.Path,
            CssSriHash = customCss?.SriHash,
            JsSriHash = customJs?.SriHash,
            SignalRClientPath = await resolution.ValueOfAsync(AuthServerConfigKeys.PageSignalRClientPath),
            FooterHtml = await resolution.ValueOfAsync(AuthServerConfigKeys.PageFooterHtml),
            HopInstructionHtml = await resolution.ValueOfAsync(AuthServerConfigKeys.PageHopInstructionHtml)

            // The QR group of the effective design carries NEITHER of the two settings of the section
            // any more. The pixel scale is not a page-wide value: the channel is a dimension of its
            // key, so "the scale" exists only for a named channel and is asked for one at a time
            // (AuthPageSettingsResolver.ResolvePixelsPerModuleAsync). The visibility is carried by the
            // aggregate itself (QrVisibility below), which is where its one consumer — the renderer —
            // reads it; a copy of it inside the design would be a second answer nothing asks.
        };

        return new AuthPageSettings(
            design,
            await resolution.ValueOfAsync(AuthServerConfigKeys.PageTitle),
            await resolution.ValueOfAsync(AuthServerConfigKeys.PageInstruction),
            await resolution.ValueOfAsync(AuthServerConfigKeys.PageInstructionWithoutQr),
            await resolution.ValueOfAsync(AuthServerConfigKeys.PageInstructionWithoutButton),
            await resolution.ValueOfAsync(AuthServerConfigKeys.PageChannels),
            await resolution.OptionalValueOfAsync(AuthServerConfigKeys.PageChannelDisplayMode),
            await resolution.OptionalValueOfAsync(AuthServerConfigKeys.TransactionTtlSeconds),
            await resolution.ValueOfAsync(AuthServerConfigKeys.DefaultTimeZone),
            qrVisibility,
            await resolution.ValueOfAsync(AuthServerConfigKeys.CancelEnabled),

            // The visibility of the channel button lands here AS STATED, conflict with the QR
            // visibility included: replacing it at this point would hide the conflict from the rule
            // that reports it.
            await resolution.ValueOfAsync(AuthServerConfigKeys.ChannelButtonShowChannelButton),
            await resolution.ValueOfAsync(AuthServerConfigKeys.PageChannelLayout),
            await resolution.ValueOfAsync(AuthServerConfigKeys.RememberChannelHint),
            await resolution.ValueOfAsync(AuthServerConfigKeys.RememberChannelCheckbox));
    }

    /// <summary>
    /// Consistency rule of the group — every check of a relation between the page's keys, reported as
    /// one reason. A group has a single rule, and the checks are independent of each other, so a
    /// combination that fails several of them names all of them rather than the first.
    /// </summary>
    /// <param name="settings">Filled settings of the page.</param>
    /// <returns>Reasons the combination is inconsistent, or null.</returns>
    private static string? CheckConsistency(AuthPageSettings settings)
    {
        string?[] reasons = [CheckThemedBranding(settings), CheckChannelEntry(settings)];

        var violated = reasons.Where(static reason => reason is not null).ToList();

        return violated.Count == 0 ? null : string.Join(" ", violated);
    }

    /// <summary>
    /// Check of the two visibilities of the page: every class of devices must be left with a way into
    /// a channel — the QR code or the channel button
    /// (<see cref="AuthPageSettings.LeavesDeviceWithoutEntry"/>).
    /// <para>
    /// Each of the two keys is valid on its own, and each may come from a level of its own — a client
    /// entry that hides the buttons over a global section that hides the QR code. The page is not
    /// refused over it: it is rendered with the button on every device
    /// (<see cref="AuthPageSettings.EffectiveChannelButtonVisibility"/>), and the reason names both
    /// stated values, since either of them may be the one to correct.
    /// </para>
    /// </summary>
    /// <param name="settings">Filled settings of the page.</param>
    /// <returns>Reason the combination is inconsistent, or null.</returns>
    private static string? CheckChannelEntry(AuthPageSettings settings)
    {
        if (!settings.LeavesDeviceWithoutEntry)
        {
            return null;
        }

        return $"'{AuthServerConfigKeys.QrCodeShowQrCode.Name}' is '{settings.QrVisibility}' while "
            + $"'{AuthServerConfigKeys.ChannelButtonShowChannelButton.Name}' is "
            + $"'{settings.StatedChannelButtonVisibility}': a device is left with neither the QR code nor the "
            + $"channel button, so the page shows the button as '{settings.EffectiveChannelButtonVisibility}'.";
    }

    /// <summary>
    /// Check of the themed branding: a page pinned to ONE theme must not be left without the branding
    /// values stated for the neighbouring theme alone.
    /// <para>
    /// This is a relation BETWEEN keys and therefore cannot live on any of them: the theme is one key
    /// (<see cref="CorePageBrandingConfigKeys.Theme"/>) and the themed branding is two others, each of
    /// them perfectly valid on its own. The combination is what renders a page with no logo and no
    /// accent color while the deployment did configure both — silently, because a chain that ends
    /// without a value is the ordinary "no override" answer.
    /// </para>
    /// </summary>
    /// <param name="settings">Filled settings of the page.</param>
    /// <returns>Reason the combination is inconsistent, or null.</returns>
    private static string? CheckThemedBranding(AuthPageSettings settings)
    {
        // The page mode Auto leaves the theme to the browser, so both themes are painted and neither
        // of them can be the "wrong" one.
        var painted = settings.Design.Theme switch
        {
            AuthPageTheme.Light => CorePageThemes.Light,
            AuthPageTheme.Dark => CorePageThemes.Dark,
            _ => null
        };

        if (painted is null)
        {
            return null;
        }

        var neighbour = painted == CorePageThemes.Light ? CorePageThemes.Dark : CorePageThemes.Light;

        var stranded = new List<string>(capacity: 2);

        if (IsStatedForNeighbourOnly(settings.Design.LogoUrlByTheme, painted, neighbour))
        {
            stranded.Add(nameof(AuthPageDesignOptions.LogoUrl));
        }

        if (IsStatedForNeighbourOnly(settings.Design.PrimaryColorByTheme, painted, neighbour))
        {
            stranded.Add(nameof(AuthPageDesignOptions.PrimaryColor));
        }

        if (stranded.Count == 0)
        {
            return null;
        }

        var stated = stranded.Count == 1 ? "is stated" : "are stated";

        return $"The page is pinned to the '{painted}' theme, while {string.Join(" and ", stranded)} {stated} "
            + $"for the '{neighbour}' theme only: the page renders without it.";
    }

    /// <summary>
    /// Tells whether a themed value resolved for the neighbouring theme alone — that is, the
    /// deployment stated it, but not for the theme this page is painted in.
    /// </summary>
    /// <param name="valuesByTheme">Values resolved per theme; null — none was resolved at all.</param>
    /// <param name="painted">Theme the page is painted in.</param>
    /// <param name="neighbour">The other theme.</param>
    /// <returns><c>true</c> when the value exists for the neighbouring theme and not for the painted one.</returns>
    private static bool IsStatedForNeighbourOnly(
        IDictionary<string, string>? valuesByTheme,
        string painted,
        string neighbour) =>
        valuesByTheme is not null
        && valuesByTheme.ContainsKey(neighbour)
        && !valuesByTheme.ContainsKey(painted);
}
