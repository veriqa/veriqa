// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.Contracts.Audit;

/// <summary>
/// Closed set of the bases of a deletion of collected claims (SPEC-041 L85), written to
/// <see cref="AuditMetadata.DeletionBasis"/> of a <see cref="AuditActionCodes.CollectedClaimsDeleted"/> record.
/// </summary>
public static class AuditDeletionBases
{
    /// <summary>
    /// Deleted by the server endpoint at the request of a client holding the right to delete (SPEC-041 C82).
    /// </summary>
    public const string Endpoint = "endpoint";

    /// <summary>
    /// Deleted by a call of the host application (SPEC-041 C83).
    /// </summary>
    public const string HostCall = "host_call";

    /// <summary>
    /// Deleted by the automatic removal of the keys without a sign-in for the retention period (SPEC-041 C84).
    /// </summary>
    public const string Retention = "retention";
}
