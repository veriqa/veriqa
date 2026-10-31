// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Builds the styling part of the head for the pages of the "Core" contour: the inline
/// <c>&lt;style&gt;</c> assembled from the embedded stylesheets plus the generated branded token
/// overrides, and the <c>&lt;link&gt;</c> to the integrator's stylesheet.
/// <para>
/// Order matters and is the contract of the two-step override: the embedded rules come first, the
/// generated token block after them (it overrides the neutral canon by source order), and the
/// integrator's stylesheet last — so a client redefines <c>--veriqa-*</c> without <c>!important</c>.
/// </para>
/// </summary>
public static class CorePageHead
{
    /// <summary>
    /// Content color on the client's branded primary. The brand fill is an arbitrary client color,
    /// so the value is not themed and stays a constant of the branded palette (SPEC-015 §2.1).
    /// </summary>
    internal const string BrandedOnPrimary = "#ffffff";

    /// <summary>
    /// Name of the attribute of <c>&lt;html&gt;</c> the theme selectors of the token layer read.
    /// </summary>
    private const string ThemeAttributeName = "data-theme";

    /// <summary>
    /// HEX color validation pattern (#RRGGBB) to protect against CSS injection.
    /// </summary>
    private static readonly Regex HexColorPattern = new(
        @"^#[0-9A-Fa-f]{6}$",
        RegexOptions.Compiled);

    /// <summary>
    /// Builds the whole styling head of a page of the contour: token layer, the rules of the page
    /// family, the generated overrides and the integrator's stylesheet link.
    /// </summary>
    /// <param name="branding">Effective page branding (null — no branding at all).</param>
    /// <param name="rulesCss">Stylesheet of the page family (sign-in window or service pages).</param>
    /// <param name="extraCss">Generated or page-specific rules appended after the shared ones.</param>
    /// <returns>Ready-to-embed <c>&lt;style&gt;</c> element followed by an optional <c>&lt;link&gt;</c>.</returns>
    public static string BuildPageHead(CorePageBranding? branding, string rulesCss, string? extraCss = null)
    {
        // The method assembles the head in one place, so the pages of the contour cannot drift
        // apart in what they include and in which order
        var css = string.Concat(
            CorePageStyles.Base,
            rulesCss,
            extraCss ?? string.Empty,
            BuildBrandedTokens(branding?.PrimaryColor, branding?.PrimaryColorDark),
            CorePageChannelButtonColor.BuildRules(branding?.ChannelButtonColors));

        return $"<style>{css}</style>{BuildCustomCssLink(branding)}";
    }

    /// <summary>
    /// Builds the styling head of a service page (confirmation, expired callback, email pages).
    /// </summary>
    /// <param name="branding">Effective page branding (null — no branding at all).</param>
    /// <param name="extraCss">Page-specific rules appended after the shared ones; null — none.</param>
    /// <returns>Ready-to-embed <c>&lt;style&gt;</c> element followed by an optional <c>&lt;link&gt;</c>.</returns>
    public static string BuildServicePageHead(CorePageBranding? branding, string? extraCss = null)
    {
        return BuildPageHead(branding, CorePageStyles.ServicePage, extraCss);
    }

    /// <summary>
    /// Builds the theme attribute of the <c>&lt;html&gt;</c> element of a service page (SPEC-015 §2.2).
    /// It is the one point that spells the attribute for those pages, so none of them can be left
    /// without it — a page with no attribute is painted light whatever the deployment asked for.
    /// </summary>
    /// <param name="branding">Effective page branding (null — the page follows the browser setting).</param>
    /// <returns>The attribute as <c>data-theme="…"</c>, with no surrounding whitespace.</returns>
    public static string BuildThemeAttribute(CorePageBranding? branding)
    {
        var theme = branding?.Theme ?? CorePageThemes.Auto;

        return $"{ThemeAttributeName}=\"{WebUtility.HtmlEncode(theme)}\"";
    }

    /// <summary>
    /// Builds the link to the integrator's stylesheet (SPEC-007 UI-054). The URL passes the scheme
    /// allowlist; an unset or rejected value yields an empty string.
    /// </summary>
    /// <param name="branding">Effective page branding.</param>
    /// <returns>The <c>&lt;link&gt;</c> element, or an empty string.</returns>
    public static string BuildCustomCssLink(CorePageBranding? branding)
    {
        var cssPath = branding?.CustomCssPath;
        if (string.IsNullOrEmpty(cssPath) || !IsAllowedUrlScheme(cssPath))
        {
            return string.Empty;
        }

        var sriAttr = !string.IsNullOrEmpty(branding!.CssSriHash)
            ? $" integrity=\"{WebUtility.HtmlEncode(branding.CssSriHash)}\" crossorigin=\"anonymous\""
            : string.Empty;

        return $"\n  <link rel=\"stylesheet\" href=\"{WebUtility.HtmlEncode(cssPath)}\"{sriAttr} />";
    }

