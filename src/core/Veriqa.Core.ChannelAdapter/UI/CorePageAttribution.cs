// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Carrier of the attribution line of the "Core" UI contour (SPEC-015 §4.18): the product mark
/// (<see cref="Text"/>) under the card of every page the contour generates. The markup and the guard
/// script have this one home; the rules live in the shared layer of the contour
/// (<c>UI/Styles/veriqa-core-base.css</c>), which every page embeds.
/// <para>
/// Three layers keep the mark against settings and customization, each against its own failure:
/// </para>
/// <list type="bullet">
/// <item><description>
/// the server emits the node, so no setting leaves a page without it;
/// </description></item>
/// <item><description>
/// the rules of the shared layer win over the integrator's stylesheet: they are important
/// declarations inside an anonymous cascade layer, and a layered important declaration beats every unlayered one
/// whatever its specificity or source order — the integrator's stylesheet is linked after the inline
/// styles, so the source order alone would lose;
/// </description></item>
/// <item><description>
/// the guard script restores the node and its visibility after the integrator's script. It is placed
/// on the sign-in window only, the one page that links such a script.
/// </description></item>
/// </list>
/// <para>
/// None of the layers stops a fork that edits the sources, and they do not try to: that boundary is
/// drawn by licenses/TRADEMARK.md, not by code.
/// </para>
/// <para>
/// Whether the mark is shown at all is <see cref="IsShown"/> — a constant of the build, one for the
/// pages and for the QR image of the confirmation answer. There is no configuration key, no license key
/// and no service to substitute at run time.
/// </para>
/// </summary>
internal static class CorePageAttribution
{
    /// <summary>
    /// Whether the attribution is shown: <c>false</c> only in the white-label build, compiled with the
    /// <c>VERIQA_WHITELABEL</c> symbol. A <c>const</c> on purpose — the compiler writes its value into
    /// the IL of every caller, so a built assembly keeps no point where the answer could be replaced.
    /// </summary>
#if VERIQA_WHITELABEL
    internal const bool IsShown = false;
#else
    internal const bool IsShown = true;
#endif

    /// <summary>
    /// Text of the attribution. A brand constant rather than a Natural Key: the phrase is shown
    /// unmodified, and a translation would modify it.
    /// </summary>
    internal const string Text = "Powered by Veriqa";

    /// <summary>
    /// Language of <see cref="Text"/> — a primary language subtag.
    /// </summary>
    internal const string TextLanguage = "en";

    /// <summary>
    /// Class of the attribution node: the hook of its rules in the shared layer and of the guard script.
    /// </summary>
    internal const string CssClass = "veriqa-attribution";

    /// <summary>
    /// Separator of the subtags of a language tag.
    /// </summary>
    private const char LanguageSubtagSeparator = '-';

    /// <summary>
    /// Builds the attribution line — a document-level <c>&lt;footer&gt;</c> placed right after the card
    /// of the page. The text is not localized, so the node declares its own language whenever the page
    /// is in another one (WCAG 3.1.2).
    /// </summary>
    /// <param name="contentLanguage">Language of the page, as its <c>lang</c> attribute states it.</param>
    /// <returns>Markup of the line, or an empty string when the build does not show it.</returns>
    internal static string BuildLine(string contentLanguage)
    {
        ArgumentNullException.ThrowIfNull(contentLanguage);

        // A conditional expression rather than an early return: with a constant condition a statement
        // branch would be unreachable code in one of the two builds (CS0162).
        return IsShown ? LineMarkup(contentLanguage) : string.Empty;
    }

    /// <summary>
    /// Markup of the attribution line, built unconditionally.
    /// </summary>
    /// <param name="contentLanguage">Language of the page.</param>
    /// <returns>Markup of the line.</returns>
    private static string LineMarkup(string contentLanguage)
    {
        var languageAttribute = IsTextLanguage(contentLanguage)
            ? string.Empty
            : $" lang=\"{TextLanguage}\"";

        return $"""<footer class="{CssClass}"{languageAttribute}>{CorePageBrandMark.VeriqaDiamondSvg}<span>{WebUtility.HtmlEncode(Text)}</span></footer>""";
    }

