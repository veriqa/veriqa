// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Veriqa.Core.TransactionEngine.Common;

namespace Veriqa.Core.TransactionEngine.CollectedClaims.EfCore;

/// <summary>
/// Background service that applies the EF Core migrations of the collected claims store at application
/// startup — MigrateAsync with the retry policy shared by every migration service; EnsureCreatedAsync is
/// not used (migration history is mandatory, SPEC-001 §10.2).
/// </summary>
/// <remarks>
/// The migrations live in the per-provider migrations assemblies of the transaction store, next to its
/// own, and keep a history table of their own (<see cref="CollectedClaimsMigrationDefaults.MigrationsHistoryTable"/>).
/// A new migration is generated for this context by name:
/// <code>
/// dotnet ef migrations add &lt;MigrationName&gt; --context CollectedClaimsDbContext
///   --project src/core/Veriqa.Core.TransactionEngine.Migrations.PostgreSql
/// </code>
/// </remarks>
internal sealed class CollectedClaimsMigrationService : IHostedService
{
    /// <summary>
    /// Operation name for the migration runner logs (used as a possessive phrase).
    /// </summary>
    private const string OperationName = "the collected claims store";

    /// <summary>
    /// DbContext factory for creating the context at startup.
    /// </summary>
    private readonly IDbContextFactory<CollectedClaimsDbContext> _contextFactory;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<CollectedClaimsMigrationService> _logger;

    /// <summary>
    /// Creates an instance of the migration service.
    /// </summary>
    /// <param name="contextFactory">DbContext factory.</param>
    /// <param name="logger">Logger.</param>
    public CollectedClaimsMigrationService(
        IDbContextFactory<CollectedClaimsDbContext> contextFactory,
        ILogger<CollectedClaimsMigrationService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing the collected claims store (applying EF Core migrations)...");

        await MigrationRetryRunner.RunWithRetryAsync(
            ApplyMigrationsAsync,
            _logger,
            OperationName,
            cancellationToken);

        _logger.LogInformation("Collected claims store initialization completed successfully");
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Creates a DbContext and applies migrations from the host-configured migrations assembly.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task ApplyMigrationsAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        await context.Database.MigrateAsync(cancellationToken);
    }
}
