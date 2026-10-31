// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// The button that closes the tab of a page of the "Core" contour, with the line telling the tab can be
/// closed by hand, its client script and its rules — one home for every page that offers it: the
/// "sign-in session expired" page of the callback, the pages a link of a letter opens and the pages that
/// report an outcome away from the sign-in window. It lives in this assembly because it is the lowest
/// project those page families reference.
/// <para>
/// Norms the script and the rules implement. They are named here and not in the resource files, whose
/// comments are inlined into the page and reach the end user's browser (see
/// <see cref="CorePageEmbeddedText"/>):
/// </para>
/// <list type="bullet">
/// <item><description>the button asks the browser to close the window (SPEC-007 UI-109) and does nothing
/// else: it changes no state of a transaction or of a link, whatever its caption;</description></item>
/// <item><description>a browser closes by script only a window a script opened, and says nothing when it
/// refuses: a window still open shortly after the request therefore gets the line telling the user the
/// tab can be closed by hand, which the page carries hidden until then. The line takes a row of its own
/// under the buttons, and a row that follows a status plate stands off it;</description></item>
/// <item><description>the handler is attached by the script — the page carries no inline handler and no
/// <c>javascript:</c> address, which the content security policy of the contour would refuse
/// (SPEC-007 UI-040);</description></item>
/// <item><description>a page carries one such button at most: the script finds it by its id;</description></item>
/// <item><description>the row under the line of an outcome is wrapped in an element of its own, which a page
/// that follows the outcome by script keeps hidden until the ending — a row of actions lays itself out by
/// its own rule, which would win over the attribute — and which stands off what is above it, whatever stands
/// there.</description></item>
/// </list>
/// </summary>
internal static class CorePageCloseAction
{
    /// <summary>
    /// Natural Key of the caption of the button when closing is the whole action of the page.
    /// </summary>
    public const string CloseText = "Close";

    /// <summary>
    /// Natural Key of the line shown when the browser left the window open.
    /// </summary>
    public const string CloseHint = "You can close this tab.";

    /// <summary>
    /// Identifier of the button. The script finds the button by a bare literal of the same value
    /// (<c>getElementById("veriqa-close")</c>) — nothing checks the pair, so the two are renamed together.
    /// </summary>
    public const string CloseButtonId = "veriqa-close";

    /// <summary>
    /// Identifier of the line shown when the window stayed open. The script finds it by a bare literal of
    /// the same value (<c>getElementById("veriqa-close-hint")</c>) — renamed together, as
    /// <see cref="CloseButtonId"/> is.
    /// </summary>
    public const string CloseHintId = "veriqa-close-hint";

    /// <summary>
    /// Class of both forms of the line — the one the script shows and the one a browser without scripts
    /// shows; the rules give it a row of its own.
    /// </summary>
    private const string HintClass = "veriqa-close-hint";

    /// <summary>
    /// Class of the element that wraps the row under the line of an outcome.
    /// </summary>
    private const string OutcomeRowClass = "veriqa-close-outcome";

    /// <summary>
    /// Class of the element that lets the button share a row of actions with a form, as an action of the
    /// email block without a form of its own does.
    /// </summary>
    private const string RowItemClass = "veriqa-field";

    /// <summary>
    /// Resource name of the script (<c>UI/Scripts/veriqa-close-tab.js</c>).
    /// </summary>
    private const string ScriptResourceName = "Veriqa.Core.ChannelAdapter.UI.Scripts.veriqa-close-tab.js";

    /// <summary>
    /// Resource name of the rules (<c>UI/Styles/veriqa-close-tab.css</c>).
    /// </summary>
    private const string StylesResourceName = "Veriqa.Core.ChannelAdapter.UI.Styles.veriqa-close-tab.css";

    /// <summary>
    /// The texts of the action an owning level may override (SPEC-012 §4.3, key <c>AuthPage.Texts</c>);
    /// each owner of a page that offers it contributes them.
    /// </summary>
    internal static IReadOnlyList<string> OverridableTexts { get; } = [CloseText, CloseHint];

    /// <summary>
    /// The rules of the action, appended to the rules of the service pages.
    /// </summary>
    public static string Css => CorePageEmbeddedText.Load(typeof(CorePageCloseAction).Assembly, StylesResourceName);

