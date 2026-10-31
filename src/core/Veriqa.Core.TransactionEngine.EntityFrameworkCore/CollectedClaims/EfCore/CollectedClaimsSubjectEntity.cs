// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.CollectedClaims.EfCore;

/// <summary>
/// Key of the collected claims store (SPEC-041 C37): one (tenant, core subject) pair, the moment of its last
/// sign-in, its records and the subjects its applications received. Deleting it deletes both of those.
/// </summary>
internal sealed class CollectedClaimsSubjectEntity
{
    /// <summary>
    /// Surrogate identifier the records and the issued subjects refer to.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Tenant as <c>TenantKey.Segment</c> spells it — never null, so the implicit tenant is one value of the
    /// unique key rather than a null a unique index would not compare.
    /// </summary>
    public string TenantSegment { get; set; } = string.Empty;

    /// <summary>
    /// Core subject (<c>channel_type:channel_user_id</c>, CA-022).
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Last sign-in that read the records of the key, or the moment the key was created (UTC); the moment
    /// automatic removal counts from (SPEC-041 C84).
    /// </summary>
    public DateTimeOffset LastSignInAt { get; set; }

    /// <summary>
    /// Records of the key, at most one per claim.
    /// </summary>
    public ICollection<CollectedClaimEntity> Claims { get; set; } = new List<CollectedClaimEntity>();

    /// <summary>
    /// Subjects the applications received for this key.
    /// </summary>
    public ICollection<CollectedClaimsIssuedSubjectEntity> IssuedSubjects { get; set; } =
        new List<CollectedClaimsIssuedSubjectEntity>();
}
