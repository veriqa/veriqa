# Veriqa.Core.Contracts

MIT-licensed contracts package of Veriqa — the **free SPI** a channel adapter is written against.
An adapter that references this package and nothing else from Veriqa is a complete adapter: the
MPL-2.0 server core is a dependency of the *host*, never of the adapter.

## Where the boundary is declared

The free SPI is exactly the public surface of this assembly, and that surface is **declared, not
implied**: every public type and member is listed in `PublicAPI.Unshipped.txt` next to the project
sources, and `PublicAPI.Shipped.txt` is empty because the package has not been published yet.
The Roslyn public-API analyzer enforces both directions as build errors — a public member missing
from the inventory (RS0016) and an inventory entry with no member behind it (RS0017). So the SPI
cannot grow or shrink silently: it either shows up as a line in the diff of that file, or the
build breaks.

Namespaces do **not** carry the license. This assembly intentionally hosts namespaces
(`Veriqa.Core.ChannelAdapter.*`, `Veriqa.Core.TransactionEngine.*`, `Veriqa.Core.Configuration`)
that also exist in the MPL-2.0 core assemblies. The boundary is the assembly plus the declared
inventory — see "Not in this package" below for the neighbours that share a namespace but not the
license.

The package is also a **leaf**: it references no other Veriqa assembly. The edge between it and
the settings mechanism points the other way — `Veriqa.Core.Configuration` references this package
for the ownership-context types below — so referencing the free SPI never drags the core in.

## Free SPI

### Adapter contract

- `IChannelAdapter` — the channel adapter contract itself.
- `IChannelDisplayMetadata` — optional display metadata (name, glyph) of a channel.
- `ChannelCapabilities` — what the channel declares it can do: surfaces, message update, supported
  message kinds, recipient locale, outcome notice.

### Domain records

- Inbound: `ChannelInboundRequest`, `ChannelInboundResult` and its intent-specific shapes
  (`ChannelAuthStartResult`, `ChannelAuthConfirmResult`, `ChannelAuthDeclineResult`,
  `ChannelPhoneSharedResult`, `ChannelUnrelatedResult`), `ChannelInboundToken`.
- Outbound: `ChannelMessage`, `ChannelMessageRef`, `ChannelMessageKind`,
  `TransactionOutcomeNotice`, `TransactionOutcome`.
- Confirmation prompt: `ConfirmationPromptContext` with `DetailedConfirmationPromptContext`,
  `SubjectConfirmationPromptContext` and `SuppressedConfirmationPromptContext`,
  `ContextSuppressionReason`, `ConfirmationSurface`, `LoginConfirmationMode`. Every variant carries
  `Ownership` — see "Resolution context" below.
- Identity: `ChannelIdentitySnapshot` — the identity an adapter hands to the core, including the
  typed `EmailVerified` flag (a security-significant fact belongs in the contract, not in a
  free-form claim) and `TenantId`, the tenant the adapter states for the identity. Free-form
  adapter claims travel in `AdditionalClaims` and are subject to the claim-name groups below.
- Tenancy: `TenantKey` — the single spelling of a tenant inside a composite key (`TenantKey.Default`
  for an unstated one), so a rate-limiter counter and an identity record of the same user never end
  up under two different names for the default tenant.
- Health: `ChannelHealthStatus`.

### Resolution context

- `ResolutionContext` (namespace `Veriqa.Core.Configuration`) — the ownership levels a settings
  resolution runs over: the tenant, the application (`client_id`) and the `ui_config` record
  selector, plus `PerRequestContext` and `UserOverrideContext`. `ResolutionContext.Core` is the
  self-hosted value — no level is stated and the core level answers.
- It is here because a contract carries it: `ConfirmationPromptContext.Ownership` states the levels
  the prompt of that transaction is worded over. The resolution mechanism that reads those levels
  is not in this package — see "Not in this package" below.

### Common types

- `Result` and `Result<T>` — the result pattern used across the SPI.
- `TransactionError` — the failure payload carried by a failed `Result`; its `Code` comes from
  `VeriqaErrorCodes`.
- `TransactionId` and `TransactionIdJsonConverter`.

### Name registries

- `VeriqaErrorCodes` — the error codes that cross this package boundary: the ones an adapter
  returns through `IChannelAdapter`, and the ones the core branches on in behaviour observable
  from outside. Return these instead of inventing your own strings — a code outside the registry
  reaches neither the core's branches nor its telemetry tags and stays a line in a log.
