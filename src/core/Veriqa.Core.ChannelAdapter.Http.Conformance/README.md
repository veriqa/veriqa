# Veriqa HTTP channel conformance suite

An executable the author of an external channel adapter — written in any language — runs against the
base URL of the adapter to check it against the HTTP channel wire contract of Veriqa, version 1
(status `preview`).

The well-formed calls of the suite go through the real HTTP channel wrapper of Veriqa
(`Veriqa.Core.ChannelAdapter.Http`), not through a second implementation of the protocol, so the
suite cannot drift from what Veriqa does. The calls the wrapper never makes — unsigned, with an unknown
field, variant or operation, of a foreign major version — are sent by a separate sender that takes a
body the wrapper produced and changes one thing in it. A receiver of events plays the core on the
webhook routes of the channel. Every answer and every event of the adapter is checked against the
machine-readable schema of the wire, which ships with the suite.

## Build and run

The suite ships as a container image, built from the root of the repository like the image of
Veriqa:

```bash
docker build -f src/core/Veriqa.Core.ChannelAdapter.Http.Conformance/Dockerfile -t veriqa-http-channel-conformance .
```

It is configured by environment variables (or command-line arguments) and started next to the adapter,
for example as a service of the compose file the adapter runs in:

```bash
mkdir -p out
docker run --rm --network acme \
  -e Conformance__ChannelType=acme-chat \
  -e Conformance__ChannelUserId=12345 \
  -e Conformance__ReportPath=/out/report.json \
  -e Veriqa__Channels__acme-chat__InboundVerification=signature \
  -e Veriqa__Channels__acme-chat__Http__BaseUrl=http://acme-adapter:8000/ \
  -e Veriqa__Channels__acme-chat__Http__AllowInsecureHttp=true \
  -e Veriqa__Channels__acme-chat__Http__TransportSecret=whsec_... \
  -v "$PWD/out:/out" \
  --user "$(id -u):$(id -g)" \
  veriqa-http-channel-conformance
```

The image runs as a non-root user, so the directory of the report must be writable by the user the
container runs as: `./out` is created by the current user of the host before the run (a directory
Docker creates for a mount belongs to `root`), and `--user` runs the container as that user. A report
that cannot be written is exit code `2`.

The exit code of the container is the exit code of the suite.

## Configuration

**The channel under test** is configured with exactly the keys Veriqa reads —
`Veriqa:Channels:{channel}:*`, including `InboundVerification` and the `Http:*` keys (see the README
of `Veriqa.Core.ChannelAdapter.Http`). The suite has no address or secret keys of its own: it checks
the configuration an operator gives Veriqa, and it refuses to run on a configuration Veriqa would
refuse to start with — the message of Veriqa is printed as it is, with exit code 2.

**The suite** is configured under `Conformance:*`:

| Key | Required | Meaning |
|---|---|---|
| `Conformance:ChannelType` | yes | Which registration `Veriqa:Channels:{channel}` is checked |
| `Conformance:Mode` | no, `calls` | `calls` / `interactive` / `automatic` |
| `Conformance:ChannelUserId` | yes | The user of the channel the calls outside events are addressed to |
| `Conformance:Tenants:{n}` | no, the implicit tenant only | Tenants of the run; `-` is the implicit tenant. An identifier contains no `:`. The calls run in the first one |
| `Conformance:ReportPath` | no | Path of the JSON report |
| `Conformance:EventTimeoutSeconds` | no, `60` (`interactive` — `300`) | How long the receiver waits for an event, a whole number of seconds |
| `Conformance:TenantCredentials:{tenant}` | no | Mode `automatic`: the identifier of the platform credential of each tenant at the stand (not the credential itself); `-` is the implicit tenant. Every tenant named here is listed in `Tenants` |
| `Conformance:PlatformStubUrl` | in `automatic` | Base address of the platform stub of the stand, an absolute `http` or `https` URL without a query or a fragment |
| `Conformance:PlatformCannotConfirm` | no, `false` | Mode `automatic`: the platform of the stand has a case in which it cannot carry a confirmation out (see `platform.cannot-continue`) |

