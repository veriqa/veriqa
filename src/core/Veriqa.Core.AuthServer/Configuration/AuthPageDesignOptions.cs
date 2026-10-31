// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Authentication page design settings (SPEC-012 §4.2–4.3).
/// Configuration section: Veriqa:AuthPageDesign.
/// </summary>
public sealed class AuthPageDesignOptions
{
    /// <summary>
    /// Configuration section name. It is taken from the owner of the page design keys rather than
    /// spelled again here: the same section is the CORE level of every one of them, and a second
    /// literal of it would be a second answer able to drift from the addresses those keys declare.
    /// </summary>
    public const string SectionName = CorePageBrandingConfigKeys.CoreSectionName;

    /// <summary>
    /// Built-in design preset (SPEC-012 §4.2). The shipped value is taken from the owner of the key
    /// rather than spelled again here: the key DECLARES what its core level answers over a section
    /// stating nothing, and a second literal of that answer would be a second answer able to drift.
    /// When <see cref="CustomCssPath"/> is set, the preset is ignored (CFG-023).
    /// </summary>
    public DesignPreset Preset { get; set; } = CorePageBrandingConfigKeys.DefaultPreset;

    /// <summary>
    /// Page color scheme (SPEC-015 §2.2): Auto (follows the system setting), Light (light/white),
    /// Dark (dark). An explicit Light/Dark value takes precedence over the preset's color scheme. The
    /// setting covers every page the core generates — the sign-in window and the service pages. The
    /// shipped value (<see cref="AuthPageTheme.Auto"/>) is taken from the owner of the key, for the
    /// reason <see cref="Preset"/> states.
    /// </summary>
    public AuthPageTheme Theme { get; set; } = CorePageBrandingConfigKeys.DefaultTheme;

    /// <summary>
    /// Logo URL of the brand row of the sign-in page, read in every preset but
    /// <see cref="DesignPreset.Minimal"/>, where the row is not rendered. Null — this level states no
    /// logo. The key has no shipped value; the shipped default of the row is markup: when no level
    /// states either this logo or <see cref="BrandName"/>, the row carries the product's diamond and
    /// wordmark, and a single value stated by any level replaces that default as a whole. Before this
    /// default existed an unset logo meant "no logo" — it now means "the product's mark", unless a name
    /// is stated.
    /// </summary>
    public string? LogoUrl { get; set; }

    /// <summary>
    /// Primary color in HEX format (for the <see cref="DesignPreset.Branded"/> preset).
    /// Applied only with <see cref="DesignPreset.Branded"/>; otherwise — the default color.
    /// </summary>
    public string? PrimaryColor { get; set; }

    /// <summary>
    /// Logo URLs stated for a single theme, keyed by theme name (<c>light</c>, <c>dark</c>). A theme
    /// absent from the map takes <see cref="LogoUrl"/>. Null — this level states no themed logo at
    /// all, and every theme takes <see cref="LogoUrl"/>.
    /// <para>
    /// On the RESULT of a resolution the map carries the value resolved for EACH theme, and
    /// <see cref="LogoUrl"/> stays unset: the server cannot know which theme the browser will pick at
    /// <c>Auto</c>, so both travel to the page — and neither of them is the other's fallback, because
    /// the chain of the key falls back to the common value of a level and never to a value stated for
    /// the neighbouring theme.
    /// </para>
    /// </summary>
    public IDictionary<string, string>? LogoUrlByTheme { get; set; }

    /// <summary>
    /// Primary colors stated for a single theme, keyed by theme name; see
    /// <see cref="LogoUrlByTheme"/> for the shape of the map and for what it means on the result of a
    /// resolution. A theme absent from the map takes <see cref="PrimaryColor"/>.
    /// </summary>
    public IDictionary<string, string>? PrimaryColorByTheme { get; set; }

    /// <summary>
    /// Brand name shown in the brand row of the sign-in page, read in every preset but
    /// <see cref="DesignPreset.Minimal"/>, where the row is not rendered. Null or blank — this level
    /// states no name: with a logo stated the row carries the logo alone, and with neither stated it
    /// carries the shipped default, the product's diamond and wordmark (see <see cref="LogoUrl"/>).
    /// <para>
    /// The name is resolved on its own, not in a pair with <see cref="LogoUrl"/>: either of the two may
    /// come from a level the other states nothing at. It is plain text — it is encoded on output — and it
    /// is cut by no theme: a text takes its color from the page. The value is a Natural Key, so a host
    /// locale file may translate it; a name no locale file carries is shown as it is.
    /// </para>
    /// </summary>
    public string? BrandName { get; set; }

