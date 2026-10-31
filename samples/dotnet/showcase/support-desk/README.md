# Veriqa showcase — Helio, the support section of a website

A sample product — the support section of a company website — that presents these Veriqa
capabilities:

- **Sign-in without a password.** The support section is public; when the visitor signs in, they
  approve it in a messaging app they already use.
- **One sign-in page for the desktop and the phone.** A desktop gets a QR code to scan with the
  phone; a phone that opens the same page gets a button per channel and no QR — one tap into the
  messaging app. These are the core's defaults; the sample sets nothing for them.
- **Registration data without a form.** The customer card — name, picture, @username — is filled
  from the claims the channel already knows.
- **Confirmation of a sensitive action (step-up).** Closing or deleting a ticket takes one scan — one
  tap on a phone — straight into the messaging app the customer signed in with, with no channel to
  choose. The action is approved there by the same person who signed in.
- **Progressive enrichment.** The sign-in asks for no contacts. A phone number or an e-mail address
  is confirmed later, each by an authorize request of its own, when a feature needs it: "Call me
  back" opens with a confirmed phone, "Answer by e-mail" with a confirmed address.
- **Configuration per audience.** The set of channels on the sign-in page follows the browser
  language: a Russian-language browser gets one set, any other browser gets another that includes
  Instagram and Messenger. Each set is one `ui_config` record — no code change.

