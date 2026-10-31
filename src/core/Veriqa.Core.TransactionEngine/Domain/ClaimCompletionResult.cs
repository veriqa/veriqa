// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// Outcome of the completion for one missing claim (SPEC-041 C90): the collected value, or the mark
/// that the user skipped it.
/// </summary>
/// <remarks>
/// A skip carries no value, no method and no verification; a collected value carries both a value and
/// a method. The stores refuse a result that mixes the two with an <see cref="ArgumentException"/>.
/// </remarks>
/// <param name="Claim">Name of the claim, as the list of missing claims spells it.</param>
/// <param name="Value">Collected value; null — the claim was skipped.</param>
/// <param name="Verified">
/// Whether the value was verified (the source of the matching <c>*_verified</c> claim); false for a skip.
/// </param>
/// <param name="Method">How the value was obtained; null — the claim was skipped.</param>
/// <param name="RecordedAt">Moment the value was collected or the claim skipped (UTC; SPEC-041 C38 <c>updated_at</c>).</param>
public sealed record ClaimCompletionResult(
    string Claim,
    string? Value,
    bool Verified,
    ClaimCollectionMethod? Method,
    DateTimeOffset RecordedAt);
