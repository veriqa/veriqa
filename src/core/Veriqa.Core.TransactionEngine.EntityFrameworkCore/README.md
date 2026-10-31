# Veriqa.Core.TransactionEngine.EntityFrameworkCore

**Keep Veriqa sign-in transactions in PostgreSQL, SQL Server or another EF Core database.**

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Transaction store](https://veriqa.app/docs/guides/storage#transaction-store).

## About this package

EF Core transaction store for the Veriqa Transaction Engine — a satellite package. The core
engine ships with an in-memory store; installing this one moves authentication transactions into a
relational database without the core taking a dependency on an ORM.

The database provider (Npgsql and friends) and the connection string belong to the host: this
package takes the `DbContextOptionsBuilder` you configure and nothing else. The migrations live in
their own package per provider — for PostgreSQL that is
`Veriqa.Core.TransactionEngine.Migrations.PostgreSql`.

## Install

```bash
dotnet add package Veriqa.Core.TransactionEngine.EntityFrameworkCore
```

## Wiring

```csharp
authServer.ConfigureTransactionEngine(te =>
    te.UseEfCoreStore(ef => ef.UseNpgsql(
        connectionString,
        npgsql => npgsql.MigrationsAssembly("Veriqa.Core.TransactionEngine.Migrations.PostgreSql"))));
```

## Collected claims store

The same package carries the EF Core implementation of the optional collected claims store — what a
user entered or skipped during claims completion is kept per subject and not asked again. It is off
unless registered, has a context of its own (`CollectedClaimsDbContext`), ships its migrations in the
transaction migrations packages and keeps its own migration history table, because it may share the
transaction database:

```csharp
authServer.ConfigureTransactionEngine(te =>
    te.UseEfCoreCollectedClaimsStore(ef => ef.UseNpgsql(
        connectionString,
        npgsql => npgsql
            .MigrationsAssembly("Veriqa.Core.TransactionEngine.Migrations.PostgreSql")
            .MigrationsHistoryTable(CollectedClaimsMigrationDefaults.MigrationsHistoryTable))));
```

## Provider requirement

The one-time hop observation writes (`TryMarkHopObservationAsync`, `TryAttachScannerContextAsync`,
`TryFillScannerFingerprintAsync`) are single conditional `UPDATE … WHERE … IS NULL` statements
issued through `ExecuteUpdateAsync`, so the provider has to translate `ExecuteUpdate`. Relational
providers (Npgsql, SQL Server) do; the EF Core InMemory provider does not, and these writes fail on
it.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
