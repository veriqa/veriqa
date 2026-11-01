# WhatsApp through Baileys over the HTTP channel — reference external adapter in Node

A **working** adapter: it has been run end to end on a real WhatsApp number — sign-in confirmed and declined
through a poll, the receipt delivered, the session kept across a restart (see
[Live run on a real number](#live-run-on-a-real-number)). Use it to debug WhatsApp scenarios, for a small
team or for your own use.

## Warning: an unofficial client, the number may be banned

**This adapter drives WhatsApp through [Baileys](https://github.com/WhiskeySockets/Baileys), an unofficial
WhatsApp Web client. It is not affiliated with, authorized or endorsed by WhatsApp or Meta. Using it may
break the terms of service of WhatsApp, and WhatsApp may ban the number it is linked to — that risk is
yours as the operator. Link a separate number of its own, never a personal or business number you cannot
afford to lose.** For a supported path to WhatsApp at scale use the built-in `whatsapp` channel of Veriqa
(WhatsApp Business Platform or Twilio) or the reference adapter through Twilio,
`samples/node/custom-channel-whatsapp-twilio/`.

## Licenses

- The code of this sample is **MIT**, like everything under `samples/`: copy it into your own code.
- `baileys` is **MIT**.
- `libsignal`, a direct dependency of `baileys`, is **GPL-3.0**. The image the `Dockerfile` builds therefore
  contains GPL-3.0 code. Whoever distributes that image — or a product built on it — carries the
  obligations of the GPL-3.0; that is you as the operator, not Veriqa. Running it for yourself distributes
  nothing. Check the licenses of the whole dependency tree with a tool of your choice before you ship.

## What this sample is

A channel adapter that runs as a service of its own, next to Veriqa, and plugs WhatsApp in through the
**HTTP channel** of Veriqa: configuration only, no code in Veriqa and no rebuild of it. The adapter speaks
the HTTP channel wire (version 1, status `preview`) to Veriqa and WhatsApp Web to WhatsApp: it is linked to
the phone of each of its numbers as a companion device, the way WhatsApp Web in a browser is.

The code is split like the other reference adapters: `src/core/` is the side of Veriqa — the
[Standard Webhooks](https://www.standardwebhooks.com/) signature, the envelopes, the idempotency keys, the
tenant of a transaction and the delivery of events — and knows nothing of WhatsApp; `src/whatsapp/` is the
side of the platform. The seam between them is `src/core/seam.js`. The side of the platform talks to
WhatsApp through a port with two implementations: `baileys`, the real one, and `stub`, which the CI gate
uses in place of WhatsApp Web (see [Conformance suite](#conformance-suite)).

| Path | What it is |
|---|---|
| `src/` | The adapter: `node src/main.js`, `node:http`, state in one SQLite file (`node:sqlite`) and one session directory per number |
| `package.json` | Direct dependencies, pinned: `baileys` (WhatsApp Web), `standardwebhooks` |
| `Dockerfile` | Image of the adapter on `node:24-slim`, non-root, data volume `/data` |
| `compose.yaml`, `.env.example` | Veriqa and the adapter side by side |
| `ci/` | The CI gate: a stand-in WhatsApp, the stand, the conformance suite run and the own checks |

The sample ships no `package-lock.json`: `package.json` pins the direct dependencies to exact versions, and
the transitive ones are resolved when the image is built. Commit a lock file of your own when you take the
code over. `baileys` is pinned to a release candidate, `7.0.0-rc14`: WhatsApp changes its protocol, the
library follows it, and a newer version may be needed — test it on your number before you upgrade.

## Run it next to Veriqa

```bash
cp .env.example .env    # fill in the transport secret and the WhatsApp number
docker compose up
```

`compose.yaml` builds Veriqa from the root of this repository and the adapter from this folder. Veriqa
runs in the **Development** environment with the in-memory OpenIddict store and the development
certificates OpenIddict makes for itself: a demonstration, not a deployment — nothing of Veriqa survives a
restart. It listens on `http://127.0.0.1:8080`; the state of the channel is at
`http://127.0.0.1:8080/health/channels`, where `data.whatsapp-baileys` reads `healthy` once the number is
paired (see [Pairing](#pairing-and-the-session-state)). The adapter publishes no port: only Veriqa calls it.

An unfilled `.env` does not start: the adapter names the variable it could not use (never its value) and
exits with code `2`, and Veriqa refuses a transport secret that is not `whsec_…`. A port the adapter cannot
listen on and a data or sessions directory it cannot use stop the start the same way. On `SIGTERM` or
`SIGINT` the adapter closes its WhatsApp sessions, lets the requests in progress end (for up to 8 seconds),
closes its store and exits.

## Configuration of the adapter

Environment variables, read once at start.

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `VERIQA_URL` | yes | — | Base address of Veriqa; events go to `<VERIQA_URL>/api/channels/<type>/webhook[/<tenant>]` |
| `VERIQA_CHANNEL_TYPE` | yes | — | Channel type of the registration in Veriqa, `whatsapp-baileys`; a call for another type is refused with `channel_not_available` |
| `VERIQA_TRANSPORT_SECRET` | yes | — | Transport secret, `whsec_` + base64 of 24–64 bytes; the same value as `Http:TransportSecret` in Veriqa |
| `VERIQA_PREVIOUS_TRANSPORT_SECRET` | no | — | The previous secret while one is rotated (see [Signature](#signature)) |
| `ADAPTER_PORT` | no | `8000` | HTTP port, 1–65535: the calls of Veriqa (`POST /`) |
| `ADAPTER_DATA_DIR` | no | `/data` | Directory of `adapter.sqlite3` — idempotency keys, tenants and deep links of the transactions, the polls of the prompts; mount a volume here, writable by the user of the process |
| `ADAPTER_IDEMPOTENCY_TTL_SECONDS` | no | `86400` | Retention of the idempotency keys, the tenants, the deep links and the polls; below `1800` the adapter does not start |
| `ADAPTER_LOG_LEVEL` | no | `INFO` | `DEBUG`, `INFO`, `WARNING` or `ERROR` |
| `WHATSAPP_PLATFORM` | no | `baileys` | `baileys` — WhatsApp Web; `stub` — the platform stub of the CI stand, never for a real run |
| `WHATSAPP_STUB_URL` | in `stub` | — | Base address of the platform stub; not read in `baileys` |
| `WHATSAPP_SESSIONS_DIR` | no | `<ADAPTER_DATA_DIR>/sessions` | Directory of the session state of every number; made with access for the owner alone |
| `WHATSAPP_NUMBER` | see below | — | The default record: its WhatsApp number in E.164, as WhatsApp addresses it (see [Pairing](#pairing-and-the-session-state)) |
| `WHATSAPP_LABEL` | no | `default` | Label of the default record |
| `WHATSAPP_ACCOUNT_<n>_TENANT`, `_LABEL`, `_NUMBER` | see below | — | The record of a tenant, `n` = 1, 2, …; all three are required |

At least one record is required. A label is 1 to 64 lowercase letters, digits, `-` and `_`, starting with
a letter or a digit; it names the record in the log and its session directory, and it is not a secret. No
two records share a tenant, a label or a number. Start with `node src/main.js` from this folder, or with the
image; outside the image point `ADAPTER_DATA_DIR` at a directory outside the repository — it holds the keys
of the linked devices.

In Veriqa the channel is the section `Veriqa:Channels:whatsapp-baileys`; `compose.yaml` sets it in
`environment:` — a key with a dash in the channel type cannot be exported from a shell:

| Key under `Veriqa:Channels:whatsapp-baileys` | Value in `compose.yaml` |
|---|---|
| `InboundVerification` | `outbound_fetch` — see [Inbound verification](#inbound-verification-outbound_fetch) |
| `Http:BaseUrl` | `http://adapter:8000/` — the adapter by its service name |
| `Http:AllowInsecureHttp` | `true` — the network of the compose file is private; Veriqa writes a warning at start |
| `Http:TransportSecret` | `VERIQA_TRANSPORT_SECRET` of `.env` |
| `Http:AdapterSettings` | `{"prefill_text": "Send this message to sign in."}` |
| `Http:Capabilities:*` | `SupportsInChannelConfirmation`, `DeliversOutcomeNotice`, `RendersEmoji` = `true`; `SupportsMessageUpdate`, `ProvidesRecipientLocale` = `false`; `SupportedMessageKinds:0` = `plain_text` — the facts of the built-in `whatsapp` channel |

The log never carries the transport secret, `adapter_settings`, a body of the wire, a phone number (only a
short fingerprint of it), a pairing code or the session state. Baileys gets a logger that drops every line,
and in the `baileys` mode the console of the process is silenced: `libsignal` prints its session records,
keys included, to it.

### Pairing and the session state

A record without a session state is paired by a **pairing code** for its own number — no QR code, which
would have to go to the log. The adapter writes the code to a file next to the session, readable by its
user alone, and logs only the path of the file:

1. Start the adapter: `docker compose up`. The log says that record `default` is not paired and names the
   file `/data/sessions/default.pairing-code`.
2. Read the code: `docker compose exec adapter cat /data/sessions/default.pairing-code`.
3. On the phone of `WHATSAPP_NUMBER` open WhatsApp → *Settings* → *Linked devices* → *Link a device* →
   *Link with phone number instead*, and enter the code. A code runs out after a while; the adapter then
   writes a new one to the same file.
4. The log says that record `default` is connected, and the file of the code is gone.

The session state lives in `/data/sessions/<label>/`, on the volume `adapter-data`: it survives a restart
of the container, and `docker compose down -v` deletes it. It is written by `useMultiFileAuthState` of
Baileys, and the library itself warns against that store for production: it keeps the state in many small
files and does a lot of disk I/O — a production adapter keeps it in a database of its own. The files are
the keys of the linked device: whoever holds them acts as that number, so protect the volume like a secret.

The adapter checks that the paired number is the number of the record. When the phone unlinks the device
(`loggedOut`), or the session belongs to another number, the record reads **not paired**, the adapter
stops reconnecting it and logs its label. The adapter never deletes a session state by itself; to pair
again, delete it and restart:

```bash
docker compose exec adapter rm -rf /data/sessions/default
docker compose restart adapter
```

The comparison is digit for digit: the number of a record is the number **as WhatsApp addresses it**, the
user part of its WhatsApp Web address. For most numbers that is the E.164 number without `+`, but in some
countries WhatsApp addresses a number with digits that differ from its E.164 form. Such a number reads as
paired with another number right after its code is entered. The session is valid then, and nothing has to
be paired again: read the form WhatsApp uses from the session, put it into the number of the record with
`+` in front, and restart the adapter.

```bash
docker compose exec adapter node -p "require('/data/sessions/default/creds.json').me.id.split(/[:@]/)[0]"
```

Any other drop of the connection is reconnected with a pause that doubles from 1 s up to 60 s.

### The text of a deep link

A `deep-link` call of Veriqa carries `rendered.text`: the message Veriqa rendered for the link from the
`deeplink-prefill` template of the channel type `whatsapp-baileys` and the purpose of the link, in the language
of the transaction. The adapter puts that text into the link. In a working configuration the text is always
there: the template has a root wording for any channel type without one of its own. To change the text of the
link, set the template for this channel type under
`Veriqa:MessageTemplates:deeplink-prefill:BySurface:in-channel:ByChannel:whatsapp-baileys`; `prefill_text` does not
change it. A confirmation has its own wording: set it under
`Veriqa:MessageTemplates:deeplink-prefill:ByType:confirmation:BySurface:in-channel:ByChannel:whatsapp-baileys`.

[`prefill_text`](#adapter_settings) applies only when `rendered` is `null` or absent: Veriqa sends `null` when it
cannot render a plain-text wording for the link, and an earlier Veriqa does not send the field at all.

Either way the message carries the sign-in code `auth_<transaction>`: the adapter finds the transaction by it
when the user sends the message. When the text from Veriqa lacks the code, the adapter adds it on a last line
of its own: `https://wa.me/<number digits>?text=<rendered.text>%0Aauth_<transaction>`. The wording Veriqa ships
has the `{code}` slot only for its built-in `whatsapp` channel, so for `whatsapp-baileys` the adapter adds the
code unless your own template for this channel type states the slot.

The adapter keeps the link of a transaction per purpose: the text of the link may differ by purpose.

### `adapter_settings`

Veriqa forwards `Http:AdapterSettings` to the adapter verbatim with every call and never reads it. This
adapter reads a JSON object with one key:

```json
{"prefill_text": "Send this message to sign in to Acme."}
```

`prefill_text` is the fallback opening line of the message a deep link puts into WhatsApp, for a call that
carries no text from Veriqa; in a working configuration, every call carries it (see
[The text of a deep link](#the-text-of-a-deep-link)). It goes above the sign-in code
`auth_<transaction>`: `https://wa.me/<number digits>?text=<prefill_text>%0Aauth_<transaction>`. A string of
up to 200 characters; a missing setting, one that is not JSON, one without the key or with a longer line
reads as the default `Send this message to sign in.`, with a warning in the log that does not carry the
string. The key has a tenant level in Veriqa: the value of a tenant replaces the value of the channel whole.

### Credentials per tenant

A call of Veriqa names its tenant, and the adapter answers it from the number of that tenant. The record
of the tenant wins; a tenant without a record of its own — and the implicit tenant of a self-hosted
installation — uses the default record. With neither, the call is refused: `channel_not_available`, or
`deep_link_generation_failed` for a deep link. Every record has a WhatsApp Web session of its own and is
paired on its own.

```yaml
# in the environment of the adapter
WHATSAPP_NUMBER: ${WHATSAPP_NUMBER}              # the default record, label "default"
WHATSAPP_ACCOUNT_1_TENANT: shop-a
WHATSAPP_ACCOUNT_1_LABEL: shop-a
WHATSAPP_ACCOUNT_1_NUMBER: ${SHOP_A_WHATSAPP_NUMBER}
```

The first `deep-link` or `send-prompt` call of a transaction records the tenant it came with; the events
of that transaction go to the route of that tenant. An event of no known transaction goes to the route of
the tenant of the record whose session it came in on. `health` is `healthy` when the session of every
record is open and paired with the number of the record; otherwise its `details` name each record and its
state: `ok`, `not paired` (no session state, or logged out) or `not connected`. A record that is not `ok`
refuses `send-prompt`, `send-message` and `report-outcome` with `message_send_failed`; its deep link still
works — a link needs no session.

## Inbound verification: `outbound_fetch`

In Veriqa the channel is registered with **`InboundVerification = outbound_fetch`**. No request of the
platform ever comes in: WhatsApp has no webhook for a WhatsApp Web client, and the adapter receives the
messages of its numbers over the outbound WebSocket sessions it opens itself. There is no inbound entry to
protect, so the adapter publishes nothing but the input of the calls of Veriqa, `POST /`, which carries the
signature of the transport.

## The prompt: a poll

WhatsApp Web has no buttons for a client like this one — Baileys cannot send them — but it has polls. A
prompt of Veriqa is therefore a **poll with two options and a single choice**: the question is the text of
the prompt Veriqa rendered, the options are its confirm and decline labels. The answer is the vote. An
answer typed as text ("reply 1 or 2") was not taken: it is not tied to the message of the question.

- The poll is kept in the store before it goes out, with its transaction, its recipient and the option of
  each answer: a vote names the poll and the option, nothing else. The adapter decrypts the vote itself,
  with a secret it made for that poll.
- The **first** vote of the recipient answers: `auth_confirm` or `auth_decline`. A vote taken back, a
  changed or second vote, and a vote of anyone else answer nothing. A vote whose event did not reach Veriqa
  answers nothing either, and the next vote of the recipient is taken in its place.
- Equal confirm and decline labels fail the prompt with `message_send_failed`: a poll with two equal
  options cannot be answered unambiguously.
- `send-message` sends a text message and takes the kind `plain_text` alone; `report-outcome` always sends
  the status text as a new message — WhatsApp does not edit a delivered message, as the built-in channel
  says of it, so the poll stays.

The adapter never answers `channel_cannot_continue`. That code is for a platform that cannot carry the
confirmation out for the user; on WhatsApp Business it is the closed 24-hour customer service window, but
a personal number linked through WhatsApp Web has no such window, and no other case of it is known. A
failure to send is `message_send_failed`. The conformance suite therefore reports its scenario
`platform.cannot-continue` as not applicable for this adapter.

## Which messages reach Veriqa

The adapter decides it itself; nothing else leaves the adapter.

| Inbound message | Event |
|---|---|
| A text of a personal chat with `auth_<transaction>` anywhere in it — the prefilled message of a deep link | `auth_start` |
| The first vote of the recipient of a kept poll for one of its options | `auth_confirm` / `auth_decline` |
| Anything else: group chats, the own messages of the number, a text without the code, a vote on an unknown or expired poll, a vote taken back, a repeated or changed vote, a vote of anyone but the recipient | none |

The user of an event is the sender of the message: `channel_user_id` and `phone_number` are its number in
E.164, the display name is its WhatsApp profile name (or the number), `is_bot` is `false`, and
`raw_metadata` carries `whatsapp_message_id` and `message_type` (`text` or `poll_vote`) — no personal data
beyond the number in the identity. This differs from the built-in WhatsApp channel of Veriqa, which knows the
user by the business-scoped user ID (BSUID): the sample keys the user by the number, so a message without a
number is dropped. The `webhook-id` of an event is derived from the label of the record and
the WhatsApp id of the message, so a repeated delivery of one message reaches Veriqa under one id.

WhatsApp may address a sender by a LID, an identifier that is not the phone number. The adapter takes the
number WhatsApp gives beside it or the mapping Baileys keeps; a message whose sender has no number is
dropped with a warning that does not carry the sender. The [live run](#live-run-on-a-real-number) records
whether the number came.

## Idempotency and retention

`send-prompt`, `send-message` and `report-outcome` carry the idempotency key in `webhook-id`. The
successful result is kept under it, and a repeat returns it without a second message; a failed attempt
keeps nothing, and a repeat that arrives while the first call is in progress waits for its outcome.

The keys, the tenants and the deep links of the transactions, and the polls of the prompts live in
`ADAPTER_DATA_DIR`, 24 hours by default. The retention may not be shorter than any window in which the same
key can come again: the retry budget of Veriqa for one call (20 s) and the redelivery window of an event by
the adapter itself — 3 attempts of up to 30 s with pauses of 1 s and 2 s, 93 s in all. Nor may it be
shorter than the longest transaction Veriqa accepts (`TransactionTtlSeconds` up to 1800 s): a vote on a
poll that is no longer kept is dropped, and an event of a transaction whose tenant has been forgotten goes
to the route of the tenant of the record instead. Hence the floor of 1800 s.

## Signature

Both directions of the wire are signed with the transport secret, Standard Webhooks scheme `v1`, by the
[`standardwebhooks`](https://www.npmjs.com/package/standardwebhooks) library: a timestamp outside five
minutes either way or a signature that does not match is `403` with an empty body, before anything else
happens. During a rotation set the new secret in `VERIQA_TRANSPORT_SECRET` and the old one in
`VERIQA_PREVIOUS_TRANSPORT_SECRET` (and the same pair in `Http:TransportSecret` /
`Http:PreviousTransportSecret` of Veriqa): a call is accepted under either, and an event carries a
signature of each.

## Limits of the sample

- **One instance.** A WhatsApp Web session belongs to one process: its state cannot be shared between
  processes, and two processes on the same state push each other off the connection. The idempotency
  keys and the polls live in the local SQLite file as well.
- **Unofficial.** See the [warning](#warning-an-unofficial-client-the-number-may-be-banned); the
  CI gate does not run Baileys itself — only the [live run](#live-run-on-a-real-number) does.
- **No rate limit of its own.** The adapter sends what Veriqa asks it to; WhatsApp may ban a number that
  sends a lot through an unofficial client.
- **A sender without a phone number is dropped** (see [Which messages reach Veriqa](#which-messages-reach-veriqa)).
- **The deep link has a length.** `prefill_text` is limited to 200 characters, the text Veriqa renders is
  not, and Veriqa refuses a deep link longer than 2048 characters with `deep_link_generation_failed`.

## Conformance suite

Veriqa ships a conformance suite that checks an adapter against the wire. It is built from the root of
this repository:

```bash
docker build -f src/core/Veriqa.Core.ChannelAdapter.Http.Conformance/Dockerfile -t veriqa-http-channel-conformance .
```

and runs next to the adapter with the same `Veriqa:Channels:whatsapp-baileys:*` keys Veriqa reads — for
example against the adapter of `compose.yaml` with a paired number, in the mode `calls`:

```bash
docker run --rm --network veriqa-whatsapp-baileys_default \
  -e Conformance__ChannelType=whatsapp-baileys \
  -e Conformance__ChannelUserId=<another WhatsApp number of yours in E.164> \
  -e Veriqa__Channels__whatsapp-baileys__InboundVerification=outbound_fetch \
  -e Veriqa__Channels__whatsapp-baileys__Http__BaseUrl=http://adapter:8000/ \
  -e Veriqa__Channels__whatsapp-baileys__Http__AllowInsecureHttp=true \
  -e Veriqa__Channels__whatsapp-baileys__Http__TransportSecret="$(sed -n 's/^VERIQA_TRANSPORT_SECRET=//p' .env)" \
  veriqa-http-channel-conformance
```

The mode `calls` sends real messages and polls from the linked number to that number. The CI gate of this
sample runs the suite in the mode `automatic` instead, with the adapter in the `stub` mode: WhatsApp Web is
an encrypted protocol a stub cannot play, so the stub of the stand takes the sendings over HTTP and posts
the messages of the users to a service path the adapter opens in that mode alone. Everything but Baileys
itself is checked that way, together with the checks the suite does not see — the messages that are not
events, the first vote, `adapter_settings`, a record that is not paired, the service path closed in the
`baileys` mode — and `compose.yaml` itself:

```bash
bash samples/node/custom-channel-whatsapp-baileys/ci/run.sh
```

It needs Docker with the compose plugin, prints `PASS` or `FAIL` per check and, last, the path of the JSON
report of the suite. The transport secrets of the run are generated into a temporary directory outside
the repository and removed with it; the numbers of the stand are fictional, and its network has no way out.

## Live run on a real number

A manual procedure, not a gate: one sign-in confirmed and one declined through `whatsapp-baileys`, with a
real WhatsApp number. It is the only check of Baileys itself.

1. Take a **separate** WhatsApp number for the adapter, and a second WhatsApp account — the user — on
   another phone: the own messages of the linked number are not events.
2. Fill in `.env` (`WHATSAPP_NUMBER` = the separate number), `docker compose up`, and pair the number
   (see [Pairing](#pairing-and-the-session-state)). `data.whatsapp-baileys` of
   `http://127.0.0.1:8080/health/channels` reads `healthy`.
3. Veriqa issues tokens over HTTPS only: put a TLS-terminating proxy or tunnel in front of
   `http://127.0.0.1:8080`. Next to `compose.yaml` create `compose.override.yaml` — Docker Compose reads it
   by itself:

   ```yaml
   services:
     veriqa:
       environment:
         Veriqa__OpenIddict__Server__Issuer: https://<the address of your proxy>
         # The network the proxy connects from, so that Veriqa trusts its X-Forwarded-Proto.
         ForwardedHeaders__KnownIPNetworks__0: <network, for example 172.18.0.0/16>
   ```

4. Register an OpenID Connect client of yours in the same override (`Veriqa__OpenIddict__Clients__0__…`: its
   id, redirect URI and the scopes `openid`, `profile`, `channel`), `docker compose up`, and sign in from it
   through `whatsapp-baileys`: open the deep link on the phone of the user, send the prefilled message, and
   vote in the poll that comes — once confirming, once declining.
5. Restart the adapter (`docker compose restart adapter`) and check that the record is connected again
   without a new pairing.
6. Fill in the table.

Run on 2026-09-29, `dev` at 351ecb3d1, a Russian (+7) number; one sign-in confirmed, one declined.

| What | Seen |
|---|---|
| Pairing by the code of the file linked the number | yes — linked on the first code; the session number matched the number of the record |
| The prompt came as one poll with two options, the confirm and the decline label | yes — one poll, `Confirm` / `Decline` (the channel gives no locale, so the prompt is in English) |
| The vote to confirm completes the sign-in; the token carries `amr` = `whatsapp-baileys` | yes — `amr` = `whatsapp-baileys`, `sub` = `whatsapp-baileys:<E.164>`, userinfo `is_bot` = `false` |
| The vote to decline refuses the sign-in | yes — the transaction failed with `declined_by_user` |
| The receipt of the outcome came as a new message | yes |
| The number of the sender came in E.164, not as a LID only (the log shows an event, not a dropped message) | yes — E.164; the log shows `auth_start`, `auth_confirm`, `auth_decline` events |
| The session survived the restart of the container | yes — connected again 2 s after the start, no new pairing |
