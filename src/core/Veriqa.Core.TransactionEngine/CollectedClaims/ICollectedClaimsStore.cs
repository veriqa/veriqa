// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.CollectedClaims;

/// <summary>
/// Store of the claims Veriqa collected itself (SPEC-041 C37): what a user entered or skipped once is not
/// asked again at the next sign-in. Optional — without a registered implementation nothing is kept (R17);
/// an implementation is plugged in with <c>TransactionEngineBuilder.UseCollectedClaimsStore</c>.
/// </summary>
/// <remarks>
/// <para>
/// A record is keyed by (tenant, core subject): the tenant of the client the sign-in came through and the
/// subject the core derives from the channel identity (<c>channel_type:channel_user_id</c>, CA-022) — not the
/// subject a claims mapper hands to the application. A null tenant is the implicit tenant of a self-hosted
/// installation; an implementation spells it with <see cref="TenantKey.Segment"/>, so both spellings
/// of "no tenant" land on the same key.
/// </para>
/// <para>
/// The records of a key are <see cref="ClaimCompletionResult"/> values: <see cref="ClaimCompletionResult.Value"/>
/// null is the mark that the user skipped the claim (SPEC-041 L75), and
/// <see cref="ClaimCompletionResult.RecordedAt"/> is the moment the value was collected or the claim skipped.
/// At most one record per claim: a later record of the same claim replaces the earlier one, whether a value
/// or a skip.
/// </para>
/// <para>
/// The engine asks for the port inside a scope of its own for each operation, so an implementation may be
/// registered with any lifetime.
/// </para>
/// </remarks>
public interface ICollectedClaimsStore
{
    /// <summary>
    /// Reads the records of a key for a sign-in. When the key exists, its last sign-in time is set to
    /// <paramref name="now"/> — the moment automatic removal counts from (SPEC-041 C84).
    /// </summary>
    /// <param name="tenantId">Tenant of the key; null — the implicit tenant.</param>
    /// <param name="subject">Core subject of the key.</param>
    /// <param name="now">Current moment (UTC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Records of the key; empty when the key does not exist.</returns>
    Task<IReadOnlyList<ClaimCompletionResult>> ReadForSignInAsync(
        string? tenantId,
        string subject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves records of a key: per claim, a record replaces the one stored before it, a value or a skip
    /// alike. Creates the key when it does not exist; its last sign-in time becomes <paramref name="now"/>.
    /// Repeating the call never creates a second key.
    /// </summary>
    /// <param name="tenantId">Tenant of the key; null — the implicit tenant.</param>
    /// <param name="subject">Core subject of the key.</param>
    /// <param name="records">Records to save; a value carries its method, a skip carries neither.</param>
    /// <param name="now">Current moment (UTC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the completion of the operation.</returns>
    /// <exception cref="ArgumentException">A record names no claim or mixes a value with a skip.</exception>
    Task SaveAsync(
        string? tenantId,
        string subject,
        IReadOnlyList<ClaimCompletionResult> records,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the subject an application received for a key (SPEC-041 C37) — whether the key holds records
    /// of claims or not, so deletion by the subject the application knows finds the key. A missing key is
    /// created without records and with the last sign-in time <paramref name="now"/> — otherwise automatic
    /// removal would never reach it; an existing key keeps its last sign-in time. Repeating the call never
    /// creates a second key or a second entry.
    /// </summary>
    /// <param name="tenantId">Tenant of the key; null — the implicit tenant.</param>
    /// <param name="subject">Core subject of the key.</param>
    /// <param name="applicationId">Identifier of the application (client_id) the subject was issued to.</param>
    /// <param name="issuedSubject">Subject the application received.</param>
    /// <param name="now">Current moment (UTC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the completion of the operation.</returns>
    Task SaveIssuedSubjectAsync(
        string? tenantId,
        string subject,
        string applicationId,
        string issuedSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the core subjects behind a subject an application received. An issued subject is not unique:
    /// several core subjects may have been issued the same one (linked accounts, SPEC-041 R28).
    /// </summary>
    /// <param name="tenantId">Tenant of the keys; null — the implicit tenant.</param>
    /// <param name="applicationId">Identifier of the application (client_id).</param>
    /// <param name="issuedSubject">Subject the application received.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Core subjects; empty when the application was never issued that subject.</returns>
    Task<IReadOnlyList<string>> FindSubjectsAsync(
        string? tenantId,
        string applicationId,
        string issuedSubject,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a key with all its records and issued subjects (SPEC-041 R29).
    /// </summary>
    /// <remarks>
    /// With <paramref name="unusedBefore"/> set (automatic removal, SPEC-041 C84) the key is deleted only when
    /// its last sign-in time is earlier than that moment, and the check and the deletion are one atomic
    /// operation of the store: a sign-in between the selection of unused keys and this call keeps the key.
    /// </remarks>
    /// <param name="tenantId">Tenant of the key; null — the implicit tenant.</param>
    /// <param name="subject">Core subject of the key.</param>
    /// <param name="unusedBefore">
    /// Moment the last sign-in of the key has to precede for the deletion; null — delete unconditionally.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// True when the key was deleted; false when there was no such key or, with
    /// <paramref name="unusedBefore"/> set, the key was signed in with since.
    /// </returns>
    Task<bool> DeleteAsync(
        string? tenantId,
        string subject,
        DateTimeOffset? unusedBefore = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the tenants that have keys — for automatic removal, which reads its period per tenant
    /// (SPEC-041 C84).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tenants with keys; the implicit tenant (<see cref="TenantKey.Default"/>) comes back as null.</returns>
    Task<IReadOnlyList<string?>> ListTenantsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the core subjects of a tenant not signed in with since a moment (SPEC-041 C84).
    /// </summary>
    /// <param name="tenantId">Tenant of the keys; null — the implicit tenant.</param>
    /// <param name="before">Moment the last sign-in of a listed key precedes.</param>
    /// <param name="limit">Maximum number of subjects returned; positive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>At most <paramref name="limit"/> core subjects, the longest unused first.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is not positive.</exception>
    Task<IReadOnlyList<string>> ListUnusedSubjectsAsync(
        string? tenantId,
        DateTimeOffset before,
        int limit,
        CancellationToken cancellationToken = default);
}