Modes:

- `calls` — the calls alone; the CI gate of a third-party author;
- `interactive` — the calls and the receiver of events; the author plays the user on the platform;
- `automatic` — the calls, the receiver and the scenarios of the reference adapters; a stub of the
  platform plays the user and the platform.

The receiver of events listens in the modes `interactive` and `automatic` on the address of the
standard Kestrel configuration — `ASPNETCORE_URLS` or `ASPNETCORE_HTTP_PORTS`; the image listens on
port `8080`. The adapter is configured to post its events to the suite instead of Veriqa, on the paths
of Veriqa: `POST /api/channels/{channel}/webhook` and `POST /api/channels/{channel}/webhook/{tenant}`,
so only the base address of the core changes. An address that cannot be listened on is exit code `2`.

## Output

One line per scenario on the standard output:

```text
PASS health.success
FAIL auth.wrong-secret: a call with an invalid signature was accepted
N/A <id>: <reason>
```

In the mode `interactive` a flow also prints a line that asks the author to act on the platform — the
deep link to open and the button to press:

```text
ACTION flow.confirm: as the user of the implicit tenant, open the link and press Confirm: https://...
ACTION flow.code-start: as the user of the implicit tenant, send the message "40271938" to the bot of the link: https://...
ACTION flow.menu-item-press: as the user of the implicit tenant, press the button "Press the menu item" of the message "Veriqa conformance suite: a test message with buttons." the bot has just sent
```

Warnings of the wrapper go to the standard error. Neither stream carries the transport secret, the
settings string or a body of the wire.

The JSON report, when `Conformance:ReportPath` is set:

```json
{
  "wire_version": "1.0",
  "mode": "calls",
  "channel_type": "acme-chat",
  "outcome": "passed",
  "scenarios": [
    { "id": "health.success", "status": "passed", "reason": null }
  ]
}
```

