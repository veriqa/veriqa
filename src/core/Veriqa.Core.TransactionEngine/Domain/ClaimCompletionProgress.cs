// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// The parts of <see cref="ClaimCompletionState"/> written only by the conditional writes of a store —
/// the unit a store that keeps them apart from the transaction record reads, writes and lays back over
/// the record.
/// </summary>
/// <param name="Surface">Surface of the web step (see <see cref="ClaimCompletionState.Surface"/>).</param>
/// <param name="Results">Outcomes recorded so far (see <see cref="ClaimCompletionState.Results"/>).</param>
/// <param name="FormTokenHash">
/// Fingerprint of the form token (see <see cref="ClaimCompletionState.FormTokenHash"/>).
/// </param>
internal readonly record struct ClaimCompletionProgress(
    WebStepSurface? Surface,
    IReadOnlyList<ClaimCompletionResult> Results,
    string? FormTokenHash)
{
    /// <summary>
    /// No surface, no outcome and no form token — the progress of a completion that has just begun.
    /// </summary>
    public static ClaimCompletionProgress None { get; } = new(null, Array.Empty<ClaimCompletionResult>(), null);
}