A sign-in and a ticket action leave different receipts in the chat. A sign-in's question is replaced by
"Sign-in to Helio Support confirmed ✅" (`ReplacePrompt`, the global `OutcomeNotice` section). A
ticket action keeps its question and gets the receipt as a message of its own (`NewMessage`, stated
in the backend client's entry — the client that creates the confirmations), so what was approved
stays readable.

The page belongs to the fictional company. The sign-in page and the confirmation window are Veriqa's
own screens, shown as they are.

## How it is put together

One process plays three roles, as in [`inproc/step-up`](../../inproc/step-up/README.md):

| Role | What it is |
|---|---|
| Veriqa issuer (inproc) | `AddVeriqaAuthServer` → `UseVeriqaAuthServer` → `MapVeriqaAuthServer` |
| Relying party | the standard `AddOpenIdConnect` handler, pointed at this very process |
| Backend | `VeriqaConfirmationClient`, calling the public confirmation API over HTTP |

### Where the tickets come from

They are **synthesised**, not stored: `TicketStore.Synthesize` is a pure function of the `sub` claim,
so the same person sees the same four to seven tickets on every visit and the sample ships no
database. The hash behind it is FNV-1a written out in the file — `string.GetHashCode` is randomised
per process and would hand the same person a different list after every restart.

What *is* kept in memory is the difference the customer has made since: which tickets they closed,
which they deleted, and how they asked to be contacted about one. A restart forgets that difference
and the list returns to its synthesised state.

### Contacts, confirmed when a feature needs them

The sign-in asks for `openid profile channel avatar` and nothing else — neither `phone` nor `email`.
The card shows each contact as "not confirmed", with a button of its own, and the two ways of
getting in touch about a ticket stay closed until their contact is there:

| Button on the card | Route | What the authorize request adds to the sign-in | Opens |
|---|---|---|---|
| Confirm phone | `GET /verify-phone` | scope `phone`, `claims={"id_token":{"phone_number":{"essential":true}}}` | Call me back |
| Confirm e-mail | `GET /verify-email` | scope `email`, `claims={"id_token":{"email":{"essential":true}}}` | Answer by e-mail |

Either one is an ordinary authorize request of the same client, `helio-support-web`, through the
same `AddOpenIdConnect` handler and the same callback path — there is no enrichment API and no
second client. The additions are written in the handler's `OnRedirectToIdentityProvider` event, by a
mark the route puts into the authentication properties. An anonymous visitor who opens either route
is sent back to `/`: there is no session to add a contact to. This is the "by a new authorize
request" way of
[progressive claim enrichment](https://veriqa.app/docs/concepts/claim-enrichment); the other way —
Veriqa collecting the missing claims by itself, inside the sign-in — is what
[Nova Stream](../streaming-tv/README.md) shows.

**Why the request states the requirement, not the configuration.** A level set in the client's
`ClaimLevels` or in the `Veriqa:ScopesClaims` catalog applies to every sign-in, and the sign-in here
is meant to ask for nothing. A configured requirement for `email` also stops the host from starting
while the Email channel is off — and the sample has to start with every channel off. The `claims`
parameter (OIDC Core 1.0 §5.5) raises the level for one request only.

**What is accepted.** A contact is accepted only as a pair: the value **and** its mark saying
`true` — `phone_number` with `phone_number_verified`, `email` with `email_verified`
(`WithAcceptedContact` in `Program.cs`). Asking for a claim as essential is not a guarantee: a level
the issuer cannot enforce — no way to collect an address while the Email channel is off, say — is
lowered silently, and the answer arrives without the claim. The session is then left exactly as it
was, and the button stays on the card. The same happens when the customer presses "Cancel sign-in":
they come back to `/` with the session and the contact accepted earlier untouched.

**Where a contact lives.** In the cookie of this application and nowhere else, as claims of the
sample's own (`helio_verified_phone`, `helio_verified_email`); the collected claims store of Veriqa
(`Veriqa:CollectedClaims`) is deliberately not switched on. `/signout` drops the cookie and the
contacts with it, so after the next sign-in both are asked for again. Confirming a contact a second
time replaces the value; the other contact is not touched.

**The answer may carry another `sub` — and is accepted.** A contact request is a complete sign-in of
its own, and nothing ties it to the `sub` of the first one. One person has a different `sub` in
every channel, and a customer who signed in through Email has to answer the phone request in a
messaging app: the Email channel gives no phone number, and for a required number the sign-in page
offers only the channels able to provide one. The handler's `OnTicketReceived` event
therefore does not compare subjects: it keeps the identity of the session — `sub`, card, tickets —
from the first sign-in and adds the contact to it. That is a simplification of the demo: the answer
may just as well come from another person, and a product decides for itself whether the two are the
same — by comparing `sub`, by linking accounts, or by a step-up addressed to the session's identity.

**The gate is the server.** `POST /api/tickets/{id}/contact-request` answers
`403 {"error":"contact_unverified"}` when the session has not accepted the contact of the chosen
method, whatever buttons the page drew. A request needs no step-up — nothing is destroyed by it —
and is remembered together with its destination, the masked number or the address. A request made
again for the same ticket replaces the earlier one. After a sign-out the ticket still shows where
the desk was asked to answer, while the card shows no contact: the destination belongs to the
request, not to the session.

### The step-up, exactly

Closing a ticket and deleting it are **two action types**, `resolve-ticket` and `delete-ticket`, with
wordings of their own — not one type with a parameter. Each confirmation names **one** expected
identity, the one belonging to the channel the current session came through:

| Channel of the session | Declared comparable type | Normalisation |
|---|---|---|
| `telegram` | `telegram_user_id` | `Exact` |
| `whatsapp` | `whatsapp_user_id` | `Exact` |
| `max` | `max_user_id` | `Exact` |
| `instagram` | `instagram_user_id` | `Exact` |
| `messenger` | `messenger_user_id` | `Exact` |
| `email` | `email_address` | `EmailDomainCaseFold` |

All six compare the same claim, `channel_user_id`; only the rule for reading an identity differs.
The names live in `SampleIdentityTypes` and in `IdentityMatchComparableTypes` of the
`helio-support-backend` client — two halves of one declaration, because a confirmation naming an
undeclared type is refused with `candidate_type_undeclared`.

The confirmation is created with the channel of the session as well, so its QR code is a deep link
straight into that channel, whichever of the six it is — the channel-choice page is never shown.

| Outcome from Veriqa | `matched_type` | State of the operation |
|---|---|---|
| `confirmed` | the expected type | `applied` — the ticket is closed or deleted |
| `confirmed` | absent or another type | `mismatched` — the ticket is untouched |
| `declined` / `expired` / `failed` | — | `refused` — the ticket is untouched |

Which ticket a transaction acts on, and how, is remembered **on the server**: the page is never
asked, so no browser can delete one ticket by confirming another.

The customer is asked twice on purpose. The dialog in the page is the cheap question — it prevents a
misclick. The question in the channel is the one that decides.

### What stays on the server

`channel_user_id` never leaves it in full — it addresses the step-up. The card shows a Telegram user
id masked (`12*****89`, `ViewerProfile.MaskIdentifier`), and the confirmed phone number is masked
the same way (`ViewerProfile.MaskPhone`) **before** either is put into a response, so the browser is
never handed the full value. A callback request remembers the number in that masked form too.

## The API

Every route but `GET /api/session` answers an anonymous caller `401 {"error":"unauthenticated"}`.

```
GET  /api/session                        → { authenticated, profile, contacts: { phoneVerified, emailVerified } }
GET  /api/tickets                        → { tickets: [ { id, subject, status, createdAt, lastMessage,
                                                          contactRequest } ] }
POST /api/tickets/{id}/resolution        body { "action": "resolve" | "delete" }
                                         → { transactionId, qr, url, action, channelType }
                                           400 {"error":"unknown_action"} · 404 not your ticket
                                           409 {"error":"already_resolved"}
GET  /api/tickets/resolutions/{txn}      → { transactionId, ticketId, action, state, outcome }
                                           404 — unknown, or started by another session
POST /api/tickets/{id}/contact-request   body { "method": "callback" | "email" }
                                         → { ticketId, method, destination }
                                           400 {"error":"unknown_method"} · 404 not your ticket
                                           409 {"error":"already_resolved"}
                                           403 {"error":"contact_unverified"}
```

`contacts` is two `false` for an anonymous visitor. `profile.phoneMasked` and `profile.email` are
filled only by a contact the session has accepted — never by the channel of the sign-in.
`contactRequest` is `{ method, destination }` or `null`; `destination` is the masked phone number
for `callback` and the address for `email`, as it stood when the request was made.

Two more routes are pages rather than API: `GET /verify-phone` and `GET /verify-email` start the
contact requests and answer an anonymous visitor with a redirect to `/`.

`status` is `open` · `waiting` · `resolved`; `state` is `pending` · `applied` · `refused` ·
`mismatched`. A refusal from Veriqa is passed through with its own status and body.

A sign-in that comes back with an error (`error=server_error`, for one) is caught by the handler's
`OnRemoteFailure` event: the customer is returned to `/?signin=failed`, where the page says that the
sign-in did not go through and offers it again. The error text is not shown — it is written to the
log; unhandled, that return would end in an HTTP 500.

**Two operations on one ticket at once.** Both are created normally. The one confirmed first is
applied; the second then reports `applied` as well and changes nothing more — applying is
idempotent, and the end state it asks for has already been reached. A *new* request for that ticket
afterwards answers by its state: `404` when it was deleted, `409 already_resolved` when it was
closed.

## Prerequisites

- .NET SDK 10.0+
- A trusted development certificate — the application calls **itself** over HTTPS (the OIDC handler
  reads discovery, the backend calls `/connect/token`):

  ```bash
  dotnet dev-certs https --trust
  ```

- Credentials of at least one channel. A Telegram bot token from
  [@BotFather](https://t.me/BotFather) is the shortest path.
- For both contacts: a messaging channel that can hand over a phone number, **and** the Email channel
  switched on with its outbound mail (`Veriqa:Channels:Email:Outbound:*`) — Veriqa confirms an
  address by a mail it sends. With a messaging channel alone the phone is confirmed and the e-mail request comes back without an
  accepted address, so "Answer by e-mail" stays closed. With no enabled channel able to give a
  phone number the issuer answers the phone request itself, on `/connect/authorize`, with
  `400 no_channel_for_required_phone`; the sample does not intercept that.

## Running

Channels ship **disabled with empty credentials**, as everywhere in this gallery: a channel switched
on without its credentials fails the start, and `dotnet run` has to work out of the box. Switch on
the ones you have:

```bash
dotnet user-secrets --project samples/dotnet/showcase/support-desk set "Veriqa:Channels:Telegram:BotToken" "<token>"
dotnet user-secrets --project samples/dotnet/showcase/support-desk set "Veriqa:Channels:Telegram:Enabled" "true"
dotnet run --project samples/dotnet/showcase/support-desk
```

Open `https://localhost:7360`. The other channels are switched on the same way, by filling their
sections of `appsettings.json` and setting `Enabled` to `true`: `Veriqa:Channels:WhatsApp:*`,
`Veriqa:Channels:Email:*`, `Veriqa:Channels:Max:*`, `Veriqa:Channels:Instagram:*` and
`Veriqa:Channels:Messenger:*` (an enabled Messenger does not start without `GraphApiVersion` — the
channel has no default for it). The host registers all six adapters; which of the enabled channels
a sign-in page offers is decided by the request — see the next section.

**An empty sign-in page** — no way in offered at all — means that none of the channels the page was
asked for is enabled: either no channel is on at all, or the ones that are on are not in the
`ui_config` record the request named. That is the first thing to check.

### Email

`appsettings.json` ships Email in Pull mode — the customer types an address and opens the link from
the mail — with `InboundVerification` set to `shared_secret`. In Push mode, the one-tap mail
(`PushEnabled: true` and an `Inbound` block), the value depends on how the mail comes in:
`shared_secret` when an inbound mail provider posts it to the webhook, `outbound_fetch` when Veriqa
reads a mailbox over IMAP (`Inbound:Provider: ImapPolling`). A mismatched value does not stop the
host; it leaves a warning in the log at startup. The values for every channel and mode are in
[Inbound verification level](https://veriqa.app/docs/guides/channels#inbound-verification-level).

In either mode the mail alone does not sign the customer in. The link (Pull) or the sent mail
(Push) proves the mailbox, and then the sign-in window asks the question a messaging app chat
would get — `Confirm sign-in to "Helio Support" — request at {requested_at} from {browser} on {os}, {region}`, the
values filled in — and offers **Yes**, **No** and a countdown; the sign-in completes on **Yes**.
"Confirm e-mail" goes through the same step. This is the core's default for Email, not something the
sample adds: with `LoginConfirmation.Mode` set to `None` (or
`Veriqa:LoginConfirmation:ModeByChannel:email` set to `None`, for Email alone) the mail completes the sign-in by itself — see
[What the user sees after the tap](https://veriqa.app/docs/guides/channels#what-the-user-sees-after-the-tap).

### The set of channels follows the browser's language

The sign-in client has two `ui_config` records in `Veriqa:UiConfigurations:Records`, alike in
everything but `Channels` — which channels the sign-in page offers, and in what order:

| Record | Named when | `Channels`, in the order shown |
|---|---|---|
| `helio-sign-in-a` | the first language of `Accept-Language` is `ru` | `max`, `telegram`, `whatsapp`, `email` |
| `helio-sign-in-b` | any other language, and a request without the header | `telegram`, `whatsapp`, `instagram`, `messenger`, `email` |

The record is chosen by **the sample**, not by Veriqa: the handler's `OnRedirectToIdentityProvider`
event adds the `ui_config` parameter to the authorize request (`SignInUiConfig` in `Program.cs`).
"First" is the first entry of the header as the browser wrote it, compared by its primary subtag
without regard to case, so `ru` and `ru-RU` are the same answer and `en-US,en;q=0.9,ru;q=0.8` is
not. The two contact requests go through the same event and name the same record as the sign-in.
Both records are listed in the client's `AllowedUiConfigs`, and `helio-sign-in-b` is its
`DefaultUiConfig`. The names say nothing about a language on purpose: the code is visible in the
address.

The record chooses the channels and nothing else: for a browser in Russian the page itself stays in
English. Veriqa takes the language of the page from the locale files of the host
(`wwwroot/locales`), and the sample ships none. Put a `ru.json` there — the
`Veriqa.Core.ChannelLocales` package carries one — and the page follows the browser; see
[Localization](https://veriqa.app/docs/guides/customization#localization).

A record only **narrows** what the host has. A channel it lists and the configuration leaves off is
not shown; a record none of whose channels is on leaves the page empty — with
`Veriqa:Channels:Max` the only section switched on, a browser in English gets exactly that.

It is **presentation, not a restriction**. The record travels as a request parameter, so anyone may
put the other allowed code into the address and get the other set. To keep a channel away from a
client, do not enable it.

The record behind a ticket confirmation, `helio-ticket-action`, lists no channels: a step-up has to
reach whichever channel the session came through.

Over the sign-in card stands a row of two: `Veriqa:AuthPageDesign:LogoUrl` points at
`wwwroot/veriqa-mark.svg` — the mark of Veriqa, the same rounded square the core draws itself — and
`Veriqa:AuthPageDesign:BrandName` puts the desk's name, Helio, beside it. Name neither, and the page
shows the product's mark alone. The attribution line under the card stays either way: it is not a
branding setting.

### Behind a tunnel

Not needed for a run on one computer — the section is read in an ordinary browser. A tunnel is what
signing in from a phone and hop mode (below) need, and the wiring is the same as in `streaming-tv`:

```bash
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://localhost:5360 \
Veriqa__OpenIddict__Server__Issuer=https://<public-host> \
Veriqa__OpenIddict__Clients__0__AllowedRedirectUris__0=https://<public-host>/signin-oidc \
SupportDeskSample__Authority=https://<public-host> \
Veriqa__Channels__Telegram__UpdateMode=Webhook \
Veriqa__Channels__Telegram__InboundVerification=shared_secret \
Veriqa__HopMode__Enabled=true \
Veriqa__Channels__Telegram__WebhookBaseUrl=https://<public-host> \
Veriqa__Channels__Telegram__WebhookSecretToken=<any-long-random-string> \
dotnet run --project samples/dotnet/showcase/support-desk
```

Set all of them together, and keep the environment at `Development`: outside it Veriqa asks for
production PFX signing and encryption certificates. A public address is configuration, not a
different environment.

**Hop mode.** The snippet also switches [hop mode](https://veriqa.app/docs/concepts/hop-mode) on: the QR code carries a short
one-time link to the issuer instead of the channel link, and the phone opens a page of Veriqa that
takes it on to the messaging app or the mail app. The code is lighter to scan, the sign-in page shows
that it was scanned as soon as the phone opens the link, and the Email QR code no longer carries a
bare `mailto:` link — the camera of some phones (seen on a Xiaomi) opens one in the browser, where
no mail can be written. Hop builds every link on the issuer: with hop on, the issuer has to be stated
as an absolute `https` address or the host does not start, and the phone has to reach it, which it
cannot do with `localhost`. That is why the sample switches hop on here and not in
`appsettings.json`: `dotnet run` without a tunnel keeps the direct links.

**Telegram update mode.** Behind a tunnel the channel runs in `Webhook` mode and declares
`shared_secret`; without one it runs in `Polling` with `outbound_fetch`, which is what
`appsettings.Development.json` ships. Switching back to `Polling` while the
bot still has a webhook registered silently delivers nothing — remove it first with `deleteWebhook`
in the Bot API.

## The page

Plain responsive HTML, CSS and JavaScript, with a dark variant through
`prefers-color-scheme`. Nothing is loaded from outside the origin — no CDN fonts, no image files;
the three-step drawing on the public page is inline SVG built in the script.

The syntax level is **ES2018**, pinned for `samples/dotnet/showcase/**/*.js` in the root
`eslint.config.mjs`. This page would survive a newer level on its own; its neighbor running on
Tizen 5.5 and webOS 5 televisions would not, and the two samples share their plumbing.

```bash
npx eslint samples/dotnet/showcase
```

## Further reading

- Progressive claim enrichment — <https://veriqa.app/docs/concepts/claim-enrichment>
- Server-to-server confirmation API — <https://veriqa.app/docs/reference/confirmation-api>
- Linking from your server — <https://veriqa.app/docs/scenarios/channel-linking>