`outcome` is `passed` or `failed`; `status` is `passed`, `failed` or `not_applicable`, and `reason` is
set for `failed` and `not_applicable`. The identifiers of the scenarios are stable. A scenario outside
the mode of the run, a scenario of a way of obtaining the phone number the registration does not
declare (see [The phone number](#the-phone-number)), a scenario of a kind of message it does not
declare (see [Links and buttons](#links-and-buttons)) and a scenario left out by the declaration of the
start by a code (see [The start by a code](#the-start-by-a-code)) is not in the output or in the report
at all.

Exit codes:

| Code | Meaning |
|---|---|
| `0` | No scenario failed |
| `1` | At least one scenario failed |
| `2` | The run could not be carried out: a configuration of the suite or of the channel, an address the receiver cannot listen on, or a missing seam of the suite; the message names what to fix. Also a run interrupted by `SIGINT` or `SIGTERM`: it stops at the first signal with no report written, since the scenarios that ran are not the verdict of a whole run; a second signal ends the process at once |

An adapter that cannot be reached is an answer about conformance, not a failure of the suite: its
scenarios fail with the reason "no connection", and the code is `1`. So is a platform stub that does
not answer: the scenario that called it fails.

## Scenarios

Every answer and every event of the adapter is also checked against the schema of the wire.

### The phone number

Two scenarios check the two ways an HTTP channel obtains the phone number of a user, and each joins a
run only when the registration declares its way in `Http:Capabilities:PhoneNumber:Acquisition`:

| Id | Joins the run when the registration declares |
|---|---|
| `send-message.phone-number-request` | `OnRequest` |
| `identity.phone-automatic` | `Automatic` |

**A scenario that is not in the report means that its way is not declared** — it is not a
`not_applicable` line. The report of a registration that declares no phone number is the report of the
other scenarios alone. A stand that relies on one of the two therefore requires its identifier in the
report, as the CI gates of the reference adapters do.

### The start by a code

The declaration of the start by a code — `Http:Capabilities:CodeStart:LinkCarriesTransaction` stated —
changes which scenarios of the receiver join a run:

| Registration | `flow.code-start` | `flow.confirm`, `flow.decline`, `identity.phone-automatic` |
|---|---|---|
| no `CodeStart` declared | not in the run | in the run |
| `LinkCarriesTransaction` = `true` — the link carries the transaction, the code is a fallback | in the run | in the run |
| `LinkCarriesTransaction` = `false` — the link only opens the chat | in the run | **not in the run** |

A link that only opens the chat cannot start a transaction, so the scenarios that start one by
following its link are left out of the run of such a registration — they are not in the output or in
the report, as with a scenario of an undeclared way of obtaining the phone number. The suite therefore
does not play the confirmation and the refusal that follow a start by a code: the suite plays the
core, the code is one it makes up, and no transaction stands behind it.

### Links and buttons

The scenarios of the two kinds of message with content of their own join a run only when
`Http:Capabilities:SupportedMessageKinds` lists the kind:

| Id | Joins the run when the registration declares |
|---|---|
| `send-message.claim-form-link` | `claim_form_link` |
| `send-message.action-buttons`, `menu-item-pressed.*` | `action_buttons` |
| `flow.menu-item-press` | `action_buttons`, in the mode `interactive` alone |

As with the phone number, a scenario missing from the report means that its kind is not declared. The
platform stub cannot press a button, so the mode `automatic` plays no press.

### Calls through the wrapper

Each runs in every mode, in the scope of the first tenant of `Conformance:Tenants`.

| Id | Check |
|---|---|
| `health.success` | `health` succeeds and the adapter reports itself healthy |
| `deep-link.success` | `deep-link` succeeds and the wrapper admits its `url` |
| `deep-link.stable` | two `deep-link` calls with the same request (one transaction, one `purpose`) return equal `url` |
| `send-prompt.success` | `send-prompt` succeeds; a `message` of `null` is a success too |
| `send-message.success` | `send-message` succeeds; the kind is `plain_text` when the registration declares it, otherwise the first declared kind in ordinal order other than `phone_number_request`, `claim_form_link` and `action_buttons`, and `plain_text` when there is no other |
| `send-message.phone-number-request` | only for a registration that declares `OnRequest`: `send-message` of the kind `phone_number_request` to the user `Conformance:ChannelUserId`, with the content of the request — the two captions, the secondary action `skip` and a transaction — succeeds. The answer of the user is not waited for |
| `send-message.claim-form-link` | only for a registration that declares `claim_form_link`: `send-message` of that kind to the user `Conformance:ChannelUserId`, with a caption and a synthetic address of the form on `example.com`, succeeds |
| `send-message.action-buttons` | only for a registration that declares `action_buttons`: `send-message` of that kind to the user `Conformance:ChannelUserId`, with a link button on `example.com` and a menu item button with a random value, succeeds. The press is not waited for |
| `report-outcome.success` | `report-outcome` on the transaction of `send-prompt.success`, with its `prompt_message`, succeeds |
| `send-prompt.repeat-same-key` | two `send-prompt` calls caused by one inbound event — one `webhook-id` — both succeed with equal results |
| `report-outcome.repeat-same-key` | two `report-outcome` calls caused by one inbound event both succeed with equal results; in the mode `automatic` the platform log holds one action when the outcome is shown, none when it is not |
| `auth.wrong-secret` | a `send-prompt` signed with a random secret the adapter does not know is answered `403` without an envelope |
| `auth.timestamp-past` | a `send-prompt` signed ten minutes in the past is answered `403` without an envelope |
| `auth.timestamp-future` | a `send-prompt` signed ten minutes in the future is answered `403` without an envelope |

"Without an envelope" means that the body of the answer is not a JSON object with `result` or `error`.

### Calls the wrapper does not make

Each runs in every mode. The sender takes the body a scenario above recorded, changes it as the table says,
signs it with the current transport secret under a new `webhook-id` and posts it to `Http:BaseUrl`.
The one body it builds itself is the event envelope of `wire.reflected-event`. When the source scenario
made no request to the adapter, the scenario fails with the reason "no source call".

| Id | Body | Expected |
|---|---|---|
| `auth.unsigned` | the body of `health.success` without the signature headers | `403` without an envelope |
| `send-message.repeat-same-key` | the body of `send-message.success` twice under one new `webhook-id` | both `200` with `result`, the results equal |
| `deep-link.without-rendered` | the body of `deep-link.success` with `payload.rendered` = `null` — what Veriqa sends when it has no prefill text for the link — and a new `payload.transaction_id`, so the adapter cannot answer with the link it keeps for `deep-link.success` | `200` with `result` |
| `wire.unknown-field` | the body of `health.success` with an unknown field on the top level and in `payload` | `200` with `result` |
| `wire.unknown-variant` | the body of `send-prompt.success` with an unknown `prompt.variant` | `200` with `result` — a prompt without details |
| `wire.unknown-operation` | the body of `health.success` with an unknown `operation` | `200`, `error.code` = `unsupported_operation` |
| `wire.foreign-major-version` | the body of `health.success` with `version` = `2.0` | `200`, `error.code` = `unsupported_wire_version` |
| `wire.reflected-event` | a signed `auth_start` event envelope | anything but `200` with `result`; `200` with `error` passes |

In the mode `automatic` the calls that must have no effect — `auth.*`, `wire.unknown-operation`,
`wire.foreign-major-version`, `wire.reflected-event` — reset the platform stub before the call and
require an empty platform log after it; `send-message.repeat-same-key` requires exactly one `sent`
action after it; `report-outcome.repeat-same-key` requires exactly one `sent` or `edited` action after
both calls when the outcome is shown, and none when it is not (`shown = false` — the channel showed
nothing).

### Menu item presses handed to the wrapper

Each runs in every mode, only for a registration that declares `action_buttons`, in the scope of the
owner of the registration (`Http:Tenant`), or of the first tenant of the run when it has none. The
suite builds a `menu_item_pressed` envelope and hands it to the wrapper itself — to the signature check
and the reading the webhook route of Veriqa makes, with no HTTP and no receiver — so a refused envelope
never reaches `events.all-valid`. These scenarios check how the wrapper protects the event, and their
outcome does not depend on the adapter. On the route of Veriqa a refusal is the answer `403`.

| Id | Envelope | Expected |
|---|---|---|
| `menu-item-pressed.signed` | signed with the transport secret | accepted and read as the press, with its value and with the `webhook-id` as `PressId` |
| `menu-item-pressed.unsigned` | the same, without the signature headers | refused |
| `menu-item-pressed.wrong-secret` | the same, signed with a random secret | refused |
| `menu-item-pressed.timestamp-past` | the same, signed ten minutes in the past | refused |
| `menu-item-pressed.foreign-route` | the same, rightly signed, on the route of a tenant other than the owner | refused; `not_applicable` for a registration without an owner |
| `menu-item-pressed.value-bounds` | signed, with an empty value and with a value of 1025 characters | both read as unrelated |

### Receiver of events

Modes `interactive` and `automatic`. The receiver checks the signature of every event through the
wrapper — a refusal is answered `403` — reads the event through the wrapper, checks it against the
schema and answers `200`, as Veriqa does, whatever came of the reading. On `auth_start` of a
transaction of a flow it puts the prompt to the user of the event **before** it answers the event:
the adapter must take calls of the core while its own request to the core is open.

| Id | Check |
|---|---|
| `flow.confirm` | for every tenant of `Conformance:Tenants`: `deep-link` of a new transaction in the scope of the tenant; the user follows the link and confirms (`automatic` — the stub, `interactive` — the author, by the `ACTION` line); within `EventTimeoutSeconds` `auth_start` of the transaction comes on the route of that tenant; before it is answered, `send-prompt` to the user of the event, keyed by the `webhook-id` of the event, succeeds; `auth_confirm` of the transaction comes on the same route; `report-outcome` (`confirmed`, the `prompt_message` of the prompt, the `inbound_token` of the event) succeeds |
| `flow.decline` | the same for the first tenant, with the answer `decline`, the event `auth_decline` and the outcome `declined` |
| `flow.code-start` | only for a registration that declares the start by a code: the suite makes up a code of 8 random digits and builds the message by `Http:Capabilities:CodeStart:MessageTemplate` (unstated — the code alone); the bot is named by the `deep-link` of a new transaction in the scope of the first tenant. The user sends the message to that bot (`automatic` — the stub, by `send-text`; `interactive` — the author, by the `ACTION` line); within `EventTimeoutSeconds` `auth_start_by_code` comes whose `code`, with whitespace and `-` dropped, is the digits sent, and whose `identity.channel_user_id` is `Conformance:ChannelUserId`. `unaddressed` from that user with the text sent, in its place, fails it; any other event that names no transaction — a bare `/start` of the author who opened the chat, say — does not end the wait. A declared template the core cannot use (blank, longer than 64 characters, `{code}` not exactly once) fails it before anything is sent. The reason of a wrong code names the lengths, never the codes |
| `flow.menu-item-press` | mode `interactive` alone, only for a registration that declares `action_buttons`, for the first tenant: the suite sends the user the message of `send-message.action-buttons` with a new random value of the item, and the `ACTION` line asks the author to press its menu item button. Within `EventTimeoutSeconds` `menu_item_pressed` comes on the route of that tenant, with that value as its `menu_item_value` and `Conformance:ChannelUserId` as its `identity.channel_user_id`, and the wrapper reads its `webhook-id` as `PressId`. A press with another value — a button of an earlier run — does not end the wait |
| `identity.phone-automatic` | only for a registration that declares `Automatic`: every `auth_start` of the transactions of `flow.confirm` and `flow.decline` carries `identity.phone_number` in E.164 with the leading plus; an `auth_start` without the number or with one in another spelling fails it, and so does a run in which no such `auth_start` came. It runs after the flows |
| `events.all-valid` | every event that came passed the signature, the reading, the schema, the rule of the declared proof and the rule of the route of its transaction; `not_applicable` when no event came. It runs last |

Two rules of `events.all-valid` concern the phone number:

- **the declared proof.** An event whose `identity.phone_number_verified` is `true` fails when the
  registration does not declare `Http:Capabilities:PhoneNumber:Proof` = `Proven`: Veriqa takes such a
  number as not proven, so the adapter states what the operator did not declare. The reason names the
  event and its route; the event also fails the flow of its transaction, like any other invalid event;
- **the format of `phone_shared`.** A `phone_shared` whose `phone_number` is not E.164 with the leading
  plus fails the reading. `send-message.phone-number-request` asks the user for the number and does not
  wait for the answer, so the event is checked when it comes while the receiver listens — in the mode
  `interactive`, when the author presses the share button; a platform stub does not answer the request.

The route of a transaction is the route of the tenant its `deep-link` was made for: the path without a
tenant segment for the implicit tenant, `…/webhook/{tenant}` otherwise. `auth_start_by_code` names no
transaction, so the rule does not apply to it: the adapter posts it on the route of the bot that got the
message, and Veriqa finds the code among the codes of the whole deployment.

### Scenarios of the reference adapters

Mode `automatic` alone. Each starts with a reset of the platform stub. A platform action is tied to a
call by the `chat_id` and `message_id` the call returned, so an adapter that returns `message` = `null`
fails these scenarios.

| Id | Applies | Check |
|---|---|---|
| `platform.single-message-on-repeat` | always | two `send-prompt` calls under one key → exactly one `sent` action in the platform log, and it carries the message the first call returned |
| `platform.cannot-continue` | `Conformance:PlatformCannotConfirm` = `true`, otherwise `not_applicable` | `scenario` with `platform_cannot_confirm: true`, then `send-prompt` → the adapter answers `channel_cannot_continue`, the wrapper refuses with it, and no new `sent` action |
| `platform.credentials-by-tenant` | at least two tenants in `Conformance:TenantCredentials`, otherwise `not_applicable` | `send-prompt` in the scope of each of them; the `sent` action with the message of the call carries the `credential` of that tenant |

**Applicability of `platform.cannot-continue` is declared by the stand** with
`Conformance:PlatformCannotConfirm`. A platform with no such case — nothing that makes it refuse to
deliver a prompt — does not implement the switch of `scenario`, and the scenario is `not_applicable`.
A platform that has one — for WhatsApp, a closed customer service window — declares it, and the adapter
must answer the prompt with `channel_cannot_continue`.

## Platform stub contract

In the mode `automatic` the suite drives the stub of the platform the reference adapter talks to: it
plays the user and reports what the adapter did on the platform. HTTP and JSON in `snake_case`, no
signature — a service channel of a stand in an isolated network. The base address is
`Conformance:PlatformStubUrl`; the paths below are relative to it. An answer outside `2xx` fails the
scenario, and the reason names the call.

| Request | Body | Answer | Meaning |
|---|---|---|---|
| `POST /conformance/reset` | — | `204` | clear the platform log and the switches |
| `POST /conformance/scenario` | `{"platform_cannot_confirm": bool}` | `204` | with `true`, the platform refuses to deliver a prompt until the next reset, as it would in real life; the adapter answers the prompt with `channel_cannot_continue` |
| `POST /conformance/follow-link` | `{"url": string, "answer": "confirm"\|"decline"}` | `202` | the user `Conformance:ChannelUserId` opens `url` on the platform and, when the prompt comes, presses the button `answer` |
| `POST /conformance/send-text` | `{"url": string, "text": string}` | `202` | the user `Conformance:ChannelUserId` sends the plain text message `text` to the bot the link `url` (an answer of `deep-link`) names. The stub only chooses the bot by the link and does not open it: a transaction the link may carry is not used. Called only in a run of a registration that declares the start by a code |
| `GET /conformance/platform-log` | — | `200` `{"actions":[{"kind":"sent"\|"edited","credential":string,"chat_id":string,"message_id":string}]}` | what the adapter did on the platform since the last reset, in order |

- `credential` is the identifier of the credential the request of the adapter came to the platform
  with, never the secret itself. Its value is the one of `Conformance:TenantCredentials:{tenant}` of
  the stand.
- The adapter of the stand posts its events to the address of the suite (port `8080` of the image) and
  knows the working transport secret: that is the configuration of the stand.

Keys of the mode `automatic`: `Conformance:PlatformStubUrl` (required, otherwise exit code `2`),
`Conformance:TenantCredentials:{tenant}`, `Conformance:PlatformCannotConfirm` (default `false`).

## Wire schema

The machine-readable schema of the wire, JSON Schema draft 2020-12, is
`Schema/http-channel-wire-v1.schema.json` in this project and `/schema/http-channel-wire-v1.schema.json`
in the image:

```bash
docker run --rm --entrypoint cat veriqa-http-channel-conformance /schema/http-channel-wire-v1.schema.json
```

A document is checked against one of the entry points in its `$defs`: `call_envelope` (a call of
Veriqa to the adapter), `send_prompt_response`, `send_message_response`, `report_outcome_response`,
`deep_link_response`, `health_response` (the answers of the adapter) and `event_envelope` (an event the
adapter posts to Veriqa). The payload of `send-message` carries three members, each an object or
`null`: `phone_number_request` with `transaction_id`, `share_button_label`, `secondary_action` and
`secondary_button_label`; `claim_form_link` with `url` and `button_label`; `action_buttons` with a
non-empty `buttons`, each button with a `label` and a string in exactly one of `url` and `menu_item_value`.
The schema does not tie a member to `kind`, Veriqa does. The event envelope knows the event
`phone_step_action` (`transaction_id`, `action`, `identity`), the event `auth_start_by_code` (`code` — a
string, `identity`; no transaction), the event `menu_item_pressed` (`menu_item_value` — a string,
`identity`; no transaction) and the optional `text` of `unaddressed`. Unknown fields are allowed everywhere, and the enumerated values of the wire
are open: a receiver that does not know a value behaves as the wire contract declares.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root.
