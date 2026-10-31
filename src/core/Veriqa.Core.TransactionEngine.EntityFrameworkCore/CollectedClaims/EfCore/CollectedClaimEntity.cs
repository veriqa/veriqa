// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.CollectedClaims.EfCore;

/// <summary>
/// One record of the collected claims store: a collected value of a claim, or the mark that the user
/// skipped it (SPEC-041 C37, L75). The stored form of <see cref="ClaimCompletionResult"/>.
/// </summary>
internal sealed class CollectedClaimEntity
{
    /// <summary>
    /// Identifier of the key the record belongs to.
    /// </summary>
    public long SubjectId { get; set; }

    /// <summary>
    /// Name of the claim.
    /// </summary>
    public string Claim { get; set; } = string.Empty;

    /// <summary>
    /// Collected value; null — the user skipped the claim.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Whether the value was verified; false for a skip.
    /// </summary>
    public bool Verified { get; set; }

    /// <summary>
    /// How the value was obtained; null — the user skipped the claim. Stored by its numeric value, which is
    /// part of the storage format of the enumeration.
    /// </summary>
    public ClaimCollectionMethod? Method { get; set; }

    /// <summary>
    /// Moment the value was collected or the claim skipped (UTC).
    /// </summary>
    public DateTimeOffset RecordedAt { get; set; }
}