- `VeriqaClaimTypes` — OIDC claim names issued by the core and by channel adapters, plus the three
  declared groups the core enforces when it merges `AdditionalClaims`: `MandatoryClaims` (names the
  core always issues), `CoreReservedClaims` (names derived from typed snapshot fields — among them
  `EmailVerified`), and `VendorClaims` (the closed list of vendor names accepted as an exception).
  Anything else must start with `{ChannelType}_` — your own namespace, collision-free by
  construction. A key that breaks the rule is dropped with a warning; authentication does not fail.
  Claim names are compared with `NameComparer` / `NameComparison` (case-insensitive, published by
  the same type): the core compares names exactly as the token consumers do, so re-casing a letter
  does not walk a reserved name past the check. That decides whose name it is, not how you may
  write it — a name you own is taken only in the declared spelling (`Veriqa_Channel`,
  `TELEGRAM_note` are dropped), and two of your names differing only in letter case are one claim:
  one of them is merged, the rest are dropped with a warning. Which spelling survives is not
  defined — send one spelling, not a pair.
- `VeriqaScopes` — names of the Veriqa-specific OAuth scopes. `channel` gates the `channel_type`
  and `channel_user_id` claims: a relying party that does not request it receives neither, in any
  token — and therefore not from the userinfo endpoint either. `avatar` gates the `picture` claim
  the same way; when it is requested, the avatar travels only in the access token and is read from
  the userinfo endpoint, never in the identity token.
- `AvatarDataUri` — the format of `ChannelIdentitySnapshot.AvatarUrl` when the adapter delivers the
  image itself: `data:<mime>;base64,<payload>`, JPEG, PNG, WebP or GIF detected by byte signature,
  at most `MaxImageBytes` (64 KiB) decoded bytes. The limit follows from the 128 KB limit of a
  transaction snapshot, which carries the image: a larger photo is left out rather than failing the
  sign-in. Build the value with `TryCreate` from the downloaded bytes;
  `TryParse` decodes and validates it into an `AvatarImage`. The other accepted value is an absolute
  https URL; `IsAcceptedAvatar` checks both. `ChannelIdentitySnapshot.AvatarUrl` stores any other
  value as null, so an adapter that hands over a malformed or oversized avatar signs the user in
  without one.
- `CallbackDataPrefixes` — the ownership markers Veriqa puts into the payload of its own buttons and
  deep links, identical across channels. They are what lets a router tell a Veriqa update from an
  update of your own bot before anything is parsed; `Vendor` (`vq_`) is the namespace they are built
  from. The default body of `IChannelAdapter.OwnsInboundEvent` matches against exactly these values.
- `ChannelTypes`, `ChannelInboundIntents`, `ChannelMessageKinds`, `ChannelNoticeKinds`,
  `TransactionOutcomeCodes`, `ContextSuppressionReasons`, `ChannelTelemetry`,
  `CustomChannelContract` — the remaining wire-level constant registries.

### Log masking

- `LogMasking` — `Fingerprint(value)` returns the 16 hex characters an adapter writes to a log
  instead of a raw identifier. A `channel_user_id`, a phone number, an e-mail address or a display
  name must not appear in a log record above the `Debug` level (`SPEC-003 §18.3`, `CA-121`), and
  this is what a record carries in its place: the same identifier always gives the same
  fingerprint, so the steps of a single sign-in still meet in the log, but the value itself is not
  in it. Name the placeholder for what it holds — `{ChannelUserIdHash}`, not `{ChannelUserId}` —
  or whoever reads the log will take the fingerprint for the value. The fingerprint is not a
  secret: it is unsalted on purpose (a salt would differ per replica and the records would stop
  meeting), so a short value behind it is guessable by brute force. At `Debug` and below the raw
  value is allowed — that is where an operator looks at it.

### Audit trail

- `AuditRecord` — one record of the audit journal, in the fixed six-field schema
  (`Timestamp`, `Actor`, `Action`, `Target`, `Result`, `Metadata`); `AuditResult` and `AuditOutcome`
  carry the binary outcome and, for a failure, its reason code. `AuditMetadata` is a **closed** set
  of safe attributes and deliberately not a dictionary: an attribute outside the list cannot be
  attached at all, so no personal data or transaction snapshot leaks into the journal through it.
  The one attribute the closed list cannot vet is `ChannelDetails` — free-form text whose content
  is yours. Keep the log discipline above in it: put a `LogMasking.Fingerprint` there, never a raw
  `channel_user_id`, phone number or e-mail address. Nothing checks this for you, and an audit
  record outlives a log line.
