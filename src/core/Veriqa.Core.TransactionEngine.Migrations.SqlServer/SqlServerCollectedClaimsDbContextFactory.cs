// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Veriqa.Core.TransactionEngine.CollectedClaims.EfCore;

namespace Veriqa.Core.TransactionEngine.Migrations.SqlServer;

/// <summary>
/// DbContext factory of the collected claims store for EF Core design-time tools (dotnet ef migrations) —
/// SQL Server. The store's migrations live in this assembly next to the transaction migrations, so the
/// tooling picks the context by name (<c>--context CollectedClaimsDbContext</c>).
/// Reads the connection string from TRANSACTION_DB_CONNECTION_STRING — the shipped host keeps the store in
/// the transaction database; if the variable is not set — uses a placeholder for local tooling (no database
/// connection is required to generate migrations).
/// </summary>
internal sealed class SqlServerCollectedClaimsDbContextFactory
    : IDesignTimeDbContextFactory<CollectedClaimsDbContext>
{
    /// <summary>
    /// Name of the environment variable holding the connection string.
    /// </summary>
    private const string ConnectionStringEnvVariable = "TRANSACTION_DB_CONNECTION_STRING";

    /// <summary>
    /// Default connection string for design-time tooling (placeholder without credentials).
    /// The real string is provided via TRANSACTION_DB_CONNECTION_STRING.
    /// </summary>
    private const string DefaultDesignTimeConnectionString =
        "Server=localhost;Database=veriqa_transactions;Integrated Security=true;TrustServerCertificate=true";

    /// <inheritdoc />
    public CollectedClaimsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvVariable)
            ?? DefaultDesignTimeConnectionString;

        var builder = new DbContextOptionsBuilder<CollectedClaimsDbContext>();

        // Migrations live in this assembly — specify it explicitly. The history table is the one the
        // runtime uses (the store shares its database with the transaction store): named differently, the
        // tooling and the application would each keep their own history and apply the same migration twice.
        builder.UseSqlServer(
            connectionString,
            sqlServer =>
            {
                sqlServer.MigrationsAssembly(typeof(SqlServerCollectedClaimsDbContextFactory).Assembly.GetName().Name!);
                sqlServer.MigrationsHistoryTable(CollectedClaimsMigrationDefaults.MigrationsHistoryTable);
            });

        return new CollectedClaimsDbContext(builder.Options);
    }
}
