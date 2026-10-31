// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Veriqa.Core.Contracts.Audit;
using Veriqa.Core.TransactionEngine.Identity;

namespace Veriqa.Core.TransactionEngine.CollectedClaims;

/// <summary>
/// The one deletion of collected claims behind its three entries (SPEC-041 R29): the server endpoint (C82),
/// the call of the host (C83, <see cref="ICollectedClaimsDeletion"/>) and the automatic removal (C84). Every
/// deleted core subject is written to the audit journal (L85).
/// </summary>
internal sealed class CollectedClaimsDeletion : ICollectedClaimsDeletion
{
    /// <summary>
    /// Factory of the scope the store is taken from for each deletion.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Clock of the audit records.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<CollectedClaimsDeletion> _logger;

    /// <summary>
    /// Creates the deletion.
    /// </summary>
    /// <param name="scopeFactory">Factory of the scope the store is taken from.</param>
    /// <param name="timeProvider">Clock of the audit records.</param>
    /// <param name="logger">Logger.</param>
    public CollectedClaimsDeletion(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<CollectedClaimsDeletion> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task DeleteBySubjectAsync(
        string? tenantId,
        string? applicationId,
        string subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        await DeleteAsync(
            new CollectedClaimsDeletionRequest
            {
                TenantId = tenantId,
                ApplicationId = applicationId,
                Subject = subject,
                Actor = AuditActors.System,
                ClientId = applicationId,
                DeletionBasis = AuditDeletionBases.HostCall
            },
            cancellationToken);
    }

    /// <summary>
    /// Deletes the keys a request addresses and writes an audit record for each of them.
    /// </summary>
    /// <param name="request">The deletion as its entry states it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of keys the store actually deleted.</returns>
    internal async Task<int> DeleteAsync(
        CollectedClaimsDeletionRequest request,
        CancellationToken cancellationToken)
    {
        // A subject issued to an application is translated into the core subjects it was issued for — one
        // issued subject may stand for several of them (linked accounts, R28), and all are deleted. Each core
        // subject is deleted and recorded before the next one, so a failure of the store halfway leaves every
        // deletion already made in the journal. The failure itself propagates: no record claims a deletion
        // that did not happen.
        ArgumentNullException.ThrowIfNull(request);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ICollectedClaimsStore>();

        var coreSubjects = await ResolveCoreSubjectsAsync(store, request, cancellationToken);

        var deletedCount = 0;

        foreach (var coreSubject in coreSubjects)
        {
            var deleted = await store.DeleteAsync(
                request.TenantId,
                coreSubject,
                request.UnusedBefore,
                cancellationToken);

            if (deleted)
            {
                deletedCount++;
            }
            else if (request.UnusedBefore is not null)
            {
                // The automatic removal found the key signed in between the selection and the deletion: the
                // key stays, and nothing was deleted to be recorded.
                continue;
            }

            // A deletion by request is recorded as a success whether or not anything was stored: the request
            // was carried out.
            await AppendAuditRecordAsync(scope.ServiceProvider, request, coreSubject);
        }

        return deletedCount;
    }

    /// <summary>
    /// The core subjects a request addresses.
    /// </summary>
    /// <param name="store">Collected claims store.</param>
    /// <param name="request">The deletion as its entry states it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The core subjects; the requested subject itself when it names no issued one.</returns>
    private static async Task<IReadOnlyList<string>> ResolveCoreSubjectsAsync(
        ICollectedClaimsStore store,
        CollectedClaimsDeletionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ApplicationId))
        {
            return [request.Subject];
        }

        var found = await store.FindSubjectsAsync(
            request.TenantId,
            request.ApplicationId,
            request.Subject,
            cancellationToken);

        return found.Count > 0 ? found : [request.Subject];
    }

    /// <summary>
    /// Writes the audit record of one deleted core subject, when the audit journal is wired.
    /// </summary>
    /// <param name="services">Provider of the scope of the deletion.</param>
    /// <param name="request">The deletion as its entry states it.</param>
    /// <param name="coreSubject">The deleted core subject.</param>
    /// <returns>Task representing the completion of the write.</returns>
    private async Task AppendAuditRecordAsync(
        IServiceProvider services,
        CollectedClaimsDeletionRequest request,
        string coreSubject)
    {
        var auditSink = services.GetService<IAuditSink>();

        if (auditSink is null)
        {
            return;
        }

        var maskedSubject = IdentifierMask.Mask(coreSubject);

        var record = new AuditRecord
        {
            Timestamp = _timeProvider.GetUtcNow(),
            Actor = request.Actor,
            Action = AuditActionCodes.CollectedClaimsDeleted,
            Target = maskedSubject,
            Result = new AuditResult { Outcome = AuditOutcome.Success },
            Metadata = new AuditMetadata
            {
                TenantId = request.TenantId,
                ClientId = request.ClientId,
                DeletionBasis = request.DeletionBasis
            }
        };

        try
        {
            // The deletion has already happened, so its record is written even when the caller has gone away
            // in the meantime: the token of the caller is deliberately not passed.
            await auditSink.AppendAsync(record, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "The collected claims of subject {MaskedSubject} were deleted (basis: {DeletionBasis}), "
                + "but the audit record of the deletion could not be written",
                maskedSubject,
                request.DeletionBasis);
        }
    }
}
