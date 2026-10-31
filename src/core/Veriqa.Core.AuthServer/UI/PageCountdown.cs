// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// The countdown to the expiry of a transaction, as the pages of the contour show it: the sign-in window
/// and the page of the claims completion carry the same element, the same rules and the same drawing
/// function, so the figure cannot look or behave differently on the two (SPEC-007 UI-094, UI-125;
/// SPEC-015 §4.5).
/// <para>
/// Norms the script and the rules implement. They are named here and not in the files themselves, whose
/// comments are inlined into the page and reach the end user's browser (see
/// <see cref="CorePageEmbeddedText"/>):
/// </para>
/// <list type="bullet">
/// <item><description>the figure is <c>m:ss</c>, or the seconds alone under a minute, in the monospace
/// face of the contour; at 30 seconds and less it takes the error colour, and loses it again when the
/// remainder is replanned back above the threshold (SPEC-015 §4.5);</description></item>
/// <item><description>the shared function only draws and plans the next tick, never past the deadline.
/// Zero on the clock is a question for the server: what is asked and what the answer does belongs to
/// the page (SPEC-007 UI-094, UI-125);</description></item>
/// <item><description>the element is not a live region: a figure changing every second is not announced
/// (SPEC-015 §4.5).</description></item>
/// </list>
/// </summary>
internal static class PageCountdown
{
    /// <summary>
    /// Resource name of the drawing function (<c>UI/Scripts/veriqa-countdown.js</c>).
    /// </summary>
    private const string ScriptResourceName = "Veriqa.Core.AuthServer.UI.Scripts.veriqa-countdown.js";

    /// <summary>
    /// Resource name of the rules (<c>UI/Styles/veriqa-countdown.css</c>).
    /// </summary>
    private const string StylesResourceName = "Veriqa.Core.AuthServer.UI.Styles.veriqa-countdown.css";

    /// <summary>
    /// The element of the countdown, empty until the script of the page draws the first figure. The scripts
    /// of both pages read it back by a bare literal of its identifier
    /// (<c>getElementById("veriqa-countdown")</c>) — nothing checks the pair, so they are renamed together.
    /// </summary>
    public const string Markup = """<div class="veriqa-countdown" id="veriqa-countdown" aria-live="off"></div>""";

    /// <summary>
    /// Milliseconds a page may count down from <paramref name="at"/> to <paramref name="expiresAt"/>:
    /// rounded down and never negative, so a page does not count on time the server does not have.
    /// </summary>
    /// <param name="expiresAt">The moment the counted thing expires.</param>
    /// <param name="at">The server's moment the remainder is taken at.</param>
    /// <returns>The remainder in whole milliseconds; 0 when the moment has passed.</returns>
    public static long RemainingMs(DateTimeOffset expiresAt, DateTimeOffset at) =>
        (long)Math.Floor(Math.Max(0, (expiresAt - at).TotalMilliseconds));

    /// <summary>
    /// Source of the drawing function, ready to be inlined ahead of the script of the page that calls it.
    /// </summary>
    public static string Js => CorePageEmbeddedText.Load(typeof(PageCountdown).Assembly, ScriptResourceName);

    /// <summary>
    /// Rules of the countdown, to be put after the token layer of the contour.
    /// </summary>
    public static string Css => CorePageEmbeddedText.Load(typeof(PageCountdown).Assembly, StylesResourceName);
}
