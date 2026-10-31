// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Veriqa.Core.AuditTrail.Store.EfCore;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.TransactionEngine.CollectedClaims.EfCore;

namespace Veriqa.Core.AuthServer.Host;

/// <summary>
/// Relational stores of this host. Each provider entry keeps a migrations assembly and a health check
/// per store.
/// </summary>
internal enum RelationalStore
{
    /// <summary>
    /// Transaction store (Veriqa:TransactionEngine:Store).
    /// </summary>
    Transactions,

    /// <summary>
    /// OpenIddict store (Veriqa:OpenIddict:Database).
    /// </summary>
    OpenIddict,

    /// <summary>
    /// Audit journal sink (Veriqa:Logging:Store, SPEC-012 CFG-252).
    /// </summary>
    Audit,

    /// <summary>
    /// Collected claims store (Veriqa:CollectedClaims:Store, SPEC-041 C37). Lives on the connection string
    /// and in the migrations assembly of the transaction store, with a migration history table of its own.
    /// </summary>
    CollectedClaims
}

/// <summary>
/// Names of the migrations assemblies one provider ships for the relational stores of this host.
/// </summary>
/// <param name="Transactions">Migrations assembly of the transaction store.</param>
/// <param name="OpenIddict">Migrations assembly of the OpenIddict store.</param>
/// <param name="Audit">Migrations assembly of the audit journal.</param>
internal sealed record RelationalStoreMigrations(string Transactions, string OpenIddict, string Audit)
{
    /// <summary>
    /// Returns the migrations assembly of the given store.
    /// </summary>
    /// <param name="store">Relational store.</param>
    /// <returns>Migrations assembly name.</returns>
    public string For(RelationalStore store) => store switch
    {
        RelationalStore.Transactions => Transactions,
        RelationalStore.OpenIddict => OpenIddict,
        RelationalStore.Audit => Audit,

        // The collected claims store ships its migrations in the transaction store's assemblies.
        RelationalStore.CollectedClaims => Transactions,
        _ => throw new ArgumentOutOfRangeException(nameof(store), store, null)
    };
}

/// <summary>
/// One relational EF Core provider this host is built with: the configuration value that selects it,
/// how a store is configured on it and how its health check is registered. Entries live in
/// <see cref="RelationalStoreProviders"/>.
/// </summary>
internal sealed class RelationalStoreProvider
{
    /// <summary>
    /// Provider call on the options builder: connection string, migrations assembly, migration history
    /// table (null keeps the provider default).
    /// </summary>
    private readonly Action<DbContextOptionsBuilder, string, string, string?> _useProvider;

    /// <summary>
    /// Health check registration: connection string, check name.
    /// </summary>
    private readonly Action<IHealthChecksBuilder, string, string> _addHealthCheck;

    /// <summary>
    /// Prefix of the health check names of this provider (the store name follows it).
    /// </summary>
    private readonly string _healthCheckNamePrefix;

    /// <summary>
    /// Migrations assemblies of this provider.
    /// </summary>
    private readonly RelationalStoreMigrations _migrations;

    /// <summary>
    /// Startup check that the provider can run in this process (fail-fast), or null when it needs none.
    /// </summary>
    private readonly Action? _ensureAvailable;

    /// <summary>
    /// Creates a provider entry.
    /// </summary>
    /// <param name="name">Configuration value that selects the provider.</param>
    /// <param name="healthCheckNamePrefix">Prefix of the health check names.</param>
    /// <param name="migrations">Migrations assemblies of the provider.</param>
    /// <param name="useProvider">Provider call on the options builder.</param>
    /// <param name="addHealthCheck">Health check registration.</param>
    /// <param name="ensureAvailable">
    /// Startup check that the provider can run in this process; it throws when it cannot. Null when the
    /// provider needs none.
    /// </param>
    public RelationalStoreProvider(
        string name,
        string healthCheckNamePrefix,
        RelationalStoreMigrations migrations,
        Action<DbContextOptionsBuilder, string, string, string?> useProvider,
        Action<IHealthChecksBuilder, string, string> addHealthCheck,
        Action? ensureAvailable = null)
    {
        Name = name;
        _healthCheckNamePrefix = healthCheckNamePrefix;
        _migrations = migrations;
        _useProvider = useProvider;
        _addHealthCheck = addHealthCheck;
        _ensureAvailable = ensureAvailable;
    }

    /// <summary>
    /// Configuration value that selects the provider (compared ignoring case, trimmed).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Builds the EF Core configurator of a store on this provider: the provider call with the
    /// connection string, the migrations assembly of the store and, for the audit journal and the collected
    /// claims store, a migration history table of their own — each may share a database with the
    /// transaction store. The
    /// provider's availability check runs here, so a store selected on a provider that cannot run in this
    /// process stops the startup instead of failing at the first query.
    /// </summary>
    /// <param name="store">Relational store.</param>
    /// <param name="connectionString">Non-empty connection string (guaranteed by the caller).</param>
    /// <returns>Delegate configuring the options builder.</returns>
    public Action<DbContextOptionsBuilder> BuildConfigurator(RelationalStore store, string connectionString)
    {
        _ensureAvailable?.Invoke();

        var migrationsAssembly = _migrations.For(store);
        var migrationsHistoryTable = store switch
        {
            RelationalStore.Audit => AuditMigrationDefaults.MigrationsHistoryTable,
            RelationalStore.CollectedClaims => CollectedClaimsMigrationDefaults.MigrationsHistoryTable,
            _ => null
        };

        return ef => _useProvider(ef, connectionString, migrationsAssembly, migrationsHistoryTable);
    }

