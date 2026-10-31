// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// Access to the stylesheet of the sign-in window shipped as an embedded resource of this assembly.
/// The token layer it relies on is shared with the service pages and lives in
/// <see cref="CorePageStyles"/>; only the window's own rules are stored here.
/// <para>
/// Norms the window's rules implement. They are named here and not in the stylesheet, whose comments
/// are inlined into the page and reach the end user's browser (see <see cref="CorePageEmbeddedText"/>):
/// </para>
/// <list type="bullet">
/// <item><description>the channel brand colors are literals and are never repainted by the client's
/// branding — they are canonical constants of an external brand (SPEC-015 §3.8; docs/design-rules.md
/// items 1 and 5);</description></item>
/// <item><description>the Minimal preset shows the QR and the buttons only, with no decorative
/// elements (SPEC-012 §4.2);</description></item>
/// <item><description>a clipped third-party label gets the tooltip of the contour
/// (SPEC-015 §4.16);</description></item>
/// <item><description>on a phone under <c>DesktopOnly</c> and wherever the server renders the
/// container modifier (<c>Never</c>) the channels are a list of buttons without tabs — a composition of
/// the existing primitives, separate from hiding the QR (SPEC-025 HOP-145, HOP-146; SPEC-015 §4.6,
/// §4.8–4.10, §4.15);</description></item>
/// <item><description>on a page of the unified hop QR code that keeps the per-channel QR codes the state of
/// the "Any channel" tab lays the panels out as that list of buttons under the tab strip, without their own
/// QR codes, under the unified one or beside it on the split card below, and a channel tab shows its panel
/// with its QR code and no unified QR code; a phone under <c>DesktopOnly</c> gets the buttons layout with
/// no QR code of either kind (SPEC-025 HOP-146, HOP-187);</description></item>
/// <item><description>on a page of the unified hop QR code whose desktop shows the channel buttons the card
/// is the split card on a window wide enough for it: 680px, the QR code on the left and the buttons on the
/// right, the same width on every tab, a panel with no QR code of its own as one column in the middle, the
/// controls under the columns and the spare start code in the block of the started hop one column wide and
/// no "OR" divider of Email; a narrower window and a phone
/// under <c>DesktopOnly</c> keep the one-column card. The strip of a page with the "Any channel" tab gives
/// that tab a row of its own over the channel tabs two to a row, with no empty cell; when that tab shows the
/// unified QR code alone, its panels leave under it everywhere a QR code is shown (SPEC-025 HOP-187;
/// SPEC-015 §4.7, §4.8);</description></item>
/// <item><description>the channel buttons follow their own visibility setting the way the QR code
/// follows its own: the value arrives on the root element and the page applies it by the device marker
/// (SPEC-007 UI-020); the button revealing the email form leads into no channel and stays
/// (SPEC-025 HOP-145);</description></item>
/// <item><description>under the grid layout every channel panel is on screen at once, in a row that
/// wraps: the card is widened by the number of its channels and never past the window, the controls under
/// the row and the spare start code in the block of the started hop stay one column wide, a panel is a
/// group labelled by the visible name of its channel instead of a tab panel (WAI-ARIA), and the rules
/// use neither the spacing property of a flex row nor a comparison function, which the browsers of
/// televisions do not read; a phone under <c>DesktopOnly</c> gets the buttons layout from this markup
/// as it does from the tabs (SPEC-025 HOP-146);</description></item>
/// <item><description>once the hop has started the channel choice gives way to the instruction block
/// inside the waiting state, and the set of page states stays as it is (SPEC-025 HOP-101,
/// SPEC-007 §7.3);</description></item>
/// <item><description>on the scanner page with a choice of channels the chosen channel is a display mode
/// inside the waiting state as well: the title of the page, the lead instruction and the channel choice give
/// way to the heading of that channel and to the way back to the choice, a text link under it; one heading is
/// on screen at a time, and the way back leaves for good once the answer is given elsewhere, the
/// confirmation is being processed or the page has ended, the heading of the channel with it — taken away by
/// the reaction of the status block, not by the stylesheet (SPEC-025 HOP-193);</description></item>
/// <item><description>the link that offers the spare start code on a phone, under the status line, is
/// bold, in the text color and not smaller than the main text of the card, so it does not read as a
/// secondary toggle; its block leaves the page with every ending and once the answer is given elsewhere
/// (SPEC-007 UI-119);</description></item>
/// <item><description>the cancel button ("Cancel sign-in", "Cancel confirmation") is the secondary variant
/// of the button primitive, whose rule lives in the shared layer; the page only spaces the form and drops it
/// on an expired, a failed and a completed page (SPEC-015 §4.6);</description></item>
/// <item><description>the QR image keeps the 200px scanning minimum and the module density verified at
/// 220px; with the wide OS scrollbar setting both can be missed on part of the viewport range, which
/// is an accepted limitation (SPEC-015 §4.2);</description></item>
/// <item><description>the focus indicator is a <c>:focus-visible</c> outline (SPEC-015 §6.2); the
/// integrator's footer is painted with the muted text token because the soft one stays below 4.5:1 on
/// the light background (SPEC-015 §6.1), and not with the primary color, since the Branded preset
/// repaints only the listed elements (docs/design-rules.md item 3);</description></item>
/// <item><description>a tab keeps the target size floor (<c>--veriqa-target-min-size</c>) when the scale of
/// a short window shrinks its text and padding (SPEC-015 §6.1, WCAG 2.5.8).</description></item>
/// </list>
/// </summary>
internal static class AuthPageStyles
{
    /// <summary>
    /// Resource name of the window rules (<c>UI/Styles/veriqa-auth-page.css</c>).
    /// </summary>
    private const string ResourceName = "Veriqa.Core.AuthServer.UI.Styles.veriqa-auth-page.css";

    /// <summary>
    /// Rules of the sign-in window (without the token layer).
    /// </summary>
    public static string Css => CorePageEmbeddedText.Load(typeof(AuthPageStyles).Assembly, ResourceName);
}
