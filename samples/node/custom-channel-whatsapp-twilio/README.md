# WhatsApp through Twilio over the HTTP channel — reference external adapter in Node

A channel adapter that runs as a service of its own, next to Veriqa, and plugs WhatsApp in through
[Twilio](https://www.twilio.com/docs/whatsapp) and the **HTTP channel** of Veriqa: configuration only, no
code in Veriqa and no rebuild of it. The adapter speaks the HTTP channel wire (version 1, status
`preview`) to Veriqa and the [Twilio Messages API](https://www.twilio.com/docs/messaging/api/message-resource)
to Twilio.

It is two things at once:

- **a template for an adapter in any language.** The code is split in two: `src/core/` is the side of
  Veriqa — the [Standard Webhooks](https://www.standardwebhooks.com/) signature, the envelopes, the
  idempotency keys, the tenant of a transaction and the delivery of events — and knows nothing of Twilio;
  `src/twilio/` is the side of the platform. The seam between them is `src/core/seam.js`. An adapter for
  another messaging app replaces the second side and keeps the first;
- **a smoke test of the HTTP channel** on a platform that can refuse a prompt: outside the 24-hour
  customer service window of WhatsApp, Twilio does not deliver it, and the adapter tells Veriqa so
  (see [Closed customer service window](#closed-customer-service-window)).

| Path | What it is |
|---|---|
| `src/` | The adapter: `node src/main.js`, `node:http`, state in one SQLite file (`node:sqlite`) |
| `package.json` | Direct dependencies, pinned: `twilio` (the signature of the inbound webhooks), `standardwebhooks` |
| `Dockerfile` | Image of the adapter on `node:24-slim`, non-root, data volume `/data` |
| `compose.yaml`, `.env.example` | Veriqa and the adapter side by side |
| `ci/` | The CI gate: a stand-in Twilio, the stand, the conformance suite run and the own checks |

The sample ships no `package-lock.json`: `package.json` pins the direct dependencies to exact versions, and
the transitive ones are resolved when the image is built. Commit a lock file of your own when you take the
code over.

## Run it next to Veriqa

```bash
cp .env.example .env    # fill in the transport secret, the Twilio account and the public address
docker compose up
```

`compose.yaml` builds Veriqa from the root of this repository and the adapter from this folder. Veriqa
runs in the **Development** environment with the in-memory OpenIddict store and the development
certificates OpenIddict makes for itself: a demonstration, not a deployment — nothing survives a
restart. It listens on `http://127.0.0.1:8080`; the state of the channel is at
`http://127.0.0.1:8080/health/channels`, where `data.whatsapp-twilio` reads `healthy` once the adapter
answers and the Twilio account is `active`. The adapter listens on `http://127.0.0.1:8000`, for the
tunnel or reverse proxy that brings the webhooks of Twilio in.

An unfilled `.env` does not start: the adapter names the variable it could not use (never its value)
and exits with code `2`, and Veriqa refuses a transport secret that is not `whsec_…`. A port the adapter
cannot listen on and a data directory it cannot open its store in stop the start the same way. On
`SIGTERM` or `SIGINT` the adapter stops taking connections, lets the requests in progress end (for up to
8 seconds), closes its store and exits.

## Configuration of the adapter

Environment variables, read once at start.

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `VERIQA_URL` | yes | — | Base address of Veriqa; events go to `<VERIQA_URL>/api/channels/<type>/webhook[/<tenant>]` |
| `VERIQA_CHANNEL_TYPE` | yes | — | Channel type of the registration in Veriqa, `whatsapp-twilio`; a call for another type is refused with `channel_not_available` |
| `VERIQA_TRANSPORT_SECRET` | yes | — | Transport secret, `whsec_` + base64 of 24–64 bytes; the same value as `Http:TransportSecret` in Veriqa |
| `VERIQA_PREVIOUS_TRANSPORT_SECRET` | no | — | The previous secret while one is rotated (see [Signature](#signature)) |
| `ADAPTER_PORT` | no | `8000` | HTTP port, 1–65535: the calls of Veriqa (`POST /`) and the webhooks of the senders (`POST /twilio/<sender digits>`) |
| `ADAPTER_DATA_DIR` | no | `/data` | Directory of `adapter.sqlite3`; mount a volume here, writable by the user of the process |
| `ADAPTER_IDEMPOTENCY_TTL_SECONDS` | no | `86400` | Retention of the idempotency keys, the tenants and the deep links of the transactions; below `1800` the adapter does not start |
| `ADAPTER_LOG_LEVEL` | no | `INFO` | `DEBUG`, `INFO`, `WARNING` or `ERROR` |
| `TWILIO_API_URL` | no | `https://api.twilio.com` | Base URL of the Twilio REST API — or a stand-in |
| `TWILIO_PUBLIC_URL` | yes | — | Public base address of the adapter as Twilio reaches it, without a trailing slash (see [Inbound webhook](#inbound-webhook)) |
| `TWILIO_STATUS_WAIT_SECONDS` | no | `5` | How long `send-prompt` may take from its start, the creation of the prompt included, while it reads the status for a refusal; keep it below `Http:OperationTimeout` of Veriqa (10 s by default) — the adapter does not know that timeout and does not check it |
| `TWILIO_ACCOUNT_SID`, `TWILIO_AUTH_TOKEN`, `TWILIO_WHATSAPP_FROM`, `TWILIO_PROMPT_CONTENT_SID` | see below | — | The default record: Account SID (`AC…`), Auth Token, WhatsApp sender in E.164, SID of the prompt content (`HX…`); with the SID set, all four are required |
| `TWILIO_ACCOUNT_<n>_TENANT`, `_SID`, `_AUTH_TOKEN`, `_WHATSAPP_FROM`, `_PROMPT_CONTENT_SID` | see below | — | The record of a tenant, `n` = 1, 2, …; with `_SID` set, all five are required |

At least one record is required. No two records name the same tenant or the same sender number; two
records may share an Account SID — one account, two senders. Start with `node src/main.js` from this
folder, or with the image.

In Veriqa the channel is the section `Veriqa:Channels:whatsapp-twilio`; `compose.yaml` sets it in
`environment:` — a key with a dash in the channel type cannot be exported from a shell:

| Key under `Veriqa:Channels:whatsapp-twilio` | Value in `compose.yaml` |
|---|---|
| `InboundVerification` | `signature` — the only inbound mode, the signed webhook of Twilio |
| `Http:BaseUrl` | `http://adapter:8000/` — the adapter by its service name |
| `Http:AllowInsecureHttp` | `true` — the network of the compose file is private; Veriqa writes a warning at start |
| `Http:TransportSecret` | `VERIQA_TRANSPORT_SECRET` of `.env` |
| `Http:AdapterSettings` | `{"prefill_text": "Send this message to sign in."}` |
| `Http:Capabilities:*` | `SupportsInChannelConfirmation`, `DeliversOutcomeNotice`, `RendersEmoji` = `true`; `SupportsMessageUpdate`, `ProvidesRecipientLocale` = `false`; `SupportedMessageKinds:0` = `plain_text` — the facts of the built-in `whatsapp` channel |

## Inbound webhook

Every record has an inbound webhook of its own, named by the digits of its sender:
`POST <TWILIO_PUBLIC_URL>/twilio/<sender digits>` — for the sender `+14155550100`, `/twilio/14155550100`.
Set it in Twilio as the URL of *a message comes in* of that sender (for the Sandbox, in the settings of
the Sandbox). In Veriqa the channel is registered with **`InboundVerification = signature`**.

- The header `X-Twilio-Signature` is checked by `validateRequest` of the official `twilio` SDK with the
  Auth Token of the record the path names, over `TWILIO_PUBLIC_URL` + the path of the request. Twilio
  signs the URL it posted to, so behind a proxy or a tunnel `TWILIO_PUBLIC_URL` is the outside address,
  not the one the adapter listens on.
- No signature, or one that does not match — the token of another record among them — is `403`, with no
  effect; a request without the header is refused before its body is read. A body over 1 MB is refused.
- Otherwise the answer is `200` with an empty TwiML document, `<Response/>`: Twilio replies nothing to the
  user by itself.

The port also serves the calls of Veriqa on `POST /`, which carry the signature of the transport; let only
the paths `/twilio/…` through from outside when your proxy can.

## The prompt: `twilio/quick-reply` content

A prompt is a WhatsApp message with two buttons, sent as a
[`twilio/quick-reply`](https://www.twilio.com/docs/content/twilio-quick-reply) content with variables the
adapter fills for every prompt:

| Variable | What the adapter puts in it | Limit |
|---|---|---|
| `{{1}}` | The body: the text of the prompt Veriqa rendered | 1024 characters — a longer prompt fails with `message_send_failed`, it is never cut |
| `{{2}}` | Title of the confirm button | 20 characters, longer captions are cut |
| `{{3}}` | Id of the confirm button: `vq_confirm_<transaction>` | 200 characters |
| `{{4}}` | Title of the decline button | 20 characters, longer captions are cut |
| `{{5}}` | Id of the decline button: `vq_decline_<transaction>` | 200 characters |

Create the content once per account, with the Content Template Builder of the Twilio Console (a new
content of the type *Quick reply*, body `{{1}}`, two buttons with the titles `{{2}}`, `{{4}}` and the ids
`{{3}}`, `{{5}}`) or with the Content API:

```bash
curl -X POST "https://content.twilio.com/v1/Content" \
  -u "$TWILIO_ACCOUNT_SID:$TWILIO_AUTH_TOKEN" \
  --json '{
    "friendly_name": "veriqa_prompt",
    "language": "en",
    "variables": {"1": "Sign in to Acme?", "2": "Confirm", "3": "confirm", "4": "Decline", "5": "decline"},
    "types": {
      "twilio/quick-reply": {
        "body": "{{1}}",
        "actions": [{"title": "{{2}}", "id": "{{3}}"}, {"title": "{{4}}", "id": "{{5}}"}]
      }
    }
  }'
```

The `sid` of the answer (`HX…`) goes into `TWILIO_PROMPT_CONTENT_SID` of the record. Twilio takes a
variable in the title of a button in a session message only — which a prompt is: the user has just written
to the sender through the deep link.

## The text of a deep link

A `deep-link` call of Veriqa carries `rendered.text`: the message Veriqa rendered for the link from the
`deeplink-prefill` template of the channel type `whatsapp-twilio` and the purpose of the link, in the language
of the transaction. The adapter puts that text into the link. In a working configuration the text is always
there: the template has a root wording for any channel type without one of its own. To change the text of the
link, set the template for this channel type under
`Veriqa:MessageTemplates:deeplink-prefill:BySurface:in-channel:ByChannel:whatsapp-twilio`; `prefill_text` does not
change it.

[`prefill_text`](#adapter_settings) applies only when `rendered` is `null` or absent: Veriqa sends `null` when it
cannot render a plain-text wording for the link, and an earlier Veriqa does not send the field at all.

Either way the message carries the sign-in code `auth_<transaction>`: the adapter finds the transaction by it
when the user sends the message. When the text from Veriqa lacks the code, the adapter adds it on a last line
of its own: `https://wa.me/<sender digits>?text=<rendered.text>%0Aauth_<transaction>`. The wording Veriqa ships
has the `{code}` slot only for its built-in `whatsapp` channel, so for `whatsapp-twilio` the adapter adds the
code unless your own template for this channel type states the slot.

The adapter keeps the link of a transaction per purpose: the text of the link may differ by purpose.

## `adapter_settings`

Veriqa forwards `Http:AdapterSettings` to the adapter verbatim with every call and never reads it. This
adapter reads a JSON object with one key:

```json
{"prefill_text": "Send this message to sign in to Acme."}
```

`prefill_text` is the fallback opening line of the message a deep link puts into WhatsApp, for a call that
carries no text from Veriqa; in a working configuration, every call carries it (see
[The text of a deep link](#the-text-of-a-deep-link)). It goes above the sign-in code
`auth_<transaction>`: `https://wa.me/<sender digits>?text=<prefill_text>%0Aauth_<transaction>`. A string of
up to 200 characters; a missing setting, one that is not JSON, one without the key or with a longer line
reads as the default `Send this message to sign in.`, with a warning in the log that does not carry the
string. The key has a tenant level in Veriqa: the value of a tenant replaces the value of the channel whole,
so every tenant can greet its users by its own name.

## Credentials per tenant

A call of Veriqa names its tenant, and the adapter answers it with the record of that tenant — its Twilio
account, its sender, its prompt content. The record of the tenant wins; a tenant without a record of its
own — and the implicit tenant of a self-hosted installation — uses the default record. With neither, the
call is refused: `channel_not_available`, or `deep_link_generation_failed` for a deep link.

```yaml
# in the environment of the adapter
TWILIO_ACCOUNT_SID: ${TWILIO_ACCOUNT_SID}                 # the default record
TWILIO_AUTH_TOKEN: ${TWILIO_AUTH_TOKEN}
TWILIO_WHATSAPP_FROM: ${TWILIO_WHATSAPP_FROM}
TWILIO_PROMPT_CONTENT_SID: ${TWILIO_PROMPT_CONTENT_SID}
TWILIO_ACCOUNT_1_TENANT: shop-a
TWILIO_ACCOUNT_1_SID: ${SHOP_A_ACCOUNT_SID}
TWILIO_ACCOUNT_1_AUTH_TOKEN: ${SHOP_A_AUTH_TOKEN}
TWILIO_ACCOUNT_1_WHATSAPP_FROM: ${SHOP_A_WHATSAPP_FROM}
TWILIO_ACCOUNT_1_PROMPT_CONTENT_SID: ${SHOP_A_PROMPT_CONTENT_SID}
```

The first `deep-link` or `send-prompt` call of a transaction records the tenant it came with; the events
of that transaction go to the route of that tenant, and a later call with another tenant does not move
it. An event of no known transaction goes to the route of the tenant of the record whose webhook it came
in on. `health` checks every record: it is `healthy` when the account of each is `active`.

## Which messages reach Veriqa

The adapter decides it itself, by the rules of the built-in `whatsapp` channel of Veriqa; nothing else
leaves the adapter.

| Inbound message | Event |
|---|---|
| A text with `auth_<transaction>` anywhere in it — the prefilled message of a deep link | `auth_start` |
| A press of the confirm / decline button of a prompt (`ButtonPayload`) | `auth_confirm` / `auth_decline` |
| Anything else: a text without the code, a button of another content, a code with an invalid transaction, media without text | none |

The user of an event is the sender of the message: `channel_user_id` and `phone_number` are its number in
E.164, stated as verified (`phone_number_verified: true`) — the message came from it; the display name is
its WhatsApp profile name (or the number), and `raw_metadata` carries `whatsapp_message_id` (the
`MessageSid`) and `message_type`.

That is the **automatic** way of obtaining the phone number, and the registration declares it
(`compose.yaml`):

```yaml
Veriqa__Channels__whatsapp-twilio__Http__Capabilities__PhoneNumber__Acquisition__0: Automatic
Veriqa__Channels__whatsapp-twilio__Http__Capabilities__PhoneNumber__Proof: Proven
```

With `Automatic` declared the channel stays in the sign-in window of an application that requires the
phone number, and the number of the `auth_start` event reaches the token with nothing asked of the
user. `Proof: Proven` is the ceiling of what an event may state: without it Veriqa lowers
`phone_number_verified: true` of the event, writes a warning, and the token carries
`phone_number_verified: false`. `OnRequest` is not declared and `SupportedMessageKinds` stays
`plain_text`: WhatsApp through Twilio has no request for a contact. A message from an address that is not a phone
number is dropped with a warning that does not carry it. This differs from the built-in WhatsApp channel of
Veriqa, which knows the user by the business-scoped user ID (BSUID): the sample keys the user by the number,
so a message from a user who hides the number behind a username is dropped. The `webhook-id` of an event is
derived from the `MessageSid`, so a repeated delivery of one message reaches Veriqa under one id.

## Closed customer service window

WhatsApp lets a business answer a user freely within 24 hours of the user's last message. Outside that
window Twilio does not deliver a session message and reports
[error 63016](https://www.twilio.com/docs/api/errors/63016). The adapter answers a prompt it cannot deliver
for that reason with `channel_cannot_continue`, and Veriqa ends the sign-in with a refusal of the channel
instead of waiting for an answer that cannot come. It looks for the code in two places:

- in the answer to the creation of the message — for any of `send-prompt`, `send-message` and
  `report-outcome`, where Veriqa takes it on the last two as an ordinary failure to send;
- for `send-prompt`, in the status of the created message: the adapter reads it while it is `queued`,
  `accepted` or `sending`, until `TWILIO_STATUS_WAIT_SECONDS` have passed since the call began. `failed` or `undelivered` with
  code `63016` is `channel_cannot_continue`, any other failure `message_send_failed`, and a status still
  undecided when the wait ends counts as sent.

**Limit:** a `63016` that Twilio reports after the status wait is not reported to Veriqa — the wire has
no operation for a refusal that comes later. The user then gets no prompt, and the sign-in runs into its
timeout. The [live run](#live-run-in-the-twilio-sandbox) records where the code actually comes.

A user who starts a sign-in with the deep link writes to the sender first and opens the window; a closed
window is met by prompts addressed to a user who has not written — a confirmation started by the backend,
for example.

## Idempotency and retention

`send-prompt`, `send-message` and `report-outcome` carry the idempotency key in `webhook-id`. The
successful result is kept under it, and a repeat returns it without a second message; a failed attempt
keeps nothing, and a repeat that arrives while the first call is in progress waits for its outcome.

The keys, the tenants and the deep links of the transactions live in `ADAPTER_DATA_DIR`, 24 hours by
default. The retention may not be shorter than any window in which the same key can come again: the
retry budget of Veriqa for one call (20 s) and the redelivery window of an event by the adapter itself —
3 attempts of up to 30 s with pauses of 1 s and 2 s, 93 s in all. An attempt waits that long because
Veriqa answers `auth_start` only after its own `send-prompt` to the adapter has gone through. Nor may it
be shorter than the longest transaction Veriqa accepts (`TransactionTtlSeconds` up to 1800 s): the
tenant of a transaction lives as long as the retention, and an event of a transaction whose tenant has
been forgotten goes to the route of the tenant of the record instead. Hence the floor of 1800 s.

## Signature

Both directions of the wire are signed with the transport secret, Standard Webhooks scheme `v1`, by the
[`standardwebhooks`](https://www.npmjs.com/package/standardwebhooks) library: a timestamp outside five
minutes either way or a signature that does not match is `403` with an empty body, before anything
else happens. The library takes every signature of the header in turn, so the adapter needs no comparison
of its own. During a rotation set the new secret in `VERIQA_TRANSPORT_SECRET` and the old one in
`VERIQA_PREVIOUS_TRANSPORT_SECRET` (and the same pair in `Http:TransportSecret` /
`Http:PreviousTransportSecret` of Veriqa): a call is accepted under either, and an event carries a
signature of each.

## Limits of the sample

- **One instance.** A repeat that waits for a call in progress and the SQLite file are local to the
  process; two replicas would each send their own message.
- **The answer to a webhook waits for the event.** The adapter answers Twilio once Veriqa has taken the
  event, or once the delivery has given up — up to 93 s when Veriqa is unreachable. Twilio waits 15 s for a
  webhook and then records a timeout; the event is not lost by that, and Twilio is not asked to deliver the
  message again. An event Veriqa did not take after all attempts is logged and dropped: the user repeats the
  action — sends the prefilled message or taps the button once more.
- **A late `63016` is not reported** (see [Closed customer service window](#closed-customer-service-window)).
- **No rate limit of its own.** Limiting the inbound traffic belongs to the reverse proxy in front of
  the adapter.
- **The receipt is always a new message.** WhatsApp does not edit a delivered message, as the built-in
  channel says of it, so the outcome never replaces the prompt; its buttons stay.
- **The deep link has a length.** `prefill_text` is limited to 200 characters, the text Veriqa renders is
  not, and Veriqa refuses a deep link longer than 2048 characters with `deep_link_generation_failed`: an opening line of many non-ASCII
  characters can grow past it once it is percent-encoded.

## Conformance suite

Veriqa ships a conformance suite that checks an adapter against the wire. It is built from the root of
this repository:

```bash
docker build -f src/core/Veriqa.Core.ChannelAdapter.Http.Conformance/Dockerfile -t veriqa-http-channel-conformance .
```

and runs next to the adapter with the same `Veriqa:Channels:whatsapp-twilio:*` keys Veriqa reads — for
example against the adapter of `compose.yaml` (`docker compose up adapter`), in the mode `calls`:

```bash
docker run --rm --network veriqa-whatsapp-twilio_default \
  -e Conformance__ChannelType=whatsapp-twilio \
  -e Conformance__ChannelUserId=<your WhatsApp number in E.164> \
  -e Veriqa__Channels__whatsapp-twilio__InboundVerification=signature \
  -e Veriqa__Channels__whatsapp-twilio__Http__BaseUrl=http://adapter:8000/ \
  -e Veriqa__Channels__whatsapp-twilio__Http__AllowInsecureHttp=true \
  -e Veriqa__Channels__whatsapp-twilio__Http__TransportSecret="$(sed -n 's/^VERIQA_TRANSPORT_SECRET=//p' .env)" \
  veriqa-http-channel-conformance
```

The mode `calls` sends real messages to that number through your sender, so the user must have written to
the sender within the last 24 hours. The CI gate of this sample runs the suite in the mode `automatic`
against a stand-in Twilio — the closed window included — together with the checks the suite does not see:
the signature of the inbound webhook, the messages that are not events, `adapter_settings`, a `63016` in
the answer to the creation — and `compose.yaml` itself:

```bash
bash samples/node/custom-channel-whatsapp-twilio/ci/run.sh
```

It needs Docker with the compose plugin, prints `PASS` or `FAIL` per check and, last, the path of the
JSON report of the suite. The secrets, SIDs and numbers of the run are generated into a temporary
directory outside the repository and removed with it.

## Live run in the Twilio Sandbox

A manual procedure, not a gate: one sign-in confirmed and one declined through `whatsapp-twilio`, on
the [WhatsApp Sandbox](https://www.twilio.com/docs/whatsapp/sandbox) of a Twilio account.

**Not run yet — a known limitation of the sample.** The adapter is checked against the platform stub of
its CI stand only; it has not been run against Twilio itself. What only Twilio can show stays unchecked:
whether the Sandbox accepts the `twilio/quick-reply` content with variables and draws two buttons, and
where `63016` comes for a closed window. Run the procedure below before relying on the sample.

1. In the Twilio Console open the WhatsApp Sandbox (*Messaging → Try it out → Send a WhatsApp message*)
   and send `join <keyword>` from your phone to the Sandbox number, as the page says.
2. Create the prompt content (see [The prompt](#the-prompt-twilioquick-reply-content)) in the same
   account. Start a public HTTPS tunnel to `127.0.0.1:8000` (the adapter) and fill in `.env`: the Account
   SID and Auth Token, `TWILIO_WHATSAPP_FROM` = the Sandbox number, `TWILIO_PROMPT_CONTENT_SID`,
   `TWILIO_PUBLIC_URL` = the address of the tunnel. In the Sandbox settings set *When a message comes in*
   to `<TWILIO_PUBLIC_URL>/twilio/<Sandbox number digits>`, method `POST`.
3. Veriqa issues tokens over HTTPS only: put a TLS-terminating proxy or tunnel in front of
   `http://127.0.0.1:8080` as well. Next to `compose.yaml` create `compose.override.yaml` — Docker
   Compose reads it by itself:

   ```yaml
   services:
     veriqa:
       environment:
         Veriqa__OpenIddict__Server__Issuer: https://<the address of your proxy>
         # The network the proxy connects from, so that Veriqa trusts its X-Forwarded-Proto.
         ForwardedHeaders__KnownIPNetworks__0: <network, for example 172.18.0.0/16>
   ```

4. Register an OpenID Connect client of yours in the same override (`Veriqa__OpenIddict__Clients__0__…`:
   its id, redirect URI and the scopes `openid`, `profile`, `channel`), `docker compose up`, and sign in
   from it through `whatsapp-twilio`: once confirming, once declining.
5. For the closed window, sign in with a number that has not written to the Sandbox for more than 24
   hours — or wait that long — so that the prompt goes to a user whose last message is older than the
   window.
6. Fill in the table.

| What | Seen |
|---|---|
| The prompt came as one message with two buttons |  |
| Confirming completes the sign-in; the token carries `amr` = `whatsapp-twilio` |  |
| Declining refuses the sign-in |  |
| The receipt of the outcome came as a new message |  |
| Closed window: the sign-in got a refusal of the channel |  |
| Closed window: where `63016` came — in the answer to the creation, in the status within the wait, or later (the adapter log says which) |  |
| The Sandbox delivered the `twilio/quick-reply` content with variables |  |

### The phone number

Not run yet either. On the same stand: add the `phone` scope to the client and
`Veriqa__OpenIddict__Clients__0__RequirePhone: "true"` to the override (see the
[phone number guide](https://veriqa.app/docs/guides/phone-number)), restart, and sign in through
`whatsapp-twilio`, confirming.

| What | Seen |
|---|---|
| With a required number the sign-in window offers `whatsapp-twilio` |  |
| The sign-in completes with nothing asked about the number |  |
| The token carries `phone_number` — the number the message came from, in E.164 — and `phone_number_verified: true` |  |

## License

MIT, like everything under `samples/` — copy it into your own code.
