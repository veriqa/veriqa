// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The focus of the default answer of a question page of the "Core" contour — the page of the core's own
/// "Yes/No" question and the page of a web provider channel. "No" is the default answer: the page focuses
/// it when it opens (<c>autofocus</c>, SPEC-007 UI-128), so Enter answers it. A focus the page sets itself
/// matches <c>:focus-visible</c>, and the ring of the contour around a filled button reads as a second
/// outline of it; the script therefore keeps the ring of the button the page focused off until the user
/// presses a key. The focus itself stays, and so does Enter on it.
/// <para>
/// Norms the script and its rule implement. They are named here and not in the resource file, whose
/// comments are inlined into the page and reach the end user's browser (see
/// <see cref="CorePageEmbeddedText"/>):
/// </para>
/// <list type="bullet">
/// <item><description>the script marks the root of the document with <c>data-veriqa-focus-quiet</c> when the
/// page loads, and the rule of the shared layer (<c>veriqa-core-base.css</c>) drops the ring of a button of
/// the contour that carries <c>autofocus</c> while the mark stands; no other control loses its
/// ring;</description></item>
/// <item><description>the first key pressed anywhere on the page takes the mark off for good, so the ring is
/// back for every focus from then on, the one of the default answer included — a keyboard user sees where
/// the focus is as soon as they use the keyboard;</description></item>
/// <item><description>a page whose script does not run keeps the ring: without the mark the rule does not
/// apply.</description></item>
/// </list>
/// </summary>
internal static class CorePageDefaultAnswerFocus
{
    /// <summary>
    /// Resource name of the script (<c>UI/Scripts/veriqa-default-answer-focus.js</c>).
    /// </summary>
    private const string ScriptResourceName = "Veriqa.Core.ChannelAdapter.UI.Scripts.veriqa-default-answer-focus.js";

    /// <summary>
    /// Builds the script element, inlined under the CSP nonce of the request (SPEC-007 UI-040).
    /// </summary>
    /// <param name="cspNonce">CSP nonce of this request; without one the script goes out without the
    /// attribute, as every inline script of the contour does.</param>
    /// <returns>Markup of the script.</returns>
    public static string Script(string? cspNonce)
    {
        var nonceAttribute = string.IsNullOrEmpty(cspNonce)
            ? string.Empty
            : $" nonce=\"{WebUtility.HtmlEncode(cspNonce)}\"";

        return $"<script{nonceAttribute}>{CorePageEmbeddedText.Load(typeof(CorePageDefaultAnswerFocus).Assembly, ScriptResourceName)}</script>";
    }
}
