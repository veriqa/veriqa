# Veriqa.Core.TransactionEngine.Redis

**Keep Veriqa sign-in transactions in Redis and run several replicas of your host.** A
sign-in started on one replica completes on another.

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Transaction store](https://veriqa.app/docs/guides/storage#transaction-store).

## About this package

Redis transaction store for the Veriqa Transaction Engine — a satellite package. It keeps
authentication transactions in Redis, so several replicas of a host see one transaction. Nothing but
this package pulls a Redis client into the dependency graph of a deployment that does not use it.

Transactions are short-lived by nature, which is what makes a key-value store with an expiry a
natural home for them; a deployment that needs them in a relational database installs the EF Core
satellite instead.

## Install

```bash
dotnet add package Veriqa.Core.TransactionEngine.Redis
```

## Wiring

```csharp
authServer.ConfigureTransactionEngine(te =>
    te.UseRedisStore(redis => redis.Configuration = "localhost:6379"));
```

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