    /// <summary>
    /// Registers the readiness health check of a store on this provider, named
    /// <c>&lt;provider&gt;-&lt;store&gt;</c> (for example <c>postgresql-transactions</c>).
    /// </summary>
    /// <param name="checks">Health checks builder.</param>
    /// <param name="store">Relational store.</param>
    /// <param name="connectionString">Non-empty connection string (guaranteed by the caller).</param>
    public void AddHealthCheck(IHealthChecksBuilder checks, RelationalStore store, string connectionString)
    {
        var storeName = store switch
        {
            RelationalStore.Transactions => "transactions",
            RelationalStore.OpenIddict => "openiddict",
            RelationalStore.Audit => "audit",
            _ => throw new ArgumentOutOfRangeException(nameof(store), store, null)
        };

        _addHealthCheck(checks, connectionString, $"{_healthCheckNamePrefix}-{storeName}");
    }
}

/// <summary>
/// Table of the relational providers this host is built with (SPEC-020 MODE-NET-3-010). The set is
/// fixed at build time by the package and migrations references of the project; the provider of each
/// store is selected by configuration at runtime. Adding a provider is one entry here plus the project
/// references — the store resolvers and the health checks of the host read the table and do not change.
/// </summary>
internal static class RelationalStoreProviders
{
    /// <summary>
    /// Value of Veriqa:OpenIddict:Database:Provider that selects the volatile in-memory OpenIddict store
    /// (SPEC-012 CFG-118). It is not a relational provider and has no entry in the table.
    /// </summary>
    public const string InMemoryName = "InMemory";

    /// <summary>
    /// PostgreSQL — also the provider of a store whose Provider key is empty.
    /// </summary>
    public static RelationalStoreProvider PostgreSql { get; } = new(
        name: "PostgreSQL",
        healthCheckNamePrefix: "postgresql",
        migrations: new RelationalStoreMigrations(
            Transactions: "Veriqa.Core.TransactionEngine.Migrations.PostgreSql",
            OpenIddict: "Veriqa.Core.AuthServer.Migrations.PostgreSql",
            Audit: "Veriqa.Core.AuditTrail.Migrations.PostgreSql"),
        useProvider: static (ef, connectionString, migrationsAssembly, migrationsHistoryTable) =>
            ef.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(migrationsAssembly);

                if (migrationsHistoryTable is not null)
                {
                    npgsql.MigrationsHistoryTable(migrationsHistoryTable);
                }
            }),
        addHealthCheck: static (checks, connectionString, name) =>
            checks.AddNpgSql(
                connectionString: connectionString,
                name: name,
                tags: [InfrastructureConfigConstants.ReadinessTag]));

    /// <summary>
    /// SQL Server. On Windows the driver needs a native library the Windows build does not ship — see
    /// <see cref="SqlServerNativeNetworking"/>.
    /// </summary>
    public static RelationalStoreProvider SqlServer { get; } = new(
        name: "SqlServer",
        healthCheckNamePrefix: "sqlserver",
        migrations: new RelationalStoreMigrations(
            Transactions: "Veriqa.Core.TransactionEngine.Migrations.SqlServer",
            OpenIddict: "Veriqa.Core.AuthServer.Migrations.SqlServer",
            Audit: "Veriqa.Core.AuditTrail.Migrations.SqlServer"),
        useProvider: static (ef, connectionString, migrationsAssembly, migrationsHistoryTable) =>
            ef.UseSqlServer(connectionString, sqlServer =>
            {
                sqlServer.MigrationsAssembly(migrationsAssembly);

                if (migrationsHistoryTable is not null)
                {
                    sqlServer.MigrationsHistoryTable(migrationsHistoryTable);
                }
            }),
        addHealthCheck: static (checks, connectionString, name) =>
            checks.AddSqlServer(
                connectionString: connectionString,
                name: name,
                tags: [InfrastructureConfigConstants.ReadinessTag]),
        ensureAvailable: SqlServerNativeNetworking.EnsureAvailable);

    /// <summary>
    /// All entries, in the order the allowed values are reported to the operator.
    /// </summary>
    public static IReadOnlyList<RelationalStoreProvider> All { get; } = [PostgreSql, SqlServer];

    /// <summary>
    /// Allowed relational values as the refusal message lists them: <c>'PostgreSQL', 'SqlServer'</c>.
    /// </summary>
    public static string AllowedNames { get; } = string.Join(", ", All.Select(provider => $"'{provider.Name}'"));

    /// <summary>
    /// Finds the entry a configuration value selects.
    /// </summary>
    /// <param name="value">Configuration value (trimmed, compared ignoring case).</param>
    /// <returns>The entry, or null when the value selects none.</returns>
    public static RelationalStoreProvider? Find(string value)
    {
        var trimmed = value.Trim();

        return All.FirstOrDefault(provider => string.Equals(provider.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the entry a non-empty Provider key selects. An unrecognized value is a configuration
    /// exception (fail-fast), so a typo ("Postgres", an extra letter) does not silently pick another
    /// provider.
    /// </summary>
    /// <param name="key">Configuration key the value was read from.</param>
    /// <param name="value">Non-empty configuration value.</param>
    /// <returns>The selected entry.</returns>
    public static RelationalStoreProvider Resolve(string key, string value) =>
        Find(value)
        ?? throw new InvalidOperationException($"Unsupported value '{key}' = '{value}'. Allowed: {AllowedNames}.");
}