    /// <summary>
    /// Footer block of the sign-in page — the integrator's markup (legal links, a note, a contact)
    /// shown as the last element of the card in every preset. Null or blank — no block.
    /// <para>
    /// The value is MARKUP and is emitted unsanitized: its correctness and its safety are the
    /// integrator's responsibility. Its shape is checked
    /// (<see cref="Resolution.AuthServerConfigKeys.PageFooterHtml"/>): a small set of inline tags, the
    /// attributes <c>href</c>, <c>target</c>, <c>rel</c> and <c>class</c> — no class name of the page's own
    /// namespace (<c>veriqa</c>, <c>veriqa-*</c>) — balanced tags. Every link
    /// needs <c>target="_blank"</c>, because leaving the page in the same tab ends the live sign-in, and
    /// coming back requests <c>GET /connect/authorize</c> again, which creates a new transaction. The
    /// value is a Natural Key, translated by the host locale files.
    /// </para>
    /// </summary>
    public string? FooterHtml { get; set; }

    /// <summary>
    /// Content of the block the sign-in page shows in place of the QR code and the channel choice once
    /// the hop has started — text or markup. Null or blank — the page's own localized text.
    /// <para>
    /// The value is MARKUP of the same admitted shape as <see cref="FooterHtml"/>
    /// (<see cref="Resolution.AuthServerConfigKeys.PageHopInstructionHtml"/>) and is emitted
    /// unsanitized. It carries no style: the block is styled by the page's rules and by the custom
    /// stylesheet. The value is a Natural Key, translated by the host locale files.
    /// </para>
    /// </summary>
    public string? HopInstructionHtml { get; set; }

    /// <summary>
    /// Path to a custom CSS file. Null — the standard CSS.
    /// </summary>
    public string? CustomCssPath { get; set; }

    /// <summary>
    /// Path to a custom JS file. Null — the standard JS.
    /// </summary>
    public string? CustomJsPath { get; set; }

    /// <summary>
    /// SRI hash for the external CSS file. Null — SRI is not applied.
    /// </summary>
    public string? CssSriHash { get; set; }

    /// <summary>
    /// SRI hash for the external JS file. Null — SRI is not applied.
    /// </summary>
    public string? JsSriHash { get; set; }

    /// <summary>
    /// Gate of the risky customization mode (SPEC-012 §4.3, CFG-031/CFG-212): allow a client entry
    /// (<c>Veriqa:OpenIddict:Clients</c>) to state its own <see cref="CustomJsPath"/> and
    /// <see cref="JsSriHash"/>. False by default — without it the application level of the script is
    /// refused by the resolver and the script of this section applies to every client. The gate is
    /// deployment-wide rather than per-client: the party that permits an executable script on the
    /// sign-in page is the owner of the page, not the application asking for it.
    /// </summary>
    public bool CustomJsAllowApplicationOverride { get; set; }

    /// <summary>
    /// Path (or URL) to the SignalR JavaScript client loaded on the authentication page.
    /// Null or an empty string — the package's default bundled resource is used
    /// (<see cref="Constants.SignalRConstants.SignalRClientPath"/>). Overriding lets the
    /// package consumer specify their own path/URL (e.g. a CDN or a self-hosted copy)
    /// without rebuilding the package.
    /// </summary>
    public string? SignalRClientPath { get; set; }

    /// <summary>
    /// QR image generation settings (SPEC-015 §4.2): the pixel scale of the generated PNG, with an
    /// optional per-channel override. Presentational group, resolved by ownership levels
    /// (SPEC-003 §17.3, SPEC-012 §10.3).
    /// </summary>
    public QrCodeOptions QrCode { get; set; } = new();

    /// <summary>
    /// Returns the logo URL effective for a theme: the value stated for that theme when there is one,
    /// otherwise the common <see cref="LogoUrl"/>. Blank counts as unstated on either side.
    /// <para>
    /// The two branches are the two steps of the key's fallback chain, and they hold both on the
    /// configuration object of a level and on the RESULT of a resolution (see
    /// <see cref="LogoUrlByTheme"/>) — which is why the page reads the pair through this method
    /// instead of picking the fields apart.
    /// </para>
    /// </summary>
    /// <param name="theme">Theme name (<c>light</c> or <c>dark</c>).</param>
    /// <returns>Logo URL for the theme, or null when neither step states one.</returns>
    public string? GetLogoUrl(string theme) =>
        DimensionedConfigValue.TextFor(LogoUrlByTheme, theme)
        ?? (string.IsNullOrWhiteSpace(LogoUrl) ? null : LogoUrl);

    /// <summary>
    /// Returns the primary color effective for a theme; see <see cref="GetLogoUrl"/> for the two
    /// branches and where they hold.
    /// </summary>
    /// <param name="theme">Theme name (<c>light</c> or <c>dark</c>).</param>
    /// <returns>Primary color for the theme, or null when neither step states one.</returns>
    public string? GetPrimaryColor(string theme) =>
        DimensionedConfigValue.TextFor(PrimaryColorByTheme, theme)
        ?? (string.IsNullOrWhiteSpace(PrimaryColor) ? null : PrimaryColor);
}