    /// <summary>
    /// Builds the generated token block — the branded primary substituted into the token in the theme
    /// selectors. The brand color is a cut of the client's branding BY theme: the light value paints
    /// the light rendering, the dark one the dark and auto selectors, so a client whose brand is
    /// unreadable on a dark page states a second color instead of losing the theme.
    /// <para>
    /// A theme whose color is absent or malformed keeps the neutral canon of the token layer: the
    /// color of one theme is not substituted into the other, because a value stated for the light
    /// page says nothing about the dark one. A deployment that states a single color still brands
    /// both themes — that color is what BOTH parameters resolve to, so all three selectors are
    /// overridden with it.
    /// </para>
    /// <para>
    /// The bare <c>:root</c> of the token layer is not a light selector on its own — it is the light
    /// slot only because the dark selectors declared after it take the dark renderings back. This
    /// block honours that rule: the light color goes into a bare <c>:root</c> only when the dark
    /// selectors follow it here as well; otherwise the light selectors leave the dark renderings out
    /// themselves, so a dark page keeps the canon instead of the light brand.
    /// </para>
    /// <para>
    /// Every selector emitted here stays at the specificity of a bare <c>:root</c> — (0,1,0) — in all
    /// branches, which is what keeps the integrator's stylesheet able to redefine the same token by
    /// source order alone (SPEC-007 UI-054). Narrowing a block is therefore done with <c>:where()</c>,
    /// which adds no specificity, and never with a bare <c>:not()</c> chain.
    /// </para>
    /// </summary>
    /// <param name="primaryColor">Brand color of the light theme; anything but a valid #RRGGBB is
    /// treated as unset.</param>
    /// <param name="primaryColorDark">Brand color of the dark theme; unset — the dark and auto
    /// selectors keep the neutral canon.</param>
    /// <returns>CSS block, or an empty string when the neutral canon applies.</returns>
    public static string BuildBrandedTokens(string? primaryColor, string? primaryColorDark = null)
    {
        var light = IsValidBrandColor(primaryColor) ? primaryColor : null;
        var dark = IsValidBrandColor(primaryColorDark) ? primaryColorDark : null;

        if (light is null && dark is null)
        {
            return string.Empty;
        }

        var css = new StringBuilder();

        if (light is not null && dark is not null)
        {
            // Both slots are overridden here, so the light one keeps the bare :root of the token
            // layer: the dark selectors appended right below take the dark renderings back
            css.Append($$"""

                :root { --veriqa-primary: {{light}}; --veriqa-on-primary: {{BrandedOnPrimary}}; }
                """);
        }
        else if (light is not null)
        {
            // The dark slot stays on the neutral canon, so nothing below takes the dark renderings
            // back. A bare :root would beat the dark canon of the token layer (same specificity,
            // later source order) and paint the dark page with the light brand, so the selectors
            // exclude both dark renderings themselves - the forced dark page and the auto page on a
            // dark system. The auto page on a light system is branded through the paired media
            // query, since auto is the default mode of the page.
            // The exclusion is carried by :where(), which adds no specificity: a bare :not() chain
            // would raise the block to (0,3,0) and put it out of reach of the integrator's
            // :root { --veriqa-*: ... } (0,1,0), breaking the source-order override (SPEC-007 UI-054)
            css.Append($$"""

                :root:where(:not([data-theme="{{CorePageThemes.Dark}}"]):not([data-theme="{{CorePageThemes.Auto}}"])) { --veriqa-primary: {{light}}; --veriqa-on-primary: {{BrandedOnPrimary}}; }
                @media (prefers-color-scheme: light) {
                    [data-theme="{{CorePageThemes.Auto}}"] { --veriqa-primary: {{light}}; --veriqa-on-primary: {{BrandedOnPrimary}}; }
                }
                """);
        }

        if (dark is not null)
        {
            css.Append($$"""

                [data-theme="{{CorePageThemes.Dark}}"] { --veriqa-primary: {{dark}}; --veriqa-on-primary: {{BrandedOnPrimary}}; }
                @media (prefers-color-scheme: dark) {
                    [data-theme="{{CorePageThemes.Auto}}"] { --veriqa-primary: {{dark}}; --veriqa-on-primary: {{BrandedOnPrimary}}; }
                }
                """);
        }

        return css.ToString();
    }

    /// <summary>
    /// Checks that the color is a valid HEX in the #RRGGBB format (protection against CSS injection).
    /// An invalid branded color rolls the page back to the neutral palette of both themes.
    /// </summary>
    /// <param name="color">Color from configuration.</param>
    /// <returns>true if the color is valid.</returns>
    public static bool IsValidBrandColor(string? color)
    {
        return color is not null && HexColorPattern.IsMatch(color);
    }

    /// <summary>
    /// Checks that the URL scheme is safe (https, http, / — a relative path, but not protocol-relative //).
    /// Protection against javascript: / data: URI injections and protocol-relative URLs via configuration.
    /// </summary>
    /// <param name="url">URL to check.</param>
    /// <returns>true if the scheme is safe.</returns>
    public static bool IsAllowedUrlScheme(string url)
    {
        // The method validates the URL scheme to protect against XSS via configuration
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Allow relative paths (/path/to/file), but not protocol-relative URLs (//domain.com)
        return url.Length > 0 && url[0] is '/' && (url.Length < 2 || url[1] is not '/');
    }
}
