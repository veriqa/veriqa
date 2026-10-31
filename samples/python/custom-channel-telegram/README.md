# Telegram over the HTTP channel — reference external adapter in Python

A channel adapter that runs as a service of its own, next to Veriqa, and plugs Telegram in through the
**HTTP channel** of Veriqa: configuration only, no code in Veriqa and no rebuild of it. The adapter
speaks the HTTP channel wire (version 1, status `preview`) to Veriqa and the
[Telegram Bot API](https://core.telegram.org/bots/api) to Telegram.

It is two things at once:

- **a template for an adapter in any language.** The package is split in two: `adapter/core/` is the
  side of Veriqa — the [Standard Webhooks](https://www.standardwebhooks.com/) signature, the envelopes,
  the idempotency keys, the tenant of a transaction and the delivery of events — and knows nothing of
  Telegram; `adapter/telegram/` is the side of the platform. The seam between them is
  `adapter/core/seam.py`. An adapter for another messaging app replaces the second side and keeps the first;
- **a smoke test of the HTTP channel.** It is the same channel as the built-in `telegram` channel of
  Veriqa, taken through the wire instead: the prompt, the receipt and the claims of a user must come out
  the same (see [Live run](#live-run-against-telegram)).

| Path | What it is |
|---|---|
| `adapter/` | The adapter: `python -m adapter`, FastAPI on uvicorn, state in one SQLite file |
| `requirements.txt` | Direct dependencies, pinned: `fastapi`, `uvicorn`, `httpx`, `standardwebhooks` |
| `Dockerfile` | Image of the adapter on `python:3.13-slim`, non-root, data volume `/data` |
| `compose.yaml`, `.env.example` | Veriqa and the adapter side by side |
| `ci/` | The CI gate: a stand-in Telegram, the stand, the conformance suite run and the own checks |

## Run it next to Veriqa

```bash
cp .env.example .env    # fill in the transport secret and the bot token
docker compose up
```

`compose.yaml` builds Veriqa from the root of this repository and the adapter from this folder. Veriqa
runs in the **Development** environment with the in-memory OpenIddict store and the development
certificates OpenIddict makes for itself: a demonstration, not a deployment — nothing survives a
restart. It listens on `http://127.0.0.1:8080`; the state of the channel is at
`http://127.0.0.1:8080/health/channels`, where `data.telegram-http` reads `healthy` once the adapter
answers and every bot passes `getMe`.

An unfilled `.env` does not start: the adapter names the variable it could not use (never its value)
and exits with code `2`, and Veriqa refuses a transport secret that is not `whsec_…`. A port the adapter
cannot listen on and a data directory it cannot open its store in stop the start the same way.

## Configuration of the adapter

Environment variables, read once at start.

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `VERIQA_URL` | yes | — | Base address of Veriqa; events go to `<VERIQA_URL>/api/channels/<type>/webhook[/<tenant>]` |
| `VERIQA_CHANNEL_TYPE` | yes | — | Channel type of the registration in Veriqa, `telegram-http`; a call for another type is refused with `channel_not_available` |
| `VERIQA_TRANSPORT_SECRET` | yes | — | Transport secret, `whsec_` + base64 of 24–64 bytes; the same value as `Http:TransportSecret` in Veriqa |
| `VERIQA_PREVIOUS_TRANSPORT_SECRET` | no | — | The previous secret while one is rotated (see [Signature](#signature)) |
| `ADAPTER_PORT` | no | `8000` | HTTP port: the calls of Veriqa (`POST /`) and the webhooks of the bots (`POST /telegram/<bot id>`) |
| `ADAPTER_DATA_DIR` | no | `/data` | Directory of `adapter.sqlite3`; mount a volume here |
| `ADAPTER_IDEMPOTENCY_TTL_SECONDS` | no | `86400` | Retention of the idempotency keys, the tenants and the deep links of the transactions; below `1800` the adapter does not start |
| `ADAPTER_LOG_LEVEL` | no | `INFO` | `DEBUG`, `INFO`, `WARNING` or `ERROR` |
| `TELEGRAM_API_URL` | no | `https://api.telegram.org` | Base URL of the Bot API — a local Bot API server or a stand-in |
| `TELEGRAM_INBOUND_MODE` | yes | — | `webhook` or `polling` (see [Inbound modes](#inbound-modes)) |
| `TELEGRAM_PUBLIC_URL` | in `webhook` | — | Public address of the adapter the webhooks of the bots are registered at |
| `TELEGRAM_BOT_TOKEN` | see below | — | Bot of the default record |
| `TELEGRAM_BOT_FORWARD_URL` | no | — | Where the default bot forwards the updates that are not answers to Veriqa |
| `TELEGRAM_BOT_<n>_TENANT`, `_TOKEN`, `_FORWARD_URL` | see below | — | Bot of a tenant, `n` = 1, 2, …; `_TENANT` is required with `_TOKEN` |
| `CODE_START` | no | `off` | The start of a sign-in by a code: `off`, `fallback` or `only` (see [Start by a code](#start-by-a-code)); any other value stops the start |

At least one bot is required. No two records name the same tenant or the same bot.

In Veriqa the channel is the section `Veriqa:Channels:telegram-http`; `compose.yaml` sets it in
`environment:` — a key with a dash in the channel type cannot be exported from a shell:

| Key under `Veriqa:Channels:telegram-http` | Value in `compose.yaml` |
|---|---|
| `InboundVerification` | `VERIQA_INBOUND_VERIFICATION` of `.env`, by the inbound mode |
| `Http:BaseUrl` | `http://adapter:8000/` — the adapter by its service name |
| `Http:AllowInsecureHttp` | `true` — the network of the compose file is private; Veriqa writes a warning at start |
| `Http:TransportSecret` | `VERIQA_TRANSPORT_SECRET` of `.env` |
| `Http:AdapterSettings` | `{"disable_notification": false}` |
| `Http:Capabilities:*` | `SupportsInChannelConfirmation`, `SupportsMessageUpdate`, `DeliversOutcomeNotice`, `ProvidesRecipientLocale`, `RendersEmoji` = `true`, `SupportedMessageKinds:0` = `plain_text` — the facts of the built-in `telegram` channel |
| `Http:Capabilities:CodeStart:LinkCarriesTransaction` | `VERIQA_CODE_START_LINK_CARRIES_TRANSACTION` of `.env`; empty — the key is not set (see [Start by a code](#start-by-a-code)) |

## Inbound modes

| Mode | How updates come in | `InboundVerification` in Veriqa |
|---|---|---|
| `webhook` | The adapter registers `<TELEGRAM_PUBLIC_URL>/telegram/<bot id>` with `setWebhook` and a `secret_token` of its own for every bot. The header `X-Telegram-Bot-Api-Secret-Token` is compared in constant time before the body is read: missing — `401`, wrong — `403`, no effect either way. A body over 1 MB is refused | `shared_secret` |
| `polling` | `getUpdates` per bot; nothing comes in from outside, no public address is needed | `outbound_fetch` |

The `secret_token` of a webhook is derived from the token of its bot (HMAC-SHA256), so every bot has
its own, a restart keeps it, and only the holder of the token can learn it — there is no separate
variable to keep. `webhook` needs `TELEGRAM_PUBLIC_URL` to reach port `8000` of the adapter over
HTTPS: publish the port through your reverse proxy or tunnel, and let only the paths `/telegram/…`
through from outside.

## `adapter_settings`

Veriqa forwards `Http:AdapterSettings` to the adapter verbatim with every call and never reads it. This
adapter reads a JSON object with one key:

```json
{"disable_notification": true}
```

`disable_notification` goes to the Bot API with every message the adapter sends, and Telegram delivers
the message silently. A missing string, a string that is not JSON or one without the boolean key reads
as `false`, with a warning in the log that does not carry the string. The key has a tenant level in
Veriqa: the value of a tenant replaces the value of the channel whole, so one tenant can have silent
messages and another not.

## Credentials per tenant

A call of Veriqa names its tenant, and the adapter answers it with the bot of that tenant. The record
of the tenant wins; a tenant without a record of its own — and the implicit tenant of a self-hosted
installation — uses the default record (`TELEGRAM_BOT_TOKEN`). With neither, the call is refused:
`channel_not_available`, or `deep_link_generation_failed` for a deep link.

```yaml
# in the environment of the adapter
TELEGRAM_BOT_TOKEN: ${TELEGRAM_BOT_TOKEN}          # the default record
TELEGRAM_BOT_1_TENANT: shop-a
TELEGRAM_BOT_1_TOKEN: ${SHOP_A_BOT_TOKEN}
```

The first `deep-link` or `send-prompt` call of a transaction records the tenant it came with; the events
of that transaction go to the route of that tenant, and a later call with another tenant does not move
it. An event of no transaction (a contact, a message) goes to the route of the tenant of the bot that
received it.

## Which updates reach Veriqa

The adapter decides it itself, by the rules of the built-in `telegram` channel. When the bot has a
forward address, it owns the conversation, and everything that is not an answer to Veriqa goes there —
`POST` of the update, `content-type: application/json`: in `webhook` the body Telegram posted, byte for
byte; in `polling` the update of `getUpdates`, serialized again (the same JSON, not necessarily the same
bytes):

| Update | Without a forward address | With a forward address |
|---|---|---|
| `/start auth_<transaction>` in a private chat | `auth_start` | `auth_start` |
| A private message of digits, with `CODE_START` other than `off` (see [Start by a code](#start-by-a-code)) | `auth_start_by_code` | `auth_start_by_code` |
| A press of the confirm / decline button of a prompt | `auth_confirm` / `auth_decline` | the same |
| The user's own contact; the adapter brings its number to E.164 with the leading plus | `phone_shared` | forwarded |
| Any other private message, a bare `/start` among them | `unaddressed`, with the `text` of the message | forwarded |
| Anything else: other buttons, somebody else's contact, a contact whose number has no E.164 form, groups, other update types, a `/start auth_` with an invalid transaction | dropped | forwarded |

An unreachable forward address is a warning in the log; the update does not go to Veriqa instead.

The adapter names the update types it wants in every `setWebhook` and `getUpdates` (`allowed_updates`),
so a setting left on the token by an earlier user of the bot cannot leave out the button presses: a bot
without a forward address asks for `message` and `callback_query` alone, a bot with one for the default
set of Telegram — every type but `chat_member`, `message_reaction` and `message_reaction_count`, which
the bot of the integrator therefore does not get.

## Start by a code

On a platform whose link cannot carry the transaction, the user starts a sign-in by sending the bot the
short code the sign-in window shows. Telegram's link can carry it, so the sample offers the code as a
switch, to show the mode an adapter of such a platform needs.

| `CODE_START` of the adapter | Its deep link | `Http:Capabilities:CodeStart:LinkCarriesTransaction` in Veriqa |
|---|---|---|
| `off` (default) | `https://t.me/<bot>?start=auth_<transaction>`; no code | not set |
| `fallback` | the same; a message of digits starts a sign-in as well | `true` |
| `only` | `https://t.me/<bot>` — it only opens the chat; a sign-in starts by a code alone | `false` |

The two values go together: set `CODE_START` and `VERIQA_CODE_START_LINK_CARRIES_TRANSACTION` in `.env`
by this table. With `only` the adapter still takes `/start auth_<transaction>` from a link made earlier.
With `true` the sign-in window offers the code next to the link as a spare one; the core setting
`Veriqa:Channels:telegram-http:SpareStartCodeEnabled` switches that off.

**What counts as a code.** A private text message that, once whitespace and `-` are dropped, is decimal
digits alone — `123456`, `12 34-56`. It goes to Veriqa as the event `auth_start_by_code` with `code` as
the user typed it; Veriqa drops the separators, checks the length and finds the transaction. Any other
text stays `unaddressed`. The sample declares no message template: the user sends the code alone. The
event goes on the route of the bot that received the message, like any event of no transaction; Veriqa
looks the code up across the installation and takes the tenant from the transaction it finds. The code
is carried, never logged.

A message of digits goes to Veriqa even from a bot with a forward address — otherwise a bot with one
could not start a sign-in by a code — so with `CODE_START` other than `off` the bot of the integrator
does not get messages made of digits alone.

**Only a registration without an owner, for now.** An HTTP registration that declares the start by a
code and sets `Http:Tenant` stops Veriqa at start with a configuration error. `compose.yaml` sets no
`Http:Tenant`.

### Live run

The stand of [Live run against Telegram](#live-run-against-telegram) with bot B alone: no built-in
channel is needed.

1. In `.env`: `TELEGRAM_BOT_TOKEN` = the token of B, `TELEGRAM_INBOUND_MODE=polling`,
   `VERIQA_INBOUND_VERIFICATION=outbound_fetch`, `CODE_START=only`,
   `VERIQA_CODE_START_LINK_CARRIES_TRANSACTION=false`.
2. In `compose.override.yaml`: the issuer, the forwarded headers and the client of steps 3 and 4 of that
   procedure; `docker compose up`.
3. Sign in from the client. The sign-in window offers `telegram-http` with the code to send to B.
4. Open the chat and send the code to B. B asks the question of the sign-in with its two buttons.
5. Press the confirm button: the client receives the authorization code.

## The phone number on request

The sample shows the **on request** way of obtaining the phone number of a user. The registration
declares it, and the content kind that carries the request with it (`compose.yaml`):

```yaml
Veriqa__Channels__telegram-http__Http__Capabilities__SupportedMessageKinds__0: plain_text
Veriqa__Channels__telegram-http__Http__Capabilities__SupportedMessageKinds__1: phone_number_request
Veriqa__Channels__telegram-http__Http__Capabilities__PhoneNumber__Acquisition__0: OnRequest
Veriqa__Channels__telegram-http__Http__Capabilities__PhoneNumber__Proof: Proven
```

When a sign-in needs the number, Veriqa calls `send-message` with `kind` = `phone_number_request` and
the member `phone_number_request`: the captions of the two buttons, already in the language of the
user, the secondary action and the transaction. The adapter shows them as the built-in Telegram
channel does — a
[reply keyboard](https://core.telegram.org/bots/api#keyboardbutton) with the `request_contact` button
on top and the button of the secondary action under it, `one_time_keyboard` and `resize_keyboard` set.
A `phone_number_request` call without that member is refused as a malformed call (`400`).

`one_time_keyboard` only folds the keyboard away after a press. What takes it down is the final
`report-outcome` of the sign-in: when it carries `phone_request_message`, the adapter sends the status
text as a new message with `remove_keyboard` — a reply keyboard cannot be removed by an edit — and
leaves the prompt as it is, whatever `display_intent` says.

| The user | Update | Event |
|---|---|---|
| presses the share button | the user's own contact | `phone_shared` with the number in E.164 |
| presses the second button | a message whose text is the caption of the button | `unaddressed` with that `text` |

A reply button of Telegram carries no payload, so the second press is told apart by its text alone:
the adapter sends the `text` of every `unaddressed` message, and Veriqa compares it with the caption
of the request it is waiting on. The text is carried, never logged. (A platform whose button does carry
a payload posts the `phone_step_action` event instead, naming the transaction and the action.)

`Proof: Proven` is what the contact button of Telegram justifies: the adapter takes the contact only
when its `user_id` is the sender. Without it the token would carry `phone_number_verified: false`.

**Declare the phone number only for a bot without a forward address.** With one, the contact and the
text of the second button are forwarded to the bot of the integrator, as every message that names no
transaction is, and never reach Veriqa: the request would be shown and never answered.

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
been forgotten goes to the route of the tenant of the bot instead. Hence the floor of 1800 s.

## Signature

Both directions are signed with the transport secret, Standard Webhooks scheme `v1`, by the
[`standardwebhooks`](https://pypi.org/project/standardwebhooks/) library: a timestamp outside five
minutes either way or a signature that does not match is `403` with an empty body, before anything
else happens. During a rotation set the new secret in `VERIQA_TRANSPORT_SECRET` and the old one in
`VERIQA_PREVIOUS_TRANSPORT_SECRET` (and the same pair in `Http:TransportSecret` /
`Http:PreviousTransportSecret` of Veriqa): a call is accepted under either, and an event carries a
signature of each.

## Limits of the sample

- **One instance.** A repeat that waits for a call in progress and the SQLite file are local to the
  process; two replicas would each send their own message.
- **An event Veriqa does not take is lost.** When Veriqa stays unreachable (or answers `5xx`/`429`)
  through all 3 attempts, the adapter logs the loss and still answers the webhook of Telegram with `200`
  (in `polling`, moves the offset past the update): Telegram does not deliver the update again, and the
  user repeats the action — taps the link or the button once more. Answering `5xx` instead would make
  Telegram redeliver for up to 24 hours, including stale presses long after their transaction, and the
  retention would have to cover those 24 hours as well.
- **No rate limit of its own.** Limiting the inbound traffic belongs to the reverse proxy in front of
  the adapter.
- **Differences from the built-in channel**, all deliberate: a button press carries an `inbound_token`
  only when the update carries the message of the button; the prompt is edited into the receipt only
  when Veriqa asks to replace it and names the prompt message — otherwise the receipt is a new message,
  and the buttons of the prompt stay.
- The adapter never returns `channel_cannot_continue`: a user who has written to the bot can always be
  answered by it.

## Conformance suite

Veriqa ships a conformance suite that checks an adapter against the wire. It is built from the root of
this repository:

```bash
docker build -f src/core/Veriqa.Core.ChannelAdapter.Http.Conformance/Dockerfile -t veriqa-http-channel-conformance .
```

and runs next to the adapter with the same `Veriqa:Channels:telegram-http:*` keys Veriqa reads — for
example against the adapter of `compose.yaml` (`docker compose up adapter`), in the mode `calls`:

```bash
docker run --rm --network veriqa-telegram-http_default \
  -e Conformance__ChannelType=telegram-http \
  -e Conformance__ChannelUserId=<your numeric Telegram user id> \
  -e Veriqa__Channels__telegram-http__InboundVerification=outbound_fetch \
  -e Veriqa__Channels__telegram-http__Http__BaseUrl=http://adapter:8000/ \
  -e Veriqa__Channels__telegram-http__Http__AllowInsecureHttp=true \
  -e Veriqa__Channels__telegram-http__Http__TransportSecret="$(sed -n 's/^VERIQA_TRANSPORT_SECRET=//p' .env)" \
  veriqa-http-channel-conformance
```

The mode `calls` sends real messages to that user through your bot, so the user must have started the
bot first. The CI gate of this sample runs the suite in the mode `automatic` against a stand-in
Telegram, together with the checks the suite does not see — the webhook secret, the separation of the
updates, `adapter_settings` — and `compose.yaml` itself:

```bash
bash samples/python/custom-channel-telegram/ci/run.sh
```

It needs Docker with the compose plugin, prints `PASS` or `FAIL` per check and, last, the paths of the
JSON reports of the suite. The suite runs twice: as the stand is set up, and again
(`conformance.code-start`) with the adapter recreated in `CODE_START=only` and the registration declaring
the start by a code, where `flow.code-start` must pass and the scenarios that start by a link of a
transaction do not run. The tokens and the secret of the run are generated into a temporary directory
outside the repository and removed with it.

## Live run against Telegram

A manual procedure, not a gate: the same sign-in through the built-in `telegram` channel and through
`telegram-http` on one Veriqa, compared side by side.

1. Create two bots with [@BotFather](https://t.me/BotFather): **A** for the built-in `telegram`
   channel, **B** for `telegram-http`.
2. In `.env`: `TELEGRAM_BOT_TOKEN` = the token of B, `TELEGRAM_INBOUND_MODE=polling`,
   `VERIQA_INBOUND_VERIFICATION=outbound_fetch`, and a new line `BUILTIN_TELEGRAM_BOT_TOKEN` = the token
   of A. Both channels poll, so no public address is needed.
3. Veriqa issues tokens over HTTPS only: put a TLS-terminating proxy or tunnel in front of
   `http://127.0.0.1:8080`. Next to `compose.yaml` create `compose.override.yaml` — Docker Compose reads
   it by itself:

   ```yaml
   services:
     veriqa:
       environment:
         Veriqa__Channels__Telegram__Enabled: "true"
         Veriqa__Channels__Telegram__BotToken: ${BUILTIN_TELEGRAM_BOT_TOKEN}
         Veriqa__Channels__Telegram__UpdateMode: Polling
         Veriqa__Channels__Telegram__InboundVerification: outbound_fetch
         Veriqa__OpenIddict__Server__Issuer: https://<the address of your proxy>
         # The network the proxy connects from, so that Veriqa trusts its X-Forwarded-Proto.
         ForwardedHeaders__KnownIPNetworks__0: <network, for example 172.18.0.0/16>
   ```

   The template of the prompt and the facts of both channels are the same: `compose.yaml` declares for
   `telegram-http` exactly the facts of the built-in channel, `RendersEmoji` and the phone number on
   request among them.
4. Register an OpenID Connect client of yours in the same override (`Veriqa__OpenIddict__Clients__0__…`:
   its id, redirect URI and the scopes `openid`, `profile`, `channel`, `avatar`), `docker compose up`, and
   sign in from it with those scopes. Do it four times: confirm and decline through each channel.
5. Fill in the table. Everything must match except what derives from the channel type.

Run on 2026-09-29, `dev` at 351ecb3d1, both channels in `polling`: four sign-ins, both declines refused the
sign-in; the claims were compared once more on two confirmed sign-ins of one account, userinfo included. Texts are named by their localization keys; the user saw them in Russian.

| What | Built-in `telegram` | `telegram-http` | Must match |
|---|---|---|---|
| Text of the prompt | `Confirm sign-in to "{app}" — request from {browser} on {os}` | the same | yes — matched |
| Captions of the buttons | `Confirm ✅`, `Decline ❌` | the same | yes — matched |
| Receipt after confirming (the prompt is replaced) | replaced with `Sign-in confirmed ✅` (a sign-in started on a computer; one started on a phone or a tablet gets `Sign-in confirmed ✅ Tap Back to return to the sign-in page.`) | the same | yes — matched |
| Receipt after declining | replaced with `Sign-in declined ❌` | the same | yes — matched |
| `name`, `given_name`, `family_name`, `preferred_username` | present | present, equal | yes — matched |
| `locale` | `ru` | `ru` | yes — matched |
| `picture` | in userinfo (the access token only, by design) | the same image, byte for byte | yes — matched |
| `channel_user_id`, `is_bot` | equal; `is_bot` = `false` in userinfo | the same | yes — matched |
| `channel_type`, `veriqa_channel` | `telegram`; no `veriqa_channel` (only the Email channel issues it) | `telegram-http`; the same | no — the type of the channel |
| `amr` | `["telegram"]` | `["telegram-http"]` | no — the type of the channel |
| `sub` | `telegram:<user id>` | `telegram-http:<user id>` | no — a subject of its own per channel |

### The phone number

Run on 2026-10-10, branch `claude/http-channel-phone-number-593dcd` at 6767b56b2, `telegram-http` alone in
`polling`: the three sign-ins below, each observed in the chat and in the browser. The content of the
tokens was not inspected in this run — the claims are covered by the component tests.

The same stand, bot B without a forward address. The requirement of the number is set on the client
(see the [phone number guide](https://veriqa.app/docs/guides/phone-number)), and it counts only when
the sign-in asks for the `phone` scope — add it to the scopes of the client.

1. **Required, shared.** Add `Veriqa__OpenIddict__Clients__0__RequirePhone: "true"` to the override and
   restart. Sign in through `telegram-http` and confirm: the bot sends the request with two buttons —
   the share button and **Cancel sign-in**. Press the share button.
2. **Required, canceled.** Sign in again, confirm, and press **Cancel sign-in**.
3. **Optional, skipped.** Replace `RequirePhone` with
   `Veriqa__OpenIddict__Clients__0__ClaimLevels__phone_number: Optional`, restart, sign in, confirm, and
   press **Skip**.
4. Fill in the table.

| What | Seen |
|---|---|
| With a required number the sign-in window offers `telegram-http` | yes |
| The request came as one message with a reply keyboard: the share button on top, the second button under it | yes |
| Sharing completes the sign-in; the token carries `phone_number` in E.164 and `phone_number_verified: true` | the sign-in completed; the token was not inspected |
| **Cancel sign-in** refuses the sign-in: the client receives `error=access_denied` | the sign-in was refused |
| **Skip** completes the sign-in; the token carries no `phone_number` | the sign-in completed; the token was not inspected |
| The keyboard is gone once the sign-in is over (shared, canceled or skipped) | yes, in all three |

## License

MIT, like everything under `samples/` — copy it into your own code.