    /// <summary>
    /// Builds the row of a page whose whole action is to close the tab: the button and the line.
    /// </summary>
    /// <param name="caption">Caption of the button, not encoded.</param>
    /// <param name="hint">The line telling the tab can be closed by hand, not encoded.</param>
    /// <returns>Ready markup of the row.</returns>
    public static string Row(string caption, string hint)
    {
        return $"""
            <div class="veriqa-actions">
                  {Button(caption, "veriqa-btn")}
                  {Hint(hint)}
                </div>
            """;
    }

    /// <summary>
    /// Builds the row under the line of an outcome: the row of <see cref="Row"/> in an element of its own.
    /// </summary>
    /// <param name="id">Identifier of the element, by which the script of the page finds it.</param>
    /// <param name="hidden">Whether the element waits for the script of the page to show it.</param>
    /// <param name="caption">Caption of the button, not encoded.</param>
    /// <param name="hint">The line telling the tab can be closed by hand, not encoded.</param>
    /// <returns>Ready markup of the element.</returns>
    public static string OutcomeRow(string id, bool hidden, string caption, string hint)
    {
        var hiddenAttribute = hidden ? " hidden" : string.Empty;

        return $"<div class=\"{OutcomeRowClass}\" id=\"{WebUtility.HtmlEncode(id)}\"{hiddenAttribute}>{Row(caption, hint)}</div>";
    }

    /// <summary>
    /// Builds the button as the secondary action of a row whose main action is a form, and the line under
    /// both: the row of actions holds the form, then these.
    /// </summary>
    /// <param name="caption">Caption of the button, not encoded.</param>
    /// <param name="hint">The line telling the tab can be closed by hand, not encoded.</param>
    /// <returns>Ready markup of the button and the line, to be put into the row after the form.</returns>
    public static string BesideForm(string caption, string hint)
    {
        return $"""
            <div class="{RowItemClass}">{Button(caption, "veriqa-btn veriqa-btn-decline")}</div>
                  {Hint(hint)}
            """;
    }

    /// <summary>
    /// Builds the script element of the button, inlined under the CSP nonce of the request
    /// (SPEC-007 UI-040).
    /// </summary>
    /// <param name="cspNonce">CSP nonce of this request; without one the script goes out without the
    /// attribute, as every inline script of the contour does.</param>
    /// <returns>Markup of the script.</returns>
    public static string Script(string? cspNonce)
    {
        var nonceAttribute = string.IsNullOrEmpty(cspNonce)
            ? string.Empty
            : $" nonce=\"{WebUtility.HtmlEncode(cspNonce)}\"";

        return $"<script{nonceAttribute}>{CorePageEmbeddedText.Load(typeof(CorePageCloseAction).Assembly, ScriptResourceName)}</script>";
    }

    /// <summary>
    /// Builds the button.
    /// </summary>
    /// <param name="caption">Caption of the button, not encoded.</param>
    /// <param name="cssClass">Classes of the button.</param>
    /// <returns>Markup of the button.</returns>
    private static string Button(string caption, string cssClass) =>
        $"<button type=\"button\" class=\"{cssClass}\" id=\"{CloseButtonId}\">{WebUtility.HtmlEncode(caption)}</button>";

    /// <summary>
    /// Builds the line telling the tab can be closed by hand.
    /// </summary>
    /// <remarks>
    /// The line is in the document twice on purpose. Hidden, for the script to show once the browser has
    /// left the window open; and inside <c>noscript</c>, because a browser that runs no script leaves the
    /// button without an effect and the line is then the only thing that tells the user what to do.
    /// </remarks>
    /// <param name="hint">The line, not encoded.</param>
    /// <returns>Markup of both forms of the line.</returns>
    private static string Hint(string hint)
    {
        // One resolved text for both places the line stands at.
        var encoded = WebUtility.HtmlEncode(hint);

        return $"""
            <noscript class="veriqa-instruction {HintClass}">{encoded}</noscript>
                  <p class="veriqa-instruction {HintClass}" id="{CloseHintId}" role="status" hidden>{encoded}</p>
            """;
    }
}
