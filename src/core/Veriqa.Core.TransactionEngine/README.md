# Veriqa.Core.TransactionEngine

**The sign-in transaction at the heart of Veriqa.** A sign-in has an explicit state, a
lifetime and a single outcome: it survives a change of device, expires on its own and handles
repeated requests idempotently.

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Architecture](https://veriqa.app/docs/concepts/architecture).

## About this package

The lifecycle of an authentication / confirmation transaction: the state machine, the
transaction service, the pluggable transaction store and in-process event publishing. The store
shipped inside this package is the in-memory one; the EF Core and Redis stores and the RabbitMQ
publisher live in satellite packages (`Veriqa.Core.TransactionEngine.EntityFrameworkCore`,
`Veriqa.Core.TransactionEngine.Redis`, `Veriqa.Core.TransactionEngine.RabbitMq`), so a deployment
never receives an ORM or a broker client it does not use.

The finalization sequence — confirm → resolve identity → complete, compensating a failed
completion — lives here too, in `ChannelTransactionFinalizer.ConfirmAndCompleteAsync`
(`Veriqa.Core.TransactionEngine.Services`). It drives the state machine and the identity
resolution and touches no channel, so every path that finalizes a sign-in (the channel webhook,
the core confirmation page, any future one) reaches it without depending on a channel assembly.

## Writing your own transaction store

`ITransactionStore` is a published contract: an implementation over SQL Server, Mongo, DynamoDB or
anything else lives in your own assembly and needs no access to Veriqa internals.

### Register your store before `AddVeriqaTransactionEngine`

The engine wraps the store it composed with its own state-transition guard — the check that keeps a
forbidden state out of the storage no matter who produced the transaction. It can only wrap what is
already in the collection, so the registration has to come first:

```csharp
// Either register it yourself, above the engine…
builder.Services.AddSingleton<ITransactionStore, MyStore>();
builder.Services.AddVeriqaTransactionEngine(builder.Configuration);

// …or hand it to the engine builder, which is the same thing in one step.
builder.Services.AddVeriqaTransactionEngine(builder.Configuration, engine => engine.UseInMemoryStore());
```

A store registered **after** the engine wins the resolution and would carry no guard. That is not
left to be discovered later: the engine refuses to start and the message names both the cause and
the fix.

### Rehydration — snapshot in, snapshot out

A transaction does not expose public setters for its lifecycle fields (`State`, `StateReasonCode`,
`UpdatedAt`, the three outcome snapshots, `ConcurrencyToken`): moving a transaction is the business
of the state machine alone. A store does not move a transaction — it restores one that already
moved — so it gets a path of its own, and only that one:

```csharp
// Writing: take the whole transaction as one object.
var snapshot = transaction.ToSnapshot();

// Reading: restore it with every field exactly as it was stored.
var restored = Transaction.Restore(new TransactionSnapshot
{
    Id = snapshot.Id,
    Type = snapshot.Type,
    State = snapshot.State,               // required — not the default state
    CreatedAt = snapshot.CreatedAt,
    UpdatedAt = snapshot.UpdatedAt,       // required
    ExpiresAt = snapshot.ExpiresAt,
    AllowedChannelTypes = snapshot.AllowedChannelTypes,
    ConcurrencyToken = snapshot.ConcurrencyToken,   // required — never a fresh one
    // …every remaining field
});
```

`State`, `UpdatedAt` and `ConcurrencyToken` are `required` on `TransactionSnapshot` even though
`Transaction` gives them defaults. That is deliberate: a store that forgets them would return a
transaction sitting in its default state with a freshly generated token — the state machine and
optimistic concurrency would both break, silently, with nothing to observe. `required` turns that
into a compilation error.

Do not construct a `Transaction` with an object initializer inside a store, and do not reflect over
its properties. `Restore` freezes `AllowedChannelTypes` for you, so any read-only set will do.

**Obligation when the model grows.** `TransactionSnapshot` is the full field perimeter of a
transaction. A field added to `Transaction` must be added here as well, in the same change —
otherwise stores keep compiling and quietly stop carrying it.

**Upgrading your store.** A new property of `TransactionSnapshot` can arrive without breaking the
build of your store. Store the snapshot as one serialized document, with a serializer that writes every
public property, or compare the properties of `TransactionSnapshot` with your mapping after each
upgrade: a property you do not map is lost on every write. The values of the conditional `Try…` writes
are an exception: a new one can arrive without a new member of the store — a new value of
`HopObservationMark`, for example, is a new mark for `TryMarkHopObservationAsync` to write — and
`UpdateAsync` neither writes nor erases it. After each upgrade, make sure that your `Try…` members
write every new value and that `UpdateAsync` keeps it; if your
`UpdateAsync` carries such values over from the stored record by a fixed list of fields, add the new
one to the list.

### Obligations of AddAsync and UpdateAsync

- `AddAsync` **must throw** `DuplicateIdempotencyKeyException` when a transaction with the same
  (`IdempotencyScope`, `IdempotencyKey`) pair is already stored. Idempotency in the engine rests on
  that exception; returning the existing transaction instead, or overwriting it, breaks idempotency
  with no failure to observe. The uniqueness check must be atomic with the insert — a unique index
  or a conditional write, not a read followed by a write.
- `UpdateAsync` **must compare** the stored concurrency token with `expectedConcurrencyToken` and
  return `false` on a mismatch instead of writing. The comparison and the write must be one atomic
  operation (a conditional update, a `WHERE token = …` clause, a compare-and-swap).
- `UpdateAsync` **must not write** a state the stored transaction cannot reach.
  `TransactionStateMachine.CanTransition` — public for exactly this — decides, over the state
  currently stored and the state the passed transaction carries. A state equal to the stored one is
  not a transition, so a write that changes only other fields goes through. The refusal is an
  `InvalidStateTransitionException`, never a `false`: `false` means a lost optimistic lock or a
  transaction that is no longer stored, and a caller answers it by re-reading and retrying — which
  for a forbidden transition never terminates. Throw it **only when the concurrency token still
  matches**, that is, when the write would otherwise have been applied: a caller working off a stale
  copy has already lost the optimistic lock, and the transition its copy appears to make is an
  artefact of that copy — it keeps getting `false`, and its retry terminates, because the re-read
  copy carries the state actually stored.
- `ISupportsNativeExpiry` is a marker for a store whose backend removes terminal records itself
  (a TTL). Implement it and the cleanup service stops deleting those records; it still scans for
  expired transactions in order to publish `TransactionExpiredEvent`.

### Obligations of GetPendingSignInsAsync

When a sign-in gets its channel subject for the first time, the engine closes the other sign-ins
of the same tenant, application and channel subject that are still `Pending`: each goes to `Failed`
with the reason `superseded_by_new_sign_in`. `GetPendingSignInsAsync` is how it finds them, and the
member has no default body — a store that does not implement it does not compile.

- **Return every stored transaction** in the `Pending` state whose `SignInSubjectScope.Of(…)` equals
  the scope passed in (record equality, all four members compared ordinally). The deadline does not
  narrow the selection: a sign-in past its TTL that the cleanup pass has not promoted yet is returned
  as well. The order is not significant; nothing found is an empty list; a null scope is an
  `ArgumentNullException`.
- **Index by `SignInSubjectScope.ToFingerprint()`** rather than by the four members: it is a stable
  64-character lowercase hex SHA-256 and keeps the channel user identifier out of column values and
  key names. Recompute it on every write of the transaction — it is derived, never read back.
- **Too much is harmless, too little is not.** The engine re-checks every returned transaction
  before closing it (still `Pending`, same scope, not the new sign-in itself), so an extra one
  cannot close someone else's sign-in. A missing one is a sign-in that lives on to its deadline and
  may still write to the chat over the new one.
- **Do not answer with an empty list in silence.** A store that cannot run this selection must say
  so in its own documentation.

### Obligations of the hop observation writes

A hop link leaves five observations on its transaction — the scanner context
(`ScannerContextSnapshot`) and four marks (`HopFirstResolvedAt`, `HopInterstitialOpenedAt`,
`HopChannelLaunchedAt`, `HopRepeatResolvedAt`). They are observations, not state, and once the
transaction is stored a store writes them through three methods of their own, all abstract — there is
no default body to fall back on:

- `TryMarkHopObservationAsync(id, mark, markedAt)` — writes the mark named by `HopObservationMark`;
- `TryAttachScannerContextAsync(id, snapshot)` — attaches the scanner snapshot, which arrives without
  a fingerprint (`snapshot.ScannedAt` equals `markedAt` of the `FirstResolve` mark; the store does
  not check it);
- `TryFillScannerFingerprintAsync(id, fingerprint)` — fills the fingerprint of an attached snapshot.

Each returns `true` only to the call that wrote the value, and `false` when the field is already
filled, when (for the fingerprint) no snapshot is attached, or when the transaction is not stored.
What every implementation owes:

- **Once, by the first writer.** The write is an atomic conditional write on the field — "empty →
  value" (`WHERE … IS NULL`, `HSETNX`, a compare-and-swap) — never a read followed by a write: of two
  concurrent calls for one field exactly one gets `true`. A backend without atomicity on a field runs
  its own read → conditional write → retry loop over an unchanged `ConcurrencyToken` / `UpdatedAt`.
- **No competition with `UpdateAsync`.** The write changes neither `ConcurrencyToken`, nor
  `UpdatedAt`, nor `State`, and never makes a concurrent `UpdateAsync` with a valid token fail.
- **`AddAsync` stores the ones a new transaction carries.** Once it is stored, only the three writes
  above set them.
- **`UpdateAsync` neither writes nor erases them.** The observation values a passed transaction
  carries are ignored; the stored ones stay.
- **Every read returns them** — `GetByIdAsync`, `GetByIdempotencyKeyAsync`, `GetExpiredAsync`,
  `GetStaleTerminalAsync`, `GetStalledConfirmedAsync`.
- **They die with the transaction.** `DeleteAsync`, cleanup and the TTL of the store remove them
  together with it; nothing keeps them longer.
- **Arguments, not state.** A missing snapshot, a snapshot carrying a fingerprint, an empty
  fingerprint and an undefined mark are refused with an argument exception. The state of the
  transaction (terminal, expired) is not checked — that belongs to the callers.

### Obligations of the claims completion writes

A transaction that waits for missing claims carries `ClaimCompletion` (`ClaimCompletionState`). Its
parts are written in two ways:

- `ConfirmedAt` and `Items` — the completion mark and the list of missing claims — are ordinary fields:
  `UpdateAsync` writes them, `Restore` / `ToSnapshot` carry them;
- `Surface`, `FormTokenHash` and `Results` are written only by three conditional writes, all abstract:
  - `TrySetWebStepSurfaceAsync(id, expected, surface)` — the surface of the web step, moving
    "none → any" or "`CrossDevicePage` → `InitiatingPage`" (any other pair answers `false`);
  - `TrySetClaimFormTokenHashAsync(id, tokenHash)` — the fingerprint of the form token, moving
    "none → value"; the token itself never reaches the store;
  - `TryRecordClaimResultAsync(id, result)` — the outcome for one missing claim, once per claim.

What every implementation owes, beyond the obligations of the hop observation writes above (atomic
conditional write, no change of `ConcurrencyToken` / `UpdatedAt` / `State`, `UpdateAsync` neither writes
nor erases the three parts, every read returns them):

- **The state is part of the condition.** All three writes go through only while the transaction is
  `Pending` and carries a completion mark, checked in the same atomic step as the write — an outcome
  never lands after the transaction left `Pending`.
- **One outcome per claim, for a listed claim.** `TryRecordClaimResultAsync` adds the outcome only when
  `Items` names the claim and no outcome for it is recorded yet; claim names compare the way
  `VeriqaClaimTypes.NameComparer` does. A backend that keeps the outcomes in one document field runs a
  read → conditional write → retry loop whose condition covers the state, the mark and the outcomes it
  read.
- **Arguments.** An undefined surface, an empty token fingerprint, an outcome without a claim, with a
  value but no method (or the other way round), a verified skip and an undefined method are refused
  with an argument exception.

## Request context of a transaction: tenant, application, locale, time zone, ui_config

Five dimensions travel with a transaction, carried by one of two containers — `OidcContext` for a
transaction started by the OIDC sign-in flow, `TransactionRequestContext` for a server-to-server
one. The two are never populated together: their writers are different.

Read them **only** through `TransactionContextExtensions` — `GetTenantId()`, `GetApplicationId()`,
`GetUiLocale()`, `GetUiTimeZone()`, `GetUiConfigCode()`. Each applies the same precedence: the request-context
container, then the OIDC context, then `null`. Reaching into a container directly is how a reader
ends up seeing one kind of transaction and not the other.

Both containers are serialized whole by the store, so the tenant needs no column and no schema
change of its own.

### Where the tenant comes from

The tenant is resolved from `client_id` by the `IClientTenantResolver` port — once per request, at
every entry point of the auth server that knows the client but has no transaction to read the tenant
off (no ambient tenant scope is open on those HTTP paths; only the channel paths open one):

- the sign-in (`/connect/authorize`) stores the answer on `OidcContext.TenantId` of the transaction it
  creates;
- server-to-server creation of a confirmation transaction (`POST /api/transaction/confirmation`)
  stores it on the request context of the transaction it creates (`TransactionRequestContext.TenantId`);
- the `form_post` response page asks again for its branding: the handler that writes it is given the
  HTTP context only, not the transaction — the mapping is deterministic, so the answer is the one the
  sign-in stored.

Every later reader of a transaction takes the tenant off the transaction (`GetTenantId`), not from the
resolver. **The engine's own implementation always answers
`null`** — the default implicit tenant of an installation that never heard of tenants, and the right
answer for a host using the engine without the auth server. Where the auth server is present, the
answer comes from the calling client's own configuration entry
(`Veriqa:OpenIddict:Clients[].TenantId`): `AddVeriqaOpenIddict` displaces the engine's null answer
through `ReplaceShippedClientTenantResolver<TResolver>()` — the seam a component wired AFTER the
engine uses to take over the slot, because `TryAdd` alone would leave the null standing.
Multi-tenant hosts register their own:

```csharp
builder.Services.AddSingleton<IClientTenantResolver, MyClientTenantResolver>();
```

Your registration wins over both the engine's answer and the auth server's, whatever the call
order, as long as it is last-wins — `AddSingleton` or `Replace`. `TryAdd` wins only while the slot
is still free, that is, before either the engine or the auth server has registered one. A resolver
that throws fails none of the requests above — sign-in, confirmation creation or the `form_post`
page: the tenant is treated as unstated, the request resolves past the tenant level, and the failure
is logged. Cancellation of the request is not swallowed.

The tenant is a convenience for resolving tenant-level configuration keys — **not an access
boundary**. Isolation is provided by `client_id`, which sits above the tenant; no store method is
narrowed by tenant and none refuses a transaction over a tenant mismatch.

### Keys that carry the tenant

Two keys include the tenant so that tenants do not share a budget or a record:

- the per-user rate-limiter key — `<tenant>:<channel_type>:<channel_user_id>`;
- the channel-identity key — the triple (tenant, `ChannelType`, `ChannelUserId`).

An unstated tenant is spelled one fixed way in both (`TenantKey.Default`), so `null`
and an empty string never produce two keys for one default tenant.

Both keys read the tenant off the same field — `ChannelIdentitySnapshot.TenantId`, written by the
adapter — so the counter and the record of one user always belong to the same tenant. A channel
routed per tenant (Telegram, Max, WhatsApp) takes that value from the ambient scope the host opens
for the inbound request; Email has no tenant routing at all — neither its pages nor its inbound
webhook carry a `{tenant}` segment — so it takes the value from the transaction being confirmed.

**Upgrade note.** These key shapes changed: rate-limiter counters and `ChannelIdentity` records
written before the upgrade are not found afterwards and are created anew. Both are short-lived
working data, so nothing is lost — but a per-user rate-limit window in flight across the upgrade
starts over.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
