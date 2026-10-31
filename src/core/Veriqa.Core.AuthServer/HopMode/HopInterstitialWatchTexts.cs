// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The lines the status line of the launch page shows once a transition into the channel began, ready in
/// the language of the page.
/// </summary>
/// <param name="Launched">
/// Line shown when the transition begins — by the automatic launch or by a press of the button: where the
/// person went and what to do there. Empty — the page has no such line, and the status line stays as it
/// is.
/// </param>
/// <param name="Returned">
/// Line shown in place of <paramref name="Launched"/> once the person is back on the page after the
/// transition — a page reopened after it or a tab that became visible again: what is left to do there. A
/// channel with nothing else to say repeats <paramref name="Launched"/>.
/// </param>
/// <param name="ReturnToWindow">
/// Line shown once the core asks the confirming question on a page the launch page does not leave for:
/// the sign-in window of a sign-in, or the browser the link of the letter of a confirmation was opened in
/// when the answer is bound to that browser — where to go to answer it. It takes the place of every line
/// of the transition, and only the lines of the outcome replace it.
/// </param>
/// <param name="Processing">Line of a confirmed transaction that is still being completed.</param>
/// <param name="Completed">Line of a completed transaction.</param>
/// <param name="Expired">Line of a transaction that ran out of time or is no longer stored.</param>
/// <param name="Declined">Line of a transaction the user declined.</param>
/// <param name="Error">Line of a failure that is not the user's refusal.</param>
internal sealed record HopInterstitialWatchTexts(
    string Launched,
    string Returned,
    string ReturnToWindow,
    string Processing,
    string Completed,
    string Expired,
    string Declined,
    string Error);
