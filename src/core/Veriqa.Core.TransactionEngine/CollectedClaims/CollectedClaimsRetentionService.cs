// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts.Audit;
using Veriqa.Core.TransactionEngine.Configuration;

namespace Veriqa.Core.TransactionEngine.CollectedClaims;

/// <summary>
/// Automatic removal of the collected claims (SPEC-041 C84): at host start and then once an hour, the keys of
/// every tenant whose retention is on and who have not signed in for that many days are deleted, each with its
/// audit record (L85).
/// </summary>
/// <remarks>
/// The retention is resolved per tenant (<see cref="ClaimCompletionConfigKeys.CollectedClaimsRetentionDays"/>),
/// so a tenant can have the removal on while the core keeps it off. A key is deleted only if its last sign-in is
/// still earlier than the threshold at the moment of the deletion: a sign-in between the selection and the
/// deletion keeps the key, and it gets no audit record. A failed tenant is logged and the pass goes on to the
/// next one; a failed pass is logged and the next tick runs anew.
/// </remarks>
internal sealed class CollectedClaimsRetentionService : BackgroundService
{
    /// <summary>
    /// Number of keys selected for deletion at once.
    /// </summary>
    private const int BatchSize = 100;

    /// <summary>
    /// Interval between two passes.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Factory of the scope the store is taken from for a pass.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// The one deletion of collected claims.
    /// </summary>
    private readonly CollectedClaimsDeletion _deletion;

    /// <summary>
    /// Canonical resolver of the effective setting value.
    /// </summary>
    private readonly IConfigurationResolver _configurationResolver;

    /// <summary>
    /// Clock the threshold and the ticks are computed from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<CollectedClaimsRetentionService> _logger;

    /// <summary>
    /// Creates the removal.
    /// </summary>
    /// <param name="scopeFactory">Factory of the scope the store is taken from.</param>
    /// <param name="deletion">The one deletion of collected claims.</param>
    /// <param name="configurationResolver">Configuration resolver.</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public CollectedClaimsRetentionService(
        IServiceScopeFactory scopeFactory,
        CollectedClaimsDeletion deletion,
        IConfigurationResolver configurationResolver,
        TimeProvider timeProvider,
        ILogger<CollectedClaimsRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _deletion = deletion;
        _configurationResolver = configurationResolver;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, _timeProvider);

        try
        {
            // The first pass runs at startup, before the timer: a host restarted more often than once an hour
            // would otherwise never remove anything.
            await SweepAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Runs one pass over every tenant that has keys in the store.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token of the host.</param>
    /// <returns>Task representing the completion of the pass.</returns>
    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();

            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ICollectedClaimsStore>();

            var tenants = await store.ListTenantsAsync(stoppingToken);

            var removed = 0;

            foreach (var tenantId in tenants)
            {
                // A tenant that fails (its retention unresolvable, a threshold out of range, a store error on
                // its keys) is logged and skipped: the tenants after it are still swept.
                try
                {
                    removed += await SweepTenantAsync(store, tenantId, now, stoppingToken);
                }
                catch (Exception exception)
                    when (!(exception is OperationCanceledException && stoppingToken.IsCancellationRequested))
                {
                    _logger.LogWarning(
                        exception,
                        "Collected claims retention failed for tenant {TenantId}; the other tenants are still swept and this one is retried on the next tick",
                        tenantId);
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation(
                    "Collected claims retention removed {RemovedCount} subject key(s) unused for the retention period",
                    removed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Collected claims retention pass failed; it will be retried on the next tick");
        }
    }

    /// <summary>
    /// Removes the unused keys of one tenant, when its retention is on.
    /// </summary>
    /// <param name="store">Collected claims store.</param>
    /// <param name="tenantId">Tenant; null — the implicit tenant.</param>
    /// <param name="now">Moment of the pass.</param>
    /// <param name="stoppingToken">Cancellation token of the host.</param>
    /// <returns>Number of keys removed.</returns>
    private async Task<int> SweepTenantAsync(
        ICollectedClaimsStore store,
        string? tenantId,
        DateTimeOffset now,
        CancellationToken stoppingToken)
    {
        var retentionDays = (await _configurationResolver.ResolveAsync(
            ClaimCompletionConfigKeys.CollectedClaimsRetentionDays,
            ResolutionContext.ForTenant(tenantId),
            ConfigDimensionValues.None,
            stoppingToken)).Value;

        if (retentionDays <= 0)
        {
            return 0;
        }

        // One threshold for the selection and the conditional deletion alike: the key selected as unused is
        // deleted only if it is still unused against the same moment.
        var unusedBefore = now.AddDays(-retentionDays);

        var removed = 0;
        IReadOnlyList<string> batch;

        do
        {
            batch = await store.ListUnusedSubjectsAsync(tenantId, unusedBefore, BatchSize, stoppingToken);

            var removedInBatch = 0;

            foreach (var subject in batch)
            {
                removedInBatch += await _deletion.DeleteAsync(
                    new CollectedClaimsDeletionRequest
                    {
                        TenantId = tenantId,
                        Subject = subject,
                        Actor = AuditActors.System,
                        DeletionBasis = AuditDeletionBases.Retention,
                        UnusedBefore = unusedBefore
                    },
                    stoppingToken);
            }

            removed += removedInBatch;

            // A short batch is the last one. A full batch of which nothing was removed ends the pass as well:
            // selecting again could only return keys the store keeps refusing to delete, and the next tick
            // gets to them anyway.
            if (removedInBatch == 0)
            {
                break;
            }
        }
        while (batch.Count == BatchSize);

        return removed;
    }
}
