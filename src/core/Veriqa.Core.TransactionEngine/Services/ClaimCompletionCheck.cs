// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// Answer of the single check of the end of the claims completion
/// (<see cref="ClaimCompletionPlanner.IsCompleteAsync"/>).
/// </summary>
/// <remarks>
/// The answers that are not <see cref="Complete"/> differ in whose write the transaction carries after
/// the check: a step that vanished stored values taken off (SPEC-041 E86) moved the completion to begins
/// only for the writer whose own write moved it there, otherwise two writers that check at once would both
/// begin it.
/// </remarks>
internal enum ClaimCompletionCheck
{
    /// <summary>
    /// Nothing is missing any more.
    /// </summary>
    Complete,

    /// <summary>
    /// A missing claim still waits for its outcome, and the check did not move the current step — it wrote
    /// nothing, or its take-off left the step where it was — or it failed the transaction as unobtainable
    /// itself.
    /// </summary>
    Missing,

    /// <summary>
    /// The check took vanished stored values off the transaction with its own write, that write moved the
    /// current step, and a missing claim waits for its outcome.
    /// </summary>
    ValuesTakenOff,

    /// <summary>
    /// The write of the check lost the token: another writer moved the transaction first.
    /// </summary>
    Conflict
}
