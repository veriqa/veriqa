// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Effective branding of one generated page of the "Core" contour, in the shape the render points
/// need it (SPEC-007 UI-054, UI-101, SPEC-012 §4.2–4.3). It is the OUTCOME of a resolution — every
/// value here already went through the canonical order of levels for the context of the page's own
/// transaction — and is handed to the render points as an argument, not published through options:
/// a value fixed at DI registration time could never carry the application level or the
/// <c>ui_config</c> record of a particular request (UI-101).
/// <para>
/// Every value is applied through the same checks as on the sign-in window
/// (<see cref="CorePageHead"/>): the URL passes the scheme allowlist, the color must be
/// <c>#RRGGBB</c>. Unset values mean "no branding" — the neutral canon of the token layer.
/// </para>
/// </summary>
public sealed class CorePageBranding
{
    /// <summary>
    /// Brand name the levels state (<see cref="CorePageBrandingConfigKeys.BrandName"/>) — a Natural Key
    /// the page resolves through the locale files. Null — no level states one.
    /// </summary>
    public string? BrandName { get; init; }

    /// <summary>
    /// URL of the integrator's stylesheet linked after the inline styles, so the integrator
    /// overrides the <c>--veriqa-*</c> tokens by source order. Null or empty — no link.
    /// </summary>
    public string? CustomCssPath { get; init; }

    /// <summary>
    /// SRI hash of <see cref="CustomCssPath"/>. Null — the integrity attribute is not emitted.
    /// </summary>
    public string? CssSriHash { get; init; }

    /// <summary>
    /// Brand primary color (<c>#RRGGBB</c>) substituted into the <c>--veriqa-primary</c> token.
    /// Null — the neutral canon of the theme (the client's color is honoured only in the Branded
    /// preset, and the preset rules are applied by the producer of this value).
    /// </summary>
    public string? PrimaryColor { get; init; }

    /// <summary>
    /// Brand primary color of the DARK theme (<c>#RRGGBB</c>). Null — the dark theme keeps the
    /// neutral canon: <see cref="PrimaryColor"/> is the color of the LIGHT page and is not reused
    /// here, since a color stated for one theme says nothing about the other. A deployment that
    /// states one color for everything gets it in both properties, so its page stays branded in
    /// both themes. The value is a second one rather than a switch because the page mode
    /// <c>auto</c> leaves the choice of the theme to the browser: both colors are emitted, and the
    /// browser picks.
    /// </summary>
    public string? PrimaryColorDark { get; init; }

    /// <summary>
    /// Value of the <c>data-theme</c> attribute of the page — one of the theme names of
    /// <see cref="CorePageThemes"/>, already derived from the declared color scheme and the effective
    /// preset (<see cref="CorePageBrandingRules.EffectiveTheme"/>). The default is
    /// <see cref="CorePageThemes.Auto"/>: a page whose branding was not resolved follows the browser
    /// setting, which is what the shipped color scheme does.
    /// </summary>
    public string Theme { get; init; } = CorePageThemes.Auto;

    /// <summary>
    /// Colors of the buttons of the channels the page leads into, by channel type (key
    /// <c>AuthPageDesign.ChannelButtonColor</c>); null — no level states one, and every button keeps the
    /// color of its channel. Only the channels of the page are resolved, so the map names no other.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? ChannelButtonColors { get; init; }
}
