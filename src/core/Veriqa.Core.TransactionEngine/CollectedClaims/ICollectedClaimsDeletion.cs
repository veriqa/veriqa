// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.CollectedClaims;

/// <summary>
/// Deletion of the collected claims of a subject by a call of the host application (SPEC-041 C83) — the
/// operator of personal data erasing what Veriqa keeps about a person (R29). Registered together with the
/// collected claims store (<c>TransactionEngineBuilder.UseCollectedClaimsStore</c>); without a store the
/// service is not registered at all.
/// </summary>
/// <remarks>
/// The key is deleted with all its records and the subjects issued to applications, and each deleted core
/// subject gets a <c>CollectedClaimsDeleted</c> record in the audit journal when the journal is wired, with the
/// system as the actor and <c>host_call</c> as the basis. A failure to write that record is logged and does
/// not undo the deletion; a failure of the store propagates, and no record is written for it.
/// </remarks>
public interface ICollectedClaimsDeletion
{
    /// <summary>
    /// Deletes the collected claims of a subject. Idempotent: a subject with nothing stored is a success.
    /// </summary>
    /// <param name="tenantId">Tenant the records belong to — the tenant of the client the sign-ins came
    /// through; null — the implicit tenant of a self-hosted installation.</param>
    /// <param name="applicationId">Application (<c>client_id</c>) the <paramref name="subject"/> was issued to,
    /// when the application received a subject other than the core one (a claims mapper changing <c>sub</c>);
    /// null — <paramref name="subject"/> is the core subject. A subject not found among those issued to the
    /// application is taken as the core one.</param>
    /// <param name="subject">Subject in the form the application received it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the completion of the deletion.</returns>
    Task DeleteBySubjectAsync(
        string? tenantId,
        string? applicationId,
        string subject,
        CancellationToken cancellationToken = default);
}
