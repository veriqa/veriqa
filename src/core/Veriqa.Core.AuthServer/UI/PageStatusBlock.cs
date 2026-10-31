// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// The reaction of the status block of the pages that follow a transaction to its end: one function,
/// <c>window.veriqaStatusBlock(parts, lines)</c>, inlined ahead of the script of every page that carries the
/// script of the sign-in window and of the interstitial launch page of a hop, writes the status line on
/// each of them. It takes away the heading of the channel and the controls only where the page handed them
/// over — the page of the scanner with a chosen channel and the launch page — so a sign-in and a
/// confirmation, and those two phone pages, react to the same state in the same way (SPEC-025 HOP-193,
/// HOP-186).
/// <para>
/// Norms the script implements. They are named here and not in the file itself, whose comments are
/// inlined into the page and reach the end user's browser (see <see cref="CorePageEmbeddedText"/>):
/// </para>
/// <list type="bullet">
/// <item><description>the heading of the channel leaves with the first of the states the block shows —
/// back to the sign-in window, confirmed and processing, success, refusal, failure, expiry; before them it
/// stays on screen, and the answer of the bot and the lines of the transition do not take it away
/// (SPEC-025 HOP-193);</description></item>
/// <item><description>the same states take away the controls of the page; the focus follows to the status
/// line only on a page whose line can hold it, otherwise it is not moved (SPEC-025 HOP-186);</description></item>
/// <item><description>an ending — success, refusal, failure, expiry — shows the row of the action under the
/// outcome where the page carries one (<see cref="OutcomeCloseAction"/>); the states that are no ending yet —
/// back to the sign-in window, confirmed and processing, the answer awaited in the browser of a link — leave
/// it hidden;</description></item>
/// <item><description>the texts are the server's, one per state, so both pages of one transaction say the
/// same words at the same ending; the function has none of its own. The status line stays the polite live
/// region the page renders: the function changes its text and class, never its role.</description></item>
/// </list>
/// </summary>
internal static class PageStatusBlock
{
    /// <summary>
    /// Resource name of the reaction function (<c>UI/Scripts/veriqa-status-block.js</c>).
    /// </summary>
    private const string ScriptResourceName = "Veriqa.Core.AuthServer.UI.Scripts.veriqa-status-block.js";

    /// <summary>
    /// Source of the reaction function, ready to be inlined ahead of the script of the page that calls it.
    /// </summary>
    public static string Js => CorePageEmbeddedText.Load(typeof(PageStatusBlock).Assembly, ScriptResourceName);
}
