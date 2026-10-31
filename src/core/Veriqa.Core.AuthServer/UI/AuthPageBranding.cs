// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// Turns the design of the SIGN-IN WINDOW — already resolved as a whole by
/// <see cref="Configuration.Resolution.AuthPageSettingsResolver"/> — into the branding shape the
/// render points of the contour consume (<see cref="CorePageBranding"/>).
/// <para>
/// The rules themselves are not here: they live in <see cref="CorePageBrandingRules"/>, one home for
/// the window and for the service pages, so the two cannot answer "is this page branded" differently.
/// This type only spares the window a second resolution of the three branding keys — it already holds
/// their values inside its resolved design object.
/// </para>
/// </summary>
internal static class AuthPageBranding
{
    /// <summary>
    /// Effective preset of the window: a custom stylesheet forces the base Default (SPEC-012 CFG-023).
    /// </summary>
    /// <param name="design">Effective design settings.</param>
    /// <returns>Preset actually applied to the page.</returns>
    public static DesignPreset ResolvePreset(AuthPageDesignOptions design)
    {
        return CorePageBrandingRules.EffectivePreset(design.Preset, design.CustomCssPath);
    }

    /// <summary>
    /// Converts the design settings into the branding of the window. The preset and the theme are
    /// passed in rather than resolved here: the window already resolved them for its own markup, and
    /// resolving them twice is how the markup and the branding would start to disagree.
    /// </summary>
    /// <param name="design">Effective design settings.</param>
    /// <param name="preset">Preset resolved for the same <paramref name="design"/>.</param>
    /// <param name="theme">Value of the <c>data-theme</c> attribute resolved for the same
    /// <paramref name="design"/> — the one that goes into the window's own markup.</param>
    /// <param name="channelButtonColors">Colors of the buttons of the window's channels; null — none.</param>
    /// <returns>Branding for the render points.</returns>
    public static CorePageBranding ToCoreBranding(
        AuthPageDesignOptions design,
        DesignPreset preset,
        string theme,
        IReadOnlyDictionary<string, string>? channelButtonColors)
    {
        // The brand color is cut by theme, and the preset rule applies to both values alike: a page
        // that is not Branded shows the neutral canon in either theme.
        return new CorePageBranding
        {
            CustomCssPath = design.CustomCssPath,
            CssSriHash = design.CssSriHash,
            PrimaryColor = CorePageBrandingRules.BrandPrimary(preset, design.GetPrimaryColor(CorePageThemes.Light)),
            PrimaryColorDark = CorePageBrandingRules.BrandPrimary(preset, design.GetPrimaryColor(CorePageThemes.Dark)),
            Theme = theme,
            ChannelButtonColors = channelButtonColors
        };
    }
}