    /// <summary>
    /// Builds the guard script of the attribution line. It must be emitted right after
    /// <see cref="BuildLine"/>: the script takes the element preceding it as the line to keep.
    /// <para>
    /// The guard snapshots the line as the server rendered it and watches both the line and its
    /// parent. Any change to the line — an inline style, a class, a <c>hidden</c> attribute, its text —
    /// replaces it with a fresh copy of the snapshot; a removed line is put back in its place; another
    /// element carrying the line's class next to it is removed, so it cannot stand in for the line.
    /// Every repair runs only when the state differs from the snapshot, so the mutations the repair
    /// itself causes find nothing to do and the observers settle instead of looping.
    /// </para>
    /// </summary>
    /// <param name="cspNonce">CSP nonce of the request; null or empty — the attribute is left off, as on
    /// every other inline script of the contour.</param>
    /// <returns>The inline script, or an empty string when the build does not show the line.</returns>
    internal static string BuildGuard(string? cspNonce) =>
        IsShown ? GuardScript(cspNonce) : string.Empty;

    /// <summary>
    /// The guard script of the attribution line, built unconditionally.
    /// </summary>
    /// <param name="cspNonce">CSP nonce of the request.</param>
    /// <returns>The inline script.</returns>
    private static string GuardScript(string? cspNonce)
    {
        var nonceAttribute = string.IsNullOrEmpty(cspNonce)
            ? string.Empty
            : $" nonce=\"{WebUtility.HtmlEncode(cspNonce)}\"";

        return $$"""
            <script{{nonceAttribute}}>
              (function () {
                "use strict";
                var className = "{{CssClass}}";
                var guard = document.currentScript;
                var home = guard ? guard.parentNode : null;
                if (!home || !window.MutationObserver) { return; }
                var mark = guard.previousElementSibling;
                if (!mark || !mark.classList.contains(className)) { return; }
                var pristine = mark.cloneNode(true);
                var markObserver = new MutationObserver(heal);
                var homeObserver = new MutationObserver(heal);

                function watch(node) {
                  markObserver.disconnect();
                  mark = node;
                  markObserver.observe(mark, { attributes: true, childList: true, characterData: true, subtree: true });
                }

                function heal() {
                  if (!mark.isEqualNode(pristine)) {
                    var fresh = pristine.cloneNode(true);
                    if (mark.parentNode) {
                      mark.parentNode.replaceChild(fresh, mark);
                    }
                    watch(fresh);
                  }
                  if (mark.parentNode !== home) {
                    home.insertBefore(mark, guard.parentNode === home ? guard : null);
                  }
                  for (var i = home.children.length - 1; i >= 0; i--) {
                    var sibling = home.children[i];
                    if (sibling !== mark && sibling.classList.contains(className)) {
                      home.removeChild(sibling);
                    }
                  }
                }

                watch(mark);
                homeObserver.observe(home, { childList: true });
              })();
            </script>
            """;
    }

    /// <summary>
    /// Tells whether the page language is the language of <see cref="Text"/>, comparing primary
    /// subtags case-insensitively (<c>en</c>, <c>en-GB</c> and <c>EN</c> are all English).
    /// </summary>
    /// <param name="contentLanguage">Language of the page.</param>
    /// <returns><c>true</c> when the node needs no language of its own.</returns>
    private static bool IsTextLanguage(string contentLanguage)
    {
        var separatorIndex = contentLanguage.IndexOf(LanguageSubtagSeparator);
        var primarySubtag = separatorIndex < 0
            ? contentLanguage.AsSpan()
            : contentLanguage.AsSpan(0, separatorIndex);

        return primarySubtag.Equals(TextLanguage, StringComparison.OrdinalIgnoreCase);
    }
}
