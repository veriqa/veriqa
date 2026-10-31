// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.CollectedClaims;

/// <summary>
/// One deletion of collected claims as its entry states it (SPEC-041 C82, C83, C84): whose records, in what
/// form the subject came, and how the deletion is attributed in the audit journal (L85).
/// </summary>
internal sealed record CollectedClaimsDeletionRequest
{
    /// <summary>
    /// Tenant of the key; null — the implicit tenant.
    /// </summary>
    public required string? TenantId { get; init; }

    /// <summary>
    /// Application the <see cref="Subject"/> was issued to; null — the subject is the core one.
    /// </summary>
    public string? ApplicationId { get; init; }

    /// <summary>
    /// Subject in the form the application received it, or the core subject.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// Actor of the audit record: the calling client of the endpoint, or the system.
    /// </summary>
    public required string Actor { get; init; }

    /// <summary>
    /// Client the audit record is attributed to; null — none.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Basis of the deletion — a token of <c>AuditDeletionBases</c>.
    /// </summary>
    public required string DeletionBasis { get; init; }

    /// <summary>
    /// When set (the automatic removal), a key is deleted only if its last sign-in is earlier than this
    /// moment; a key used since is kept and gets no audit record.
    /// </summary>
    public DateTimeOffset? UnusedBefore { get; init; }
}
