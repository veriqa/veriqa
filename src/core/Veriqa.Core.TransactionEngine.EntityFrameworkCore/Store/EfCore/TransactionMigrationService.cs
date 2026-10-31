// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Veriqa.Core.TransactionEngine.Common;

namespace Veriqa.Core.TransactionEngine.Store.EfCore;

/// <summary>
/// Background service that applies EF Core migrations at application startup.
/// A single path for all relational providers — MigrateAsync with exponential backoff;
/// EnsureCreatedAsync is not used (migration history is mandatory, SPEC-001 §10.2).
/// </summary>
/// <remarks>
/// Migrations live in separate per-provider assemblies
/// (for example Veriqa.Core.TransactionEngine.Migrations.PostgreSql).
/// The host specifies the migrations assembly when configuring the provider:
/// <code>
/// engine.UseEfCoreStore(ef => ef.UseNpgsql(cs,
///     npgsql => npgsql.MigrationsAssembly("Veriqa.Core.TransactionEngine.Migrations.PostgreSql")));
/// </code>
/// A new migration is generated in the directory of the corresponding migrations assembly; the assembly
/// also carries the migrations of the collected claims store, so the context is named:
/// <code>
/// dotnet ef migrations add &lt;MigrationName&gt; --context TransactionDbContext
///   --project src/core/Veriqa.Core.TransactionEngine.Migrations.PostgreSql
/// </code>
/// </remarks>
internal sealed class TransactionMigrationService : IHostedService
{
    /// <summary>
    /// Operation name for the migration runner logs (used as a possessive phrase).
    /// </summary>
    private const string OperationName = "the transaction store";

    /// <summary>
    /// DbContext factory for creating the context at startup.
    /// </summary>
    private readonly IDbContextFactory<TransactionDbContext> _contextFactory;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<TransactionMigrationService> _logger;

    /// <summary>
    /// Creates an instance of the migration service.
    /// </summary>
    /// <param name="contextFactory">DbContext factory.</param>
    /// <param name="logger">Logger.</param>
    public TransactionMigrationService(
        IDbContextFactory<TransactionDbContext> contextFactory,
        ILogger<TransactionMigrationService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Apply migrations with retry: the database may not be ready yet when the container starts.
        // The retry policy is shared by all migration hosted services (MigrationRetryRunner).
        _logger.LogInformation("Initializing the transaction store (applying EF Core migrations)...");

        await MigrationRetryRunner.RunWithRetryAsync(
            ApplyMigrationsAsync,
            _logger,
            OperationName,
            cancellationToken);

        _logger.LogInformation("Transaction store initialization completed successfully");
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
