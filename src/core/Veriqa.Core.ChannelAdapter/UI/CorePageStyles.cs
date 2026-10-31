// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Access to the stylesheets of the "Core" UI contour shipped as embedded resources.
/// The files are never served over a URL: their content is inlined into the generated page, so the
/// document stays self-contained (SPEC-015 §1) while the styles themselves live in real .css files.
/// <para>
/// The shared part of the contour (token layer and the service-page rules) lives in this assembly
/// because it is the lowest project both consumers reference: the sign-in window and the callback
/// pages come from Veriqa.Core.AuthServer, the email pages from this assembly, and a stylesheet
/// placed in the auth server would be unreachable from here. The window's own stylesheet stays in
/// its own assembly and is read through the shared reader <see cref="CorePageEmbeddedText"/>.
/// </para>
/// <para>
/// Loading resources is an implementation detail of the contour, not a seam for integrators: the
/// public surface of the package is <see cref="CorePageHead"/> ("build me the styling head"). The
/// type is therefore internal and shared with Veriqa.Core.AuthServer through InternalsVisibleTo.
/// </para>
/// </summary>
internal static class CorePageStyles
{
    /// <summary>
    /// Resource name of the shared layer (<c>UI/Styles/veriqa-core-base.css</c>).
    /// </summary>
    private const string BaseResourceName = "Veriqa.Core.ChannelAdapter.UI.Styles.veriqa-core-base.css";

    /// <summary>
    /// Resource name of the service-page rules (<c>UI/Styles/veriqa-service-page.css</c>).
    /// </summary>
    private const string ServicePageResourceName = "Veriqa.Core.ChannelAdapter.UI.Styles.veriqa-service-page.css";

    /// <summary>
    /// Shared layer of the contour: <c>:root</c>, dark and auto theme overrides, reduced-motion,
    /// the element reset and the primitives shared by both page families (the spinner, the button, the
    /// text link and the divider, SPEC-015 §4.3, §4.6, §4.11, §4.12). Included by every page of the contour.
    /// <para>
    /// Norms the layer implements (they are named here because the stylesheet's own comments reach
    /// the browser, see <see cref="CorePageEmbeddedText"/>): spacing tokens reuse the site scale name
    /// where the value matches and take a numeric-by-value suffix otherwise (SPEC-015 §2.1); the dark
    /// theme is forced by <c>data-theme="dark"</c>, and the OS setting is followed only for
    /// <c>data-theme="auto"</c> (SPEC-015 §2.2); <c>prefers-reduced-motion</c> collapses animations and
    /// transitions (SPEC-015 §2.3); the attribution line under the card is kept against the
    /// integrator's stylesheet (SPEC-015 §4.18); on a computer the root font of every page of the
    /// contour follows the height of the browser window down to a floor, so that the card fits with a
    /// small margin, the spacing tokens follow it in rem and a QR image keeps its size, while a phone
    /// scrolls the page as before, a window lower than a height bound in em (a page zoomed in far enough,
    /// or a large default font of the browser) keeps the user's own font size, and a card that still does
    /// not fit scrolls with its top in view (SPEC-015 §2.5).
    /// </para>
    /// </summary>
    internal static string Base => CorePageEmbeddedText.Load(typeof(CorePageStyles).Assembly, BaseResourceName);

    /// <summary>
    /// Rules of the service pages (confirmation, expired callback, email adapter pages, the form of the
    /// claims completion).
    /// Included in addition to <see cref="Base"/>.
    /// <para>
    /// Primitives of SPEC-015 the rule set implements for this page family: the card as the
    /// composition frame (§4.7), the title and text blocks (§4.15), the status plate of the
    /// confirmation page (§4.4), the email entry form (§4.10), the labelled fields of a form
    /// (§4.14), the secondary action in link styling (§4.11) and the focus indicator as a
    /// <c>:focus-visible</c> outline rather than a border tint (§6.2).
    /// </para>
    /// </summary>
    internal static string ServicePage =>
        CorePageEmbeddedText.Load(typeof(CorePageStyles).Assembly, ServicePageResourceName);
}