- `AuditActionCodes`, `AuditActors` — the stable action codes of the transaction lifecycle events
  and the `system` actor used for transitions nobody initiated directly.
- `IAuditSink` — the write-only sink the journal is written through. `AppendAsync` is its only
  member: the journal is append-only, and a read, update or delete member here would break that
  guarantee structurally. Retention is enforced inside the journal implementation, not through
  this contract, so an alternative implementation stays free to store records wherever it likes.
  Such an implementation is put in place with `AuditTrailBuilder.UseSink<TSink>()` of the
  `Veriqa.Core.AuditTrail` package, and the retention of its storage comes with it: the built-in
  retention sweep runs for the shipped sinks only and is not registered for a custom one.

The implementation that ships with Veriqa — the event-bus receiver gated by the logging mode,
the append-only stores and the retention process — lives in the separate `Veriqa.Core.AuditTrail`
package, not here.

## Versioning

The package follows SemVer, and the rule that turns a contract change into a number is a single
one: **a new extension point is a minor, a change to or a removal of a published one is a major.**
Nothing here changes quietly — the declared inventory above is what the rule is applied to.

### Surface that has not settled yet

A few types carry `[Experimental("VERIQAEXP0001")]`. They are closed hierarchies the roadmap still
adds variants to — claim enrichment, a shared contact, unlinking a channel, a chain of channels —
and the marker takes them out of the rule above: **a type marked with that identifier may change in
a minor version.** Today those are `ChannelInboundResult` and `ConfirmationPromptContext`.

Your compiler will not let you use one by accident: the marker is an error at the use site. To
proceed, say so explicitly — `<NoWarn>$(NoWarn);VERIQAEXP0001</NoWarn>` in your project, or a
`#pragma warning disable VERIQAEXP0001` around the use. Everything not marked is under the ordinary
rule, `ChannelCapabilities` included: it grows by a new property with a safe default, so an adapter
already compiled against it keeps compiling.

## Identifiers in the XML documentation

The XML comments of these types carry short identifiers — `SPEC-003 §6.2`, `CA-004`, `ICC-081`,
`UI-038` and the like. They are Veriqa's own numbering: `SPEC-NNN` is a specification and the
section inside it, and the lettered codes are individual requirements of those specifications
(`CA-` — channel adapters, `ICC-` — confirmation context and prompts, `CFG-` — settings
resolution, `EM-` — the Email channel, `UI-` — the pages of the core). A comment that names one
is saying "this behaviour is a stated rule, not an accident of the implementation" — worth
reading as a warning that the shape around it is deliberate.

The specifications themselves are internal documents and do not ship with the package. The
identifier is still useful to a consumer: it is stable, and quoting it in a question or a bug
report points at the exact rule a behaviour comes from.

## Not in this package (MPL-2.0 core)

These types share the `Veriqa.Core.ChannelAdapter.Abstractions` namespace with `IChannelAdapter`
but live in the MPL-2.0 core assembly. They are core-internal bookkeeping, not SPI, and an adapter
neither needs nor can reference them:

- `IChannelPromptMessageStore` — the core stores the reference to a sent prompt itself; an adapter
  only returns a `ChannelMessageRef` from `SendMessageAsync`.
- `ChannelPromptMessageRef` — the stored form of that reference, the core's own bookkeeping record.
- `IUserAuthRateLimiter` — per-user authentication rate limiting, enforced by the core.
- `IInitiatorAnomalyDetector` — initiator anomaly detection, enforced by the core.

The same holds for the `Veriqa.Core.Configuration` namespace: only the ownership-context types
listed above are in this package. The declarative keys, the levels, the precedence rules and
`IConfigurationResolver` itself live in the MPL-2.0 `Veriqa.Core.Configuration` assembly, which
references this one.

## License

MIT — see the `PackageLicenseExpression` of this package.

## Trademark

Veriqa™ is a trademark of Dmitrii Erusov. This package's MIT license covers the code, not the name
or the logo — `licenses/TRADEMARK.md` in the repository states what the policy allows without asking (in
short: truthful, descriptive use of the word mark, including in package manifests and dependency
lists, is fine).

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
