# Veriqa.Core.TransactionEngine.Migrations.PostgreSql

**The PostgreSQL schema of the Veriqa transaction store, ready to apply.**

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Transaction store](https://veriqa.app/docs/guides/storage#transaction-store).

## About this package

PostgreSQL migrations for the transaction schema of the Veriqa Transaction Engine. The package
contains no API of its own: it is the migrations assembly a host names by string when it configures
the EF Core store, plus the design-time factory `dotnet ef` needs.

A host that takes the core from NuGet cannot create the schema without it — the migrations its EF
Core options name by assembly name live here. Migrations are per provider by EF Core design, so a
different database gets a different package, not a switch inside this one.

The assembly also carries the migrations of the optional collected claims store
(`CollectedClaimsDbContext`, history table `__CollectedClaimsMigrationsHistory`): a host that registers
the store names this same assembly for it.

## Install

```bash
dotnet add package Veriqa.Core.TransactionEngine.Migrations.PostgreSql
```

## Wiring

```csharp
authServer.ConfigureTransactionEngine(te =>
    te.UseEfCoreStore(ef => ef.UseNpgsql(
        connectionString,
        npgsql => npgsql.MigrationsAssembly("Veriqa.Core.TransactionEngine.Migrations.PostgreSql"))));
```

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
