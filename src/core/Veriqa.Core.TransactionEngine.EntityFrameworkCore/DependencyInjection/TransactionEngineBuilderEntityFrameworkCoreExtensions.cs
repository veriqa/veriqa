// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Veriqa.Core.TransactionEngine.CollectedClaims.EfCore;
using Veriqa.Core.TransactionEngine.Store;
using Veriqa.Core.TransactionEngine.Store.EfCore;

namespace Veriqa.Core.TransactionEngine.DependencyInjection;

/// <summary>EF Core store registration extensions for <see cref="TransactionEngineBuilder"/>.</summary>
/// <remarks>
/// The namespace is the one the builder itself lives in, so a store in a package of its own costs the
/// caller a package reference and no additional <c>using</c>: <c>engine.UseEfCoreStore(...)</c> resolves
/// wherever the builder's namespace is already imported.
/// </remarks>
public static class TransactionEngineBuilderEntityFrameworkCoreExtensions
{
    /// <summary>
    /// Uses the EF Core store. The provider choice, connection string and migrations assembly
    /// belong to the caller (host-owned, SPEC-001 §10.2):
    /// <code>
    /// engine.UseEfCoreStore(ef => ef.UseNpgsql(connectionString,
    ///     npgsql => npgsql.MigrationsAssembly("Veriqa.Core.TransactionEngine.Migrations.PostgreSql")));
    /// </code>
    /// Registers the DbContext factory, EfCoreTransactionStore and the migration service (MigrateAsync).
    /// </summary>
    /// <param name="engine">Transaction Engine builder.</param>
    /// <param name="configure">Action configuring EF Core (for example UseNpgsql).</param>
    /// <returns>Builder for chaining.</returns>
    public static TransactionEngineBuilder UseEfCoreStore(
        this TransactionEngineBuilder engine,
        Action<DbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(configure);

        // Register the DbContext factory; the host configures the provider via the passed delegate
        engine.Services.AddDbContextFactory<TransactionDbContext>(configure);

        // Register the store as a Singleton (safe because it uses IDbContextFactory)
        engine.Services.AddSingleton<ITransactionStore, EfCoreTransactionStore>();

        // Register the migration service — applies MigrateAsync at application startup
        engine.Services.AddHostedService<TransactionMigrationService>();

        engine.StoreRegistered = true;

        return engine;
    }

    /// <summary>
    /// Uses the EF Core collected claims store (SPEC-041 C37). The provider choice, connection string,
    /// migrations assembly and migration history table belong to the caller; the migrations ship in the
    /// transaction store's migrations assemblies, and the history table is
    /// <see cref="CollectedClaimsMigrationDefaults.MigrationsHistoryTable"/> — the store may share a database
    /// with the transaction store, and a shared history would mix the two sets of migrations:
    /// <code>
    /// engine.UseEfCoreCollectedClaimsStore(ef => ef.UseNpgsql(connectionString, npgsql => npgsql
    ///     .MigrationsAssembly("Veriqa.Core.TransactionEngine.Migrations.PostgreSql")
    ///     .MigrationsHistoryTable(CollectedClaimsMigrationDefaults.MigrationsHistoryTable)));
    /// </code>
    /// Registers the DbContext factory and the migration service (MigrateAsync); the store itself goes
    /// through <see cref="TransactionEngineBuilder.UseCollectedClaimsStore{TStore}"/>, the registration every
    /// implementation shares.
    /// </summary>
    /// <param name="engine">Transaction Engine builder.</param>
    /// <param name="configure">Action configuring EF Core (for example UseNpgsql).</param>
    /// <returns>Builder for chaining.</returns>
    public static TransactionEngineBuilder UseEfCoreCollectedClaimsStore(
        this TransactionEngineBuilder engine,
        Action<DbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(configure);

        engine.Services.AddDbContextFactory<CollectedClaimsDbContext>(configure);
        engine.Services.AddHostedService<CollectedClaimsMigrationService>();

        return engine.UseCollectedClaimsStore<EfCoreCollectedClaimsStore>();
    }
}
