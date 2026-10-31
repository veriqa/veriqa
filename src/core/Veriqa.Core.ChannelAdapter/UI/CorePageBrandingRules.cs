// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The rules that turn resolved design values into the branding of a generated page. They live in
/// ONE place on purpose: the sign-in window and the service pages must not be able to answer "is this
/// page branded" differently, and both reach these rules — the window through the auth server's own
/// design object, the service pages through <see cref="CorePageBrandingResolver"/>.
/// </summary>
public static class CorePageBrandingRules
{
    /// <summary>
    /// Effective preset: a custom stylesheet forces the base Default (SPEC-012 CFG-023) — the styling
    /// is then the integrator's job, not the preset's.
    /// </summary>
    /// <param name="declaredPreset">Preset the levels resolved to.</param>
    /// <param name="customCssPath">Resolved path of the integrator's stylesheet; null or empty — none.</param>
    /// <returns>Preset actually applied to the page.</returns>
    public static DesignPreset EffectivePreset(DesignPreset declaredPreset, string? customCssPath) =>
        string.IsNullOrEmpty(customCssPath) ? declaredPreset : DesignPreset.Default;

    /// <summary>
    /// Brand primary color honoured by the page: only in the Branded preset and only as a valid
    /// <c>#RRGGBB</c> (protection against CSS injection). Anything else — null, meaning the neutral
    /// canon of the token layer (black/white, Veriqa red is an accent only).
    /// </summary>
    /// <param name="effectivePreset">Preset already resolved by <see cref="EffectivePreset"/>.</param>
    /// <param name="color">Resolved brand color of the theme asked about.</param>
    /// <returns>Brand color, or null when the neutral canon applies.</returns>
    public static string? BrandPrimary(DesignPreset effectivePreset, string? color) =>
        effectivePreset is DesignPreset.Branded && CorePageHead.IsValidBrandColor(color)
            ? color
            : null;

    /// <summary>
    /// Value of the <c>data-theme</c> attribute of a page (SPEC-015 §2.2): an explicit Light or Dark
    /// wins; Auto follows the browser setting (<c>prefers-color-scheme</c>), except that the Dark
    /// preset (SPEC-012 §4.2) yields a dark page as well.
    /// </summary>
    /// <param name="declared">Color scheme the levels resolved to.</param>
    /// <param name="effectivePreset">Preset already resolved by <see cref="EffectivePreset"/>.</param>
    /// <returns>One of the theme names of <see cref="CorePageThemes"/>.</returns>
    public static string EffectiveTheme(AuthPageTheme declared, DesignPreset effectivePreset) =>
        declared switch
        {
            AuthPageTheme.Light => CorePageThemes.Light,
            AuthPageTheme.Dark => CorePageThemes.Dark,
            _ => effectivePreset is DesignPreset.Dark ? CorePageThemes.Dark : CorePageThemes.Auto
        };
}
