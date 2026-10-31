// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Endpoints;
using Veriqa.Core.AuthServer.UI;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// What the launch page follows the outcome of its transaction by, once a transition into the channel began
/// (SPEC-025 HOP-186): where the status of the transaction is asked, where the question of the core about
/// it is asked and the lines the page ends on.
/// </summary>
/// <param name="StatusPath">
/// The address of the polled status of the transaction (SPEC-007 §5.4), path base included. It names the
/// transaction by its session identifier — the identifier the page of the channel buttons of the same
/// transaction is handed as well.
/// </param>
/// <param name="QuestionPath">
/// The address of the page of the core that asks the confirming question of the transaction
/// (SPEC-039 E29), path base included — the address the page of the channel buttons of the same
/// transaction leaves for; null — no page of the core asks about this transaction.
/// </param>
/// <param name="ConfirmedWatchLimit">
/// How long the page goes on asking after it saw the transaction confirmed, by the effective settings of
/// the engine (<see cref="TransactionOutcomeWatchScript.ConfirmedWatchLimit"/>).
/// </param>
/// <param name="Lines">The lines the page ends on, worded in the content language of the page.</param>
/// <param name="CloseApplicationName">
/// The name of the application the action under the outcome names — "Return to {app}"; null — "Close"
/// (<see cref="OutcomeCloseAction"/>).
/// </param>
internal sealed record HopInterstitialOutcomeWatch(
    string StatusPath,
    string? QuestionPath,
    TimeSpan ConfirmedWatchLimit,
    TransactionOutcomeLines Lines,
    string? CloseApplicationName);
