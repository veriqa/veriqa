// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.CollectedClaims.EfCore;

/// <summary>
/// Subject an application received for a key when a claims mapper handed it one other than the core subject
/// (SPEC-041 C37) — what deletion by the subject the application knows looks the key up by.
/// </summary>
internal sealed class CollectedClaimsIssuedSubjectEntity
{
    /// <summary>
    /// Surrogate identifier.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Identifier of the key the entry belongs to.
    /// </summary>
    public long SubjectId { get; set; }

    /// <summary>
    /// Identifier of the application (client_id).
    /// </summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>
    /// Subject the application received.
    /// </summary>
    public string IssuedSubject { get; set; } = string.Empty;
}
