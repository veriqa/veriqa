// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Contracts;

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// Claims completion fields of a transaction (SPEC-041 C90): the mark that the user confirmed the
/// sign-in while claims were missing, the list of those claims, the surface of the web step, the
/// outcome for each missing claim and the fingerprint of the form token (N78).
/// </summary>
/// <remarks>
/// The parts are written in two different ways, and that split is what keeps the writers from
/// overwriting each other. <see cref="ConfirmedAt"/> and <see cref="Items"/> are written by an
/// ordinary <c>ITransactionStore.UpdateAsync</c> — the one that also stores the channel snapshot.
/// <see cref="Surface"/>, <see cref="Results"/> and <see cref="FormTokenHash"/> are written only by the
/// conditional writes of the store (<c>TrySetWebStepSurfaceAsync</c>, <c>TryRecordClaimResultAsync</c>,
/// <c>TrySetClaimFormTokenHashAsync</c>), and <c>UpdateAsync</c> neither writes nor erases them: a write
/// off a stale copy of the transaction cannot lose an outcome recorded in between.
/// </remarks>
public sealed record ClaimCompletionState
{
    /// <summary>
    /// Moment the user confirmed the sign-in (UTC).
    /// </summary>
    public required DateTimeOffset ConfirmedAt { get; init; }

    /// <summary>
    /// Claims missing at that moment, the ones of the phone step first. Never empty: a confirmation
    /// with nothing missing sets no mark.
    /// </summary>
    public required IReadOnlyList<ClaimCompletionItem> Items { get; init; }

    /// <summary>
    /// Surface of the web step; null — the web step has not begun.
    /// </summary>
    public WebStepSurface? Surface { get; init; }

    /// <summary>
    /// Outcome for each missing claim collected or skipped so far — at most one per claim.
    /// </summary>
    public IReadOnlyList<ClaimCompletionResult> Results { get; init; } = [];

    /// <summary>
    /// Fingerprint of the form token that gives the right to act on the form of the web step on the page
    /// of the other device (SPEC-041 N78); null — no token was issued. The token itself is never stored:
    /// whoever holds it proves it against this value.
    /// </summary>
    public string? FormTokenHash { get; init; }

    /// <summary>
    /// The missing claims no outcome is recorded for yet, in the order of <see cref="Items"/> — the
    /// single reading of "what the completion still waits for".
    /// </summary>
    /// <returns>Open elements of <see cref="Items"/>.</returns>
    public IEnumerable<ClaimCompletionItem> OpenItems()
        => Items.Where(item => !Results.Any(result => VeriqaClaimTypes.NameComparer.Equals(result.Claim, item.Claim)));
}
