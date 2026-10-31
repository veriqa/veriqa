# Veriqa.Core.AuditTrail

**An audit trail of Veriqa sign-ins and confirmations.** Identities are always masked, and
tokens are never written to it.

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Data handling](https://veriqa.app/docs/guides/data-handling).

## About this package

Satellite package holding the audit trail of the Veriqa core: the event-bus receiver, the
append-only journal and the retention process. It is optional — a host that does not register it
runs without an audit trail, and no core library references this package. The record model
(`AuditRecord`, `AuditMetadata`) and the write-only sink contract (`IAuditSink`) live in the
MIT-licensed `Veriqa.Core.Contracts` package, so an alternative journal can be written against them
without this assembly.

The journal does not depend on the OIDC server: the keys of the Logging axis (`Logging.Mode`,
`Logging.RetentionDays`) and the `LoggingMode` enum live in `Veriqa.Core.Configuration`, the
settings mechanism both sides of the axis already depend on. Declaring those keys and binding their
core level remain with the auth server assembly — declaring a key and owning its home are different
acts — so a deployment that wires the journal without the auth server resolves the retention to
nothing, and the sweep says so and skips the pass instead of deleting everything.

## What gets written, and when

The receiver subscribes to the core's transaction event bus and is the only place that decides
whether an event becomes a record. Publication itself is never filtered — the same bus drives the
real-time sign-in UI. A record is created **only** when the effective `Logging.Mode` resolves to
`Audit`; `Disabled` and `System` both mean "do not write", and there is no partial audit. The mode
is resolved per event, so a transaction that outlives a mode switch leaves a partial trail by
design.

Every record has the same six fields — timestamp, actor, action, target, result, metadata — and the
metadata is a closed set of safe attributes. Channel identities appear masked; snapshot data and
tokens never reach the journal. Issuing a token is not audited: that event does not travel the
transaction bus, and nothing else writes to the journal.

## Reading the journal

There is no read API, no endpoint and no UI — by contract, not by omission. An operator of a
self-hosted deployment reads the journal with the tools of the deployment itself: for the EF Core
sink, that is direct SQL access to the audit records table in the configured database. The
in-memory sink is a development convenience with no durability at all.

## Retention

Records are deleted only by the retention sweep, and only when they are older than the effective
`Logging.RetentionDays` (default 90). The sweep runs regardless of the current mode: switching the
mode off stops new records from appearing, it does not freeze the ones already written. How often
it runs and in what batches is host-owned (`ConfigureRetention`); how long a record lives is not.

The sweep deletes through a contract that stays internal to this assembly — next to a write-only
sink, a public delete contract would hand every consumer a way to erase audit records. A custom
sink therefore cannot be swept by it, and is not: see the sink table below.

## Known limitation: the sink shares the bus dispatcher

The receiver is an ordinary subscriber of the core's transaction event bus, and that bus has a
single reader calling its handlers one after another. The journal therefore competes for the same
dispatcher as the real-time sign-in notification: a slow (not failed) sink delays every handler
queued behind it, and once the bounded publication queue fills up, the publishing request thread
waits for a free slot. A failed sink is already contained — the receiver swallows its exceptions and
the sign-in proceeds — but a slow one is not isolated, so the journal is off the critical path only
as long as it keeps up.

Registration order is what decides who waits for whom: registering the audit trail after the
handlers that drive the UI lets the browser be notified before the record is written. That removes
the first hop of the delay, not the coupling itself — the next event still queues behind this
record. Isolating the journal behind a reader of its own is deliberately not done: the core keeps
one event dispatcher (SPEC-011 N26), and without a durable buffer a private queue would only move
the back-pressure, while records may not be dropped (R33).

## Actor of a decline: only the web page carries a masked identity

Only a refusal answered on the core web page reaches the journal with a masked channel identity in
`actor`. A refusal answered inside the channel — and the same answer given on the email
confirmation page — is recorded with the system actor and with empty `channel_type` and
`masked_channel_user_id`.

This is the norm rather than a gap in it (SPEC-011 L34): attributing a decline is not a
requirement of the journal, because the record is carried by the fact of the event and by the
reason code in `result`, not by who answered. The actor is resolved from the attribution the event
itself carries, and an actor is never invented to fill the field.

Where the difference comes from: a record carries only what the bus event carries, and the channel
identity reaches an event solely by being attached to the transaction first. On these two paths
nothing attaches it — the single operation that does also publishes an attachment event, which the
real-time layer turns into an irreversible browser redirect to the confirmation page, for a
transaction that is about to end. Attributing them would take a change to how events carry
attribution, which the spec explicitly does not require (N26).

## Relation to Serilog

Operational logs and the audit journal are two independent axes. Serilog is diagnostics, configured
per deployment; exporting audit records through a Serilog sink as well is allowed, but the
contractual guarantees — immutability, retention, access — are carried by the audit store only. An
operational log is not a substitute for the journal.

## Registration

```csharp
builder.Services.AddVeriqaAuditTrail(audit =>
{
    audit.UseEfCoreSink(ef => ef.UseNpgsql(connectionString,
        npgsql => npgsql
            .MigrationsAssembly("Veriqa.Core.AuditTrail.Migrations.PostgreSql")
            .MigrationsHistoryTable("__AuditTrailMigrationsHistory")));
});
```

`UseEfCoreSink` itself ships in a satellite package — this package carries no ORM, so a deployment
that keeps the in-process sink never receives EF Core. The migrations named in
`MigrationsAssembly(...)` ship as one more package, of the same name as the assembly:

```bash
dotnet add package Veriqa.Core.AuditTrail.EntityFrameworkCore
dotnet add package Veriqa.Core.AuditTrail.Migrations.PostgreSql
```

The provider, the connection string and the migrations assembly are the host's choice. When the
journal shares a database with another store, give it its own migration history table. Selecting no
sink is a startup failure rather than a silent in-memory fallback: for a journal, losing records
quietly is worse than refusing to start.

## Choosing the sink

Exactly one sink is chosen, and what it is decides who owns the retention:

| Call | Sink | Retention |
|---|---|---|
| `UseInMemorySink()` | in-process, development and running without a database | the built-in sweep |
| `UseEfCoreSink(...)` (the `Veriqa.Core.AuditTrail.EntityFrameworkCore` package) | the audit table of the configured database | the built-in sweep |
| `UseSink<TSink>()` | any `IAuditSink` implementation of yours | **yours** — no sweep is registered, and the host says so at startup with an `Information` record |

```csharp
builder.Services.AddVeriqaAuditTrail(audit => audit.UseSink<MyAuditSink>());
```

Choosing a built-in sink **and** a custom one in the same registration is refused on the spot: the
records would go to the custom sink while the sweep kept clearing the built-in store. The same
refusal covers any `IAuditSink` registered *after* a built-in sink — through
`AuditTrailBuilder.Services` or by a following `AddVeriqaAuditTrail` call — for the same reason.
Registering one *before* the built-in sink is not refused: the built-in sink is then the last
registration, and it owns both the records and their deletion. Calling
`ConfigureRetention(...)` next to a custom sink is not an error — the parameters simply have no
sweep to configure, which the same startup record states.

A registration can only be refused while it is still a registration: an `IAuditSink` put into the
host's own `IServiceCollection` *after* `AddVeriqaAuditTrail(...)` has returned is invisible to
that check. It is caught one step later instead — the sweep compares the sink the container
resolved against the store it deletes from, and starts with a `Warning` record when the two have
come apart. A sink that *wraps* the built-in one is a different instance too and gets the same
record, although it writes through to the swept store; the notice says so.

Calling `AddVeriqaAuditTrail(...)` more than once on the same collection is allowed, and the
retention parameters are shared between the calls: a later call that never touches
`ConfigureRetention(...)` leaves what an earlier one set in place, and one that does configures the
sweep already registered.

An ecosystem package can extend the registration surface itself: `AuditTrailBuilder.Services` is
public (hidden from IntelliSense), so an extension method on the builder registers whatever the
sink needs — its options, its clients, its hosted services — inside the same `AddVeriqaAuditTrail`
call.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
