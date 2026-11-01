# Veriqa.Core.ChannelAdapter.Http

**Login and authentication by QR code through any messaging app or bot, with an adapter in any language.**
Write the adapter for your corporate messaging app, a regional platform or the bot you already run
as a separate service — in Node, Python, Go or anything else that speaks HTTP — and plug it into
Veriqa by configuration, without a line of .NET and without rebuilding the host. Your users sign in
through it exactly as through a built-in channel.

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [HTTP channel](https://veriqa.app/docs/guides/http-channel-adapter).

Use cases: [passwordless sign-in](https://veriqa.app/docs/scenarios/passwordless-login), [second factor and step-up](https://veriqa.app/docs/scenarios/step-up), [all use cases](https://veriqa.app/docs/scenarios/overview).

## About this package

The HTTP channel wrapper lets a channel whose adapter runs as a separate service take part in
sign-in and confirmation exactly like a built-in channel.

The external adapter owns the traffic of its platform: it receives the platform's webhooks or polls
it, and posts normalized events to Veriqa. Veriqa calls the adapter back for the operations of the
channel. Both directions are signed with the channel's transport secret (Standard Webhooks, scheme
`v1`). The wire contract is version 1 with the status `preview`.

The package is not part of the `Veriqa.Core.BaseChannels` metapackage. The standard Veriqa host
references it and always registers the HTTP channels of its configuration; an embedded host adds it
explicitly.

## Install

```bash
dotnet add package Veriqa.Core.ChannelAdapter.Http
```

The channel contour (`Veriqa.Core.ChannelAdapter`) arrives with this package — it is not installed
separately.

## Wiring

```csharp
authServer.AddChannelAdapters(adapters => adapters.AddHttpChannels());
```

Every section `Veriqa:Channels:{channel}` that carries an `Http` subsection becomes one channel under
the name of the section. A configuration without such sections registers nothing. The sections are
read and checked once, at start: a misconfigured HTTP channel stops the host with a message naming the
key to fix.

## Configuration

A minimal channel, in environment variables:

```bash
Veriqa__Channels__acme-chat__InboundVerification=signature
Veriqa__Channels__acme-chat__Http__BaseUrl=https://acme-adapter.internal/
Veriqa__Channels__acme-chat__Http__TransportSecret=whsec_<base64 of 24 to 64 random bytes>
```

| Key (under `Veriqa:Channels:{channel}`) | Required | Meaning |
|---|---|---|
| `Enabled` | no, `true` | Whether the channel is enabled, as for any channel |
| `InboundVerification` | yes | How the external adapter verifies the traffic of its platform, as for any channel |
| `Http:BaseUrl` | yes | Absolute `https` URL of the external adapter |
| `Http:AllowInsecureHttp` | no, `false` | Admits an `http` base URL for an isolated network; a warning is written at start |
| `Http:TransportSecret` | yes | Transport secret, `whsec_` + base64 of 24–64 bytes; unique per channel |
| `Http:PreviousTransportSecret` | no | Previous secret while a rotation is in progress; a warning is written at start |
| `Http:DeepLinkSchemes:{n}` | no | Deep-link schemes admitted beyond `https` (for example `viber`) |
| `Http:AdapterSettings` | no | Opaque string forwarded to the adapter, up to 8192 characters; the only key with a tenant level |
| `Http:Tenant` | no | The tenant that owns the registration and the only one it serves: its route, its transactions, its sign-in entries and its calls. Unset — the registration serves every tenant; set blank — the host does not start |
| `Http:Capabilities:{fact}` | no | Channel facts, one key per property of `ChannelCapabilities`; plain text only by default |
| `Http:Capabilities:PhoneNumber:Acquisition:{n}` | no, none | How the channel obtains the phone number of the user: `Automatic`, `OnRequest` or both, a list by index. Unset — the channel gives no number |
| `Http:Capabilities:PhoneNumber:Proof` | no, `NotProven` | `Proven` — a number from the channel is proven to belong to the user; the ceiling of what an event may state |
| `Http:Capabilities:CodeStart:LinkCarriesTransaction` | no, unset | Stated, it declares the start by a code: `false` — the deep link only opens the chat and the code is the only way to start; `true` — the link carries the transaction and the code is a fallback beside it. Unset — no start by a code |
| `Http:Capabilities:CodeStart:MessageTemplate` | no, unset | The message the user sends, `{code}` standing for the code exactly once; unset or blank — the user sends the code alone |
| `Http:Capabilities:SupportedCompletionBehaviorActions:{n}` | no, `None` | The actions after a sign-in the channel performs: `None`, `WelcomeMessage`, `Menu`, `Redirect`, a list by index. `None` is always among them |
| `Http:Capabilities:DefaultCompletionBehaviorAction` | no, `None` | The action applied in place of one the channel does not perform; one of the listed actions |
| `Http:DisplayName`, `Http:IconSvgPath` | no | Name and glyph of the channel button |
| `Http:ErrorMessageText`, `Http:StaleLinkReplyText`, `Http:UnaddressedReplyText`, `Http:UnmatchedCodeReplyText` | no | Natural Keys of the channel's own texts; the reply to a code no live sign-in answers to falls back to `StaleLinkReplyText` when unset |
| `Http:OperationTimeout`, `Http:DeepLinkTimeout`, `Http:RetryBudget` | no, `00:00:10` / `00:00:02` / `00:00:20` | Timeouts of the calls to the adapter and the budget of their retries, from 10 ms to one day |

The names of the built-in channels are reserved and cannot be used for an HTTP channel.

The names of `PhoneNumber:Acquisition` and `PhoneNumber:Proof` are matched without regard to case. The
host does not start when `Http:Capabilities:PhoneNumber` itself is a single value rather than a
section, when a value is outside those names, when `Acquisition` is a single value rather than a
list, when `Proof` is `Proven` with no `Acquisition`, or when `Acquisition` lists `OnRequest`
while `Http:Capabilities:SupportedMessageKinds` does not list `phone_number_request`.

## The start by a code

A channel whose link cannot carry the transaction, or whose platform does not always deliver it,
declares the start by a code in `Http:Capabilities:CodeStart`, in the fullness of `ChannelCodeStart`.
The core shows the code on its pages; the adapter posts the code the user sent with the event
`auth_start_by_code` (`code`, `identity`), and the wrapper hands it to the core as
`ChannelAuthStartByCodeResult`. `code` is the text as the user sent it without the wrapping of the
template: the core drops whitespace and `-`, checks the length and looks the code up across the whole
installation, whichever route the event came on, taking the tenant from the transaction it finds. The
adapter therefore posts the event on the route of the bot that received the message. Neither the code
nor the text is logged.

- The template is judged where the channel is shown, not at start.
- The host does not start when `Http:Capabilities:CodeStart` is a single value rather than a section,
  when `LinkCarriesTransaction` is not a boolean, or when `MessageTemplate` is stated without
  `LinkCarriesTransaction`.
- **Only a registration without an owner, in this version.** A registration that declares the start
  by a code and sets `Http:Tenant` stops the host at start: the code is taken from the store of the
  installation before the start compares the tenant of its transaction with the owner.
- An `auth_start_by_code` from a registration that declares no start by a code is not processed.
- With `LinkCarriesTransaction` = `true` the spare code block of the core and its setting
  `SpareStartCodeEnabled` apply as to any channel.

## The phone number

An HTTP channel gives the phone number of the user in the two ways a built-in channel does, by what
its registration declares:

- **On request** (`OnRequest`). `send-message` of the kind `phone_number_request` carries the member
  `phone_number_request`: `transaction_id`, `share_button_label`, `secondary_action` (`skip` or
  `cancel_sign_in`) and `secondary_button_label`. For every other kind the member is `null`. The
  adapter shows the two actions in the way of its platform and answers with events: `phone_shared`
  for the number, and for the secondary action either `phone_step_action` (`transaction_id`, `action`,
  `identity`) or `unaddressed` with the caption of the button in its optional `text`. An `action` the
  wrapper does not know makes the event unrelated.
- **Automatically** (`Automatic`). The adapter puts the number into `identity.phone_number` of its
  events; nothing is asked.

`identity.phone_number_verified: true` of any event is taken only from a registration that declares
`Proof` = `Proven`. From any other the wrapper lowers it to `false` and writes a warning; `false` and
an absent field are carried as they are. The number is E.164 with the leading plus and is never
repaired.

## After the sign-in

An HTTP channel performs the actions after a sign-in its registration lists in
`Http:Capabilities:SupportedCompletionBehaviorActions`. The welcome reaches the adapter as
`send-message` of the kind `plain_text`, the menu and the redirect as `send-message` of the kind
`action_buttons`. The names are matched without regard to case. The host does not start in four
cases: an entry or `DefaultCompletionBehaviorAction` is not one of the four names (a number is refused
too); `DefaultCompletionBehaviorAction` names an action the list does not; the list names
`WelcomeMessage` while `Http:Capabilities:SupportedMessageKinds` does not list `plain_text`; the list
names `Menu` or `Redirect` while `SupportedMessageKinds` does not list `action_buttons`.

## Links and buttons

`send-message` carries the content of two more kinds, each in a member of the same name that is `null`
for every other kind:

- `claim_form_link` — `url`, the address of the claim completion form with its token, and
  `button_label`, the caption of the button that opens it;
- `action_buttons` — `buttons`, never empty, in display order, each with a `label` and exactly one of
  `url` (a link) and `menu_item_value` (a menu item). The value is the callback payload of the
  core without its `vq_menu_` marker: the adapter builds the payload of the platform button itself.
  A message with a value longer than `HttpChannelWire.MaxMenuItemValueLength` is not sent: its press
  would come back as an unrelated event.

The address of the form and the address of a link button, which may carry a single-use link handle,
are secrets: the adapter does not log them.

A press of a menu item comes back as the event `menu_item_pressed` (`menu_item_value`,
`identity`). The wrapper hands it to the core as `ChannelMenuItemPressedResult`, and the core passes it
to the host's `IMenuItemPressHandler`. The `webhook-id` of the event becomes `PressId`; together with
the channel type it is the key by which the host discards a repeated delivery: the core does not drop
repeats. An empty value or one longer
than `HttpChannelWire.MaxMenuItemValueLength` (1024 characters) makes the event unrelated; the warning
names the user by a fingerprint alone and carries neither the value nor its length. The user and the
tenant of the press — the route the event came on — are stated by the external adapter, not by the
platform.

## Outbound calls

Every call to the adapter is a signed `POST` to `Http:BaseUrl`. `send-prompt`, `send-message` and
`report-outcome` carry an idempotency key in `webhook-id` and are retried on a transport failure (no
connection, a timeout, a 5xx, a 429) up to three times — after 1 s, 2 s and 4 s, or after the pause of
`Retry-After` — within `Http:RetryBudget`; `deep-link` and `health` are attempted once. Redirects are not
followed, and an answer over 1 MB is refused unread. The client of these calls does not inherit a
resilience handler the host puts on every `HttpClient`.

Two extension points of the host, both registered only when the host has not registered its own:

- `IHttpChannelEgressPolicy` — asked before every connection attempt with the tenant, the channel, the
  operation and the target; a refusal opens no connection. Shipped: allow everything.
- `IHttpChannelInboundEventIdSource` — the `webhook-id` of the inbound event a call was caused by, the
  source of the idempotency keys of `send-prompt` and `report-outcome`. Shipped: the header of the
  current request when it is the inbound route of the channel.

## Wire helpers

For a party that talks to the wrapper from .NET — a test harness, an adapter of its own — the package
exposes the names and the signature of the wire:

- `HttpChannelWire` — the wire version, the three signature headers, the values of `operation` and
  `event` (`phone_step_action`, `auth_start_by_code` and `menu_item_pressed` among them), the wire
  error codes and the limit of a menu item value, `MaxMenuItemValueLength`;
- `HttpChannelSignature.Sign` / `HttpChannelSignature.Verify` — the Standard Webhooks signature
  (`v1`, HMAC-SHA256 over `id.timestamp.body`) with one or two `whsec_` secrets, and its check within
  five minutes of the given moment.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
