# Veriqa.Core.AuditTrail.Migrations.PostgreSql

**The PostgreSQL schema of the Veriqa audit trail, ready to apply.**

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Data handling](https://veriqa.app/docs/guides/data-handling).

## About this package

PostgreSQL migrations for the audit journal schema of Veriqa. The package contains no API of its
own: it is the migrations assembly a host names by string when it configures the EF Core audit sink,
plus the design-time factory `dotnet ef` needs.

A host that takes the audit trail from NuGet cannot create the journal schema without it. Migrations
are per provider by EF Core design, so a different database gets a different package.

## Install

```bash
dotnet add package Veriqa.Core.AuditTrail.Migrations.PostgreSql
```

## Wiring

```csharp
services.AddVeriqaAuditTrail(audit =>
    audit.UseEfCoreSink(ef => ef.UseNpgsql(
        connectionString,
        npgsql => npgsql.MigrationsAssembly("Veriqa.Core.AuditTrail.Migrations.PostgreSql"))));
```

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
