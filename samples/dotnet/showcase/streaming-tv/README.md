# Veriqa showcase — Nova Stream, a streaming service on a television

A sample product — a streaming service on a television — that presents these Veriqa capabilities:

- **Sign-in from the couch, on a device with no input.** One press of the remote — `Enter` on
  "Sign in", where the focus already stands — and the rest happens on the phone: the television shows
  the QR codes of all its channels at once, and the viewer approves the sign-in in a messaging app. No
  keyboard, no password, no typing with the remote, no tab to switch.
- **One sign-in page for the television and the phone.** The television gets the QR codes; a phone
  that opens the same page gets a button per channel and no QR. Two settings of the page — no code.
- **Registration data without a form.** The viewer's card — name, picture, @username, masked phone
  or e-mail address — is filled from the claims the channel already knows. A returning viewer lands
  straight in the catalog.
- **Confirmation on a device with no input (step-up).** Buying a title takes one scan: the QR code of
  the purchase leads straight into the messaging app the viewer signed in with, with no channel to
  choose. The purchase is approved there by the viewer who signed in; a confirmation from anybody
  else buys nothing.
- **Enrichment on the phone.** What no channel knows — the date of birth and the favorite genre —
  Veriqa asks for on a form on the phone (in the channels that can open it there), inside the
  sign-in, and remembers. The service closes the 18+ titles to an under-age viewer and puts the
  favorite genre first.
- **Configuration per audience.** The set of channels on the sign-in page follows the browser
  language: a Russian-language browser gets one set, any other browser gets another that includes
  Instagram and Messenger. Each set is one `ui_config` record — no code change.

A sign-in and a purchase leave different receipts in the chat. A sign-in's question is replaced by
"Sign-in to Nova Stream confirmed ✅" (`ReplacePrompt`, the global `OutcomeNotice` section). A
purchase keeps its question and gets the receipt as a message of its own (`NewMessage`, stated in
the backend client's entry — the client that creates the confirmations), so what was approved
stays readable.

The interface belongs to the fictional service: a ten-foot layout, remote-control navigation, and no
interaction that needs a pointer. The sign-in page and the confirmation window are Veriqa's own
screens, shown as they are — the sample does not dress its own pages up as the product's.

## How it is put together

One process plays three roles, as in [`inproc/step-up`](../../inproc/step-up/README.md):

| Role | What it is |
|---|---|
| Veriqa issuer (inproc) | `AddVeriqaAuthServer` → `UseVeriqaAuthServer` → `MapVeriqaAuthServer` |
| Relying party | the standard `AddOpenIdConnect` handler, pointed at this very process |
| Backend | `VeriqaConfirmationClient`, calling the public confirmation API over HTTP |

Splitting the three into separate hosts changes nothing but the addresses — see
[When the issuer moves out of the process](#when-the-issuer-moves-out-of-the-process).

### What Veriqa collects inside the sign-in

No channel gives a date of birth or a favorite genre, so the sample declares both in the
`Veriqa:ScopesClaims` catalog of `appsettings.json`, and Veriqa asks for them on a form of its own,
in the same transaction, before it issues the tokens. The sample has no second authorize request
and no form of its own:

| Claim | Scope | `Source` | `Level` | Field of the form |
|---|---|---|---|---|
| `birthdate` | `profile` | `Collected` | `Required` | `Format: Date`, labeled "Date of birth" |
| `preferred_genre` | `preferences` | `Collected` | `Optional` | `Format: Choice`, labeled "Favourite genre"; the options are the genres of the catalog |

`birthdate` is the standard OIDC claim (`YYYY-MM-DD`); `preferred_genre` and its scope `preferences`
are the sample's own. The handler asks for `preferences` on top of its usual scopes, and the scope
stands in `AllowedScopes` of `nova-stream-tv`. Both claims arrive in the ID token.

**The catalog lists every scope.** A catalog that is not empty **replaces** the product's own set of
scopes as a whole, so it names all the scopes the two clients have in `AllowedScopes` — `profile`,
`email`, `phone`, `channel`, `avatar`, `offline_access` and `preferences`; a scope left out stops
the start instead of being lost quietly. The standard ones are stated by name alone: their claims
(`name`, `picture`, `phone_number` and the rest) are issued by the scope being granted, without a
row in the catalog.

**What the viewer sees at each level.** `Required`: the sign-in does not complete without the date.
`Optional`: the genre is asked once and may be skipped; a
skipped genre is remembered as a refusal, the token then arrives without `preferred_genre`, and the
question does not come back. The labels of the field and of the options are English text: a
localization key with no translation is shown as written. Each option `Value` is a genre of
`CatalogStore.cs`, letter for letter — the list in `appsettings.json` and the titles there are two
halves of one declaration, and a value that matches no title recommends nothing.

**The form is on the phone.** `nova-stream-tv` sets `ClaimCompletionFormSurface` to
`CrossDevicePage`: the form opens on the device that confirmed the sign-in, by a link button the
channel sends into the chat, and not on the television, where a date would be typed with a remote.
The phone has to be able to open the address of the issuer, so this needs the run
[behind a tunnel](#behind-a-tunnel). The form still lands **on the television** — on the sign-in
page — whenever the core has to fall back:

- the channel cannot show a link button. Of the six channels of the sample Telegram and MAX can;
  WhatsApp, Instagram and Messenger cannot;
- Email in Pull mode (`appsettings.json`) shows the form on the confirmation page of its mail, which
  is the phone as well; in Push mode (`appsettings.Development.json`) it has no such page and falls
  back;
- the issuer is not an absolute HTTPS address, or the button was not delivered.

The fallback is the core's own and the sample does not intercept it. Entering a date with a remote
is not something the sample arranges: that is a known limitation.

**What is remembered.** The collected claims store keeps what Veriqa collected, and which optional
claim the viewer declined, so a returning viewer is not asked again. Veriqa ships an EF Core
implementation of the port and no in-memory one; the sample brings
`InMemoryCollectedClaimsStore.cs` and registers it with
`UseCollectedClaimsStore<InMemoryCollectedClaimsStore>()`. Three properties to know:

- records follow the `sub`, and one person has a different `sub` in every channel — a viewer who
  comes back through another channel is asked again. That is a property of the store, not of the
  sample;
- the store is one per tenant, shared by all its applications;
- here it lives in the memory of the process: a restart empties it and the form asks again. The
  records sit in a static field, because the registration is scoped and the engine opens a scope of
  its own for every operation — an instance does not live until the next sign-in.

A purchase never brings the form up: `nova-stream-backend` creates its confirmations with
`openid channel` and asks for neither `profile` nor `preferences`.

**What the service does with the two claims is decided on the server:**

- the age is worked out from `birthdate` there, in full years on the current UTC day
  (`ViewerProfile.AgeOn`), so it grows on the birthday itself. The page is given the age, never the
  date — the same principle as masking the phone number;
- two titles of the catalog, "Salt and Iron" and "Last Train West", are 18+. Such a title is
  closed to a viewer under 18 **and to a viewer whose age is not known** — `birthdate` absent, not a
  `YYYY-MM-DD` date, or a date in the future. `Required` does not rule that out: the form can be
  switched off in configuration (`ClaimCompletionFormEnabled`), and the service has to survive a
  token without the claim. One rule, `CatalogTitle.IsRestrictedFor`, serves the catalog and the
  purchase alike; a closed title stays reachable with the arrows, carries an "18+ only" mark in
  place of "Buy", and when selected opens a window that says why it is closed;
- the titles of the favorite genre come first and carry a "★ For you" mark; within them and within
  the rest the order of the catalog is kept. A viewer with no genre — or with a value that names
  no genre of the catalog — sees the catalog as it is, with no marks.

The other way of getting a missing claim — one more authorize request, at the moment a feature needs
the contact — is what [Helio](../support-desk/README.md) shows. Both are described in
[Progressive claim enrichment](https://veriqa.app/docs/concepts/claim-enrichment).

### The step-up, exactly

`POST /api/catalog/{titleId}/purchase` creates a confirmation that names **one** expected identity —
the one belonging to the channel the current session came through:

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
`nova-stream-backend` client — two halves of one declaration, because a confirmation naming an
undeclared type is refused with `candidate_type_undeclared`.

The confirmation is created with the channel of the session as well, so the QR code of the purchase
window leads straight into that channel, whichever of the six it is — the channel-choice page is
never shown.

The verdict is then:

| Outcome from Veriqa | `matched_type` | State of the purchase |
|---|---|---|
| `confirmed` | the expected type | `purchased` — the title is in the library |
| `confirmed` | absent or another type | `mismatched` — nothing is bought |
| `declined` / `expired` / `failed` | — | `refused` — nothing is bought |

Which title a transaction pays for is remembered **on the server**: the page is never asked, so no
browser can buy one title by confirming another.

Closing the purchase window — **Cancel** or `Backspace` — only stops the wait. Veriqa has no call
that withdraws a confirmation, so the transaction stays `pending` until it expires, and the question
stays in the chat. The backend reads the verdict only while the window polls for it: a confirmation
that arrives after the window closed is never read, and nothing is bought — although the chat still
gets its receipt, as after any answer. Buying the title again creates a new transaction. This was
read from the code, not run.

### What stays on the server

`channel_user_id` never leaves it in full — it addresses the step-up. The card shows a Telegram user
id masked (`12*****89`, `ViewerProfile.MaskIdentifier`), and the phone number is masked the same way
(`ViewerProfile.MaskPhone`) **before** either is put into a response, so the browser is never handed
the full value; masking in the page would mean shipping it to the page first.

`birthdate` stays on the server as well: the card and the catalog are given the age and what
follows from it, and nothing under `wwwroot` ever reads the date.

## The API

Every route but `GET /api/session` answers an anonymous caller `401 {"error":"unauthenticated"}` —
these are read by `fetch`, and a sign-in page arriving as the body of an XHR helps no one.

```
GET  /api/session              → { authenticated, welcomeSeen, profile }
POST /api/session/welcome-seen → 204
GET  /api/catalog              → { titles: [ { id, title, genre, year, price, currency, owned,
                                                minimumAge, restricted, recommended } ] }
POST /api/catalog/{id}/purchase→ { transactionId, qr, channelType }
                                 404 unknown title · 409 {"error":"already_owned"}
                                 403 {"error":"age_restricted"}
GET  /api/purchases/{txn}      → { transactionId, titleId, state, outcome }
                                 404 — unknown, or started by another session
```

`state` is `pending` · `purchased` · `refused` · `mismatched`. A refusal from Veriqa is passed
through with its own status and body.

`profile` carries two fields that come from no channel: `age` — a number, or `null` when it cannot
be told — and `preferredGenre` — a genre of the catalog, or `null`. The date of birth itself is
not in the answer. In the catalog `minimumAge` is `0` for a title anyone may buy, `restricted`
says that this viewer's age — or the lack of one — closes the title, and `recommended` marks the
titles of the favorite genre, which come first. `403 age_restricted` is checked after `404` and
`409 already_owned` and before any confirmation is created; a purchase already waiting for its
confirmation is not looked at again. A page that gets the refusal for a title it showed as open —
the session changed in another tab — reads the catalog again.

A sign-in that comes back with an error (`error=server_error`, for one) is caught by the handler's
`OnRemoteFailure` event: the viewer is returned to `/?signin=failed`, where the page says that the
sign-in did not go through and offers it again. The error text is not shown — it is written to the
log; unhandled, that return would end in an HTTP 500.

## Prerequisites

- .NET SDK 10.0+
- A trusted development certificate — the application calls **itself** over HTTPS (the OIDC handler
  reads discovery, the backend calls `/connect/token`):

  ```bash
  dotnet dev-certs https --trust
  ```

- Credentials of at least one channel. A Telegram bot token from
  [@BotFather](https://t.me/BotFather) is the shortest path.

## Running

Channels ship **disabled with empty credentials**, as everywhere in this gallery: a channel switched
on without its credentials fails the start, and `dotnet run` has to work out of the box. Switch on
the ones you have:

```bash
dotnet user-secrets --project samples/dotnet/showcase/streaming-tv set "Veriqa:Channels:Telegram:BotToken" "<token>"
dotnet user-secrets --project samples/dotnet/showcase/streaming-tv set "Veriqa:Channels:Telegram:Enabled" "true"
dotnet run --project samples/dotnet/showcase/streaming-tv
```

Open `https://localhost:7350`. The other channels are switched on the same way, by filling their
sections of `appsettings.json` and setting `Enabled` to `true`: `Veriqa:Channels:WhatsApp:*`,
`Veriqa:Channels:Email:*`, `Veriqa:Channels:Max:*`, `Veriqa:Channels:Instagram:*` and
`Veriqa:Channels:Messenger:*` (an enabled Messenger does not start without `GraphApiVersion` — the
channel has no default for it). The host registers all six adapters; which of the enabled channels
a sign-in page offers is decided by the request — see the next section.

**An empty sign-in page** — no way in offered at all — means that none of the channels the page was
asked for is enabled: either no channel is on at all, or the ones that are on are not in the
`ui_config` record the request named. That is the first thing to check.

### The set of channels follows the browser's language

The sign-in client has two `ui_config` records in `Veriqa:UiConfigurations:Records`, alike in
everything but `Channels` — which channels the sign-in page offers, and in what order:

| Record | Named when | `Channels`, in the order shown |
|---|---|---|
| `nova-sign-in-a` | the first language of `Accept-Language` is `ru` | `max`, `telegram`, `whatsapp`, `email` |
| `nova-sign-in-b` | any other language, and a request without the header | `telegram`, `whatsapp`, `instagram`, `messenger`, `email` |

The record is chosen by **the sample**, not by Veriqa: the handler's `OnRedirectToIdentityProvider`
event adds the `ui_config` parameter to the authorize request (`SignInUiConfig` in `Program.cs`).
"First" is the first entry of the header as the browser wrote it, compared by its primary subtag
without regard to case, so `ru` and `ru-RU` are the same answer and `en-US,en;q=0.9,ru;q=0.8` is
not. Both records are listed in the client's `AllowedUiConfigs`, and `nova-sign-in-b` is its
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

It is **presentation, not a restriction**. The record travels as a request parameter, so a viewer
may put the other allowed code into the address and get the other set. To keep a channel away from
a client, do not enable it.

The record behind a purchase, `nova-purchase`, lists no channels: a step-up has to reach whichever
channel the session came through.

### The two channels of the shop window

`appsettings.Development.json` switches WhatsApp and Email on with values that are not credentials
(`sample-display-only`, `signin@sample.invalid`). They exist so a showcase run shows what a
multi-channel sign-in page looks like — a strip of channels to choose from, which a page carrying
one channel cannot show. **Neither of them can carry a login through**: the message goes to a phone
number and a mailbox that do not exist, and Telegram is the one channel that works. Both stay off
in `appsettings.json`, so a deployment that copies the sample gets nothing of this.

Email runs in **Push** mode here rather than Pull. Pull asks the person to type their address, and
across a room there is no keyboard to type it with; Push addresses the letter for them, so the panel
carries a QR code like the other two channels instead of a form.

To sign in by mail for real, point Email at a mailbox that exists. Push needs inbound mail as well,
and the value of `InboundVerification` depends on how that mail comes in:
`shared_secret` when an inbound mail provider posts it to the webhook, `outbound_fetch` when Veriqa
reads a mailbox over IMAP (`Inbound:Provider: ImapPolling`). A mismatched value does not stop the
host; it leaves a warning in the log at startup. The values for every channel and mode are in
[Inbound verification level](https://veriqa.app/docs/guides/channels#inbound-verification-level).

Whichever the mode, the mail alone does not sign the viewer in. The link (Pull) or the sent mail
(Push) proves the mailbox, and then the sign-in window — on the television, the sign-in page — asks
the question a messaging app chat would get — `Confirm sign-in to "Nova Stream" — request at
{requested_at} from {browser} on {os}, {region}`, the values filled in — and offers **Yes**, **No** and a countdown; the
sign-in completes on **Yes**. This is the core's default for Email, not something the sample adds:
with `LoginConfirmation.Mode` set to `None` (or
`Veriqa:LoginConfirmation:ModeByChannel:email` set to `None`, for Email alone) the mail completes the sign-in by itself — see
[What the user sees after the tap](https://veriqa.app/docs/guides/channels#what-the-user-sees-after-the-tap).

### Wording and brand of the sign-in page

The same page of the core opens for two different clients, and each words it its own way. The
sign-in is started by `nova-stream-tv`, the shared QR of a purchase by `nova-stream-backend`; each
has records of its own in `Veriqa:UiConfigurations:Records` — the two of the sign-in carry the same
title — so the sign-in reads "Sign in with your messaging app" and the purchase "Confirm your purchase".
Beyond the list of channels the records state the title only:
an instruction stated by a record is a single wording, while the core's own comes in three — for a
page with the QR and the button, with the QR alone and with the button alone — and the page shows
the one that matches what is on screen on a television or a phone.

The sample states no instruction of its own and ships no locale file. Where the QR is on screen the
channel buttons are hidden, and the core's wording promises the QR alone; on a phone, where the
buttons replace the QR, it names the button.

Over the card stands a row of two: `Veriqa:AuthPageDesign:LogoUrl` points at `wwwroot/veriqa-mark.svg`
— the mark of Veriqa, the same rounded square the core draws itself — and
`Veriqa:AuthPageDesign:BrandName` puts the shop's name beside it. Name neither, and the page shows
the product's mark alone, which is right for a bare installation and wrong for a sample pretending
to be a streaming service. The attribution line under the card stays in either case — it is not a
branding setting.

### The sign-in page on a ten-foot screen

The television shows no channel buttons — neither the "Sign in with …" buttons of the messaging apps nor "Send
confirmation email": across a room nobody can follow them, and the way in is the QR code scanned with
a phone.
That is a setting of the page, `Veriqa:AuthPageDesign:ChannelButton:ShowChannelButton`, which the
sample sets to `MobileOnly`: the buttons are shown on a phone and nowhere else.

A phone that opens the page keeps the buttons and loses the QR instead. The sample sets
`Veriqa:AuthPageDesign:QrCode:ShowQrCode` to `DesktopOnly` — the core's own default, stated here
because it is the other half of the same arrangement. The core marks a phone on the page
(`data-device="mobile"` — a coarse pointer and a phone User-Agent together) and lays the channels
out there as buttons, one per channel, with no QR. A television is not a phone by either sign and
keeps its QR codes. Between the two settings every device is left with exactly one way in; a pair
that would leave a device with none is not applied by the core — it shows the buttons everywhere and
writes a warning.

The television shows the QR codes of all its channels at once, side by side, each under the name of
its channel. By default the core puts several channels behind tabs, one QR code on screen at a time
— and nobody walks up to a television to switch a tab. The sample sets
`Veriqa:AuthPageDesign:ChannelLayout` to `Grid`: the card of the page grows to hold a row of channel
panels, never past the screen, and a row that does not fit wraps. The setting arranges only what
would otherwise be tabs, so the phone above — channels as buttons, no QR — looks the same under
either value.

The wording follows both settings: the core picks the instruction by whether the QR is shown and by
whether the button is. With several channels on, the television reads "Choose a channel, then scan its
QR code to sign in" and the phone "Choose a channel and tap its button to sign in"; with a single one
the page names it — "Scan the QR code to sign in with Telegram". The sample rewords nothing.

`wwwroot/veriqa-auth-page-tv.css` does the two things no setting does. It is attached through
`Veriqa:AuthPageDesign:CustomCssPath` as a rooted path, so it follows the sample behind a tunnel
without a variable of its own. It enlarges the card on screens 1600 CSS pixels wide or wider: the
core draws the card in pixels, and a full-HD television renders 1920 of them at a device pixel ratio
of 1, so the page is otherwise a stamp in the middle of the screen. And it restates the card's width
and padding for engines older than the core's own CSS — webOS 4.x runs Chromium 53, which reads
neither `min()` nor logical padding.

The price of the sheet is real: attaching ANY sheet makes the core ignore the design preset
(CFG-023) and render its base look.

The restated width is for the one-column card only. The card of the grid layout is sized by the core
in properties every engine reads, so the sheet leaves it alone — restating 420 pixels there would put
the row of QR codes back into a column on exactly the old engines the restatement is for. That part
has been read through, not run: this sample has not been checked on an engine that old.

### On the television

The remote:

| Key | What it does |
|---|---|
| arrows | move the focus |
| `Enter` | select |
| `Backspace`, Tizen `10009`, webOS `461` | close the purchase window |
| `F` (desktop) | full screen — the Fullscreen API needs a gesture of the person's own |

There is no full-screen button on the page: on a television the page is full-screen already, and a
button standing over every screen got in the way. Full screen from `F` lasts only until the next
page: the Fullscreen API belongs to one document, so going to the sign-in page leaves it, and nothing
can carry it over. The browser's own full-screen
control (in the toolbar of the webOS browser) survives navigation — use that one for a filmed run.

The QR of the purchase window is drawn at 12 px per module
(`Veriqa:AuthPageDesign:QrCode:PixelsPerModule`) rather than the default 6: it is scanned off the
screen with a phone camera, from across the room.

## Behind a tunnel

A television cannot reach `localhost`. Put the sample behind an HTTP tunnel — nothing in the code
changes, only the environment:

```bash
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://localhost:5350 \
Veriqa__OpenIddict__Server__Issuer=https://<public-host> \
Veriqa__OpenIddict__Clients__0__AllowedRedirectUris__0=https://<public-host>/signin-oidc \
StreamingTvSample__Authority=https://<public-host> \
Veriqa__Channels__Email__PublicBaseUrl=https://<public-host> \
Veriqa__Channels__Telegram__UpdateMode=Webhook \
Veriqa__Channels__Telegram__InboundVerification=shared_secret \
Veriqa__HopMode__Enabled=true \
Veriqa__Channels__Telegram__WebhookBaseUrl=https://<public-host> \
Veriqa__Channels__Telegram__WebhookSecretToken=<any-long-random-string> \
dotnet run --project samples/dotnet/showcase/streaming-tv
```

Set **all** of them together. With the issuer left at `localhost`, discovery hands the television an
address it cannot reach and the sign-in goes nowhere.

The environment stays `Development` on purpose: outside it Veriqa asks for production PFX signing
and encryption certificates. A public address is configuration, not a different environment.

Two things in `Program.cs` serve the tunnel, and both are commented there:
`ForwardedHeadersOptions` with `XForwardedFor | XForwardedProto` and cleared `KnownProxies`
(the connector forwards over loopback), and `app.UseForwardedHeaders()` **before**
`app.UseVeriqaAuthServer()`. `UseHttpsRedirection` is never called — the tunnel terminates TLS and
the redirect would loop.

**Caching behind a tunnel.** The page's files go out with `Cache-Control: no-cache`, but the edge
of a tunnel may rewrite that header and keep `app.js` for hours; a television then runs an old script
against a new page and fails at the first element that no longer exists. `index.html` therefore names
both files with a `?v=` suffix — raise it whenever `app.js` or `styles.css` changes.

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
`shared_secret` (above); without one it runs in `Polling` with `outbound_fetch`, which is what
`appsettings.Development.json` ships. Switching back to `Polling`
while the bot still has a webhook registered silently delivers nothing — remove it first with
`deleteWebhook` in the Bot API.

## When the issuer moves out of the process

Here the issuer is embedded. Run it as a separate host — the published Docker image of Veriqa, with
the service left as a plain relying party and a backend — and almost everything this sample adds on
the issuer's side travels as configuration: it lies under `Veriqa:*` in `appsettings.json` and goes
to the container as environment variables, key for key:

- `Veriqa:ScopesClaims` — the catalog with `birthdate` and `preferred_genre`;
- `Veriqa:UiConfigurations:Records` — the two sign-in records with their `Channels`, and the record
  of the purchase;
- `Veriqa:OpenIddict:Clients` — `AllowedScopes`, `AllowedUiConfigs`, `DefaultUiConfig`,
  `ClaimCompletionFormSurface`, `IdentityMatchComparableTypes`;
- `Veriqa:Channels:*`, `Veriqa:MessageTemplates`, `Veriqa:AuthPageDesign`.

One thing is code rather than configuration: the registration of the in-memory collected claims
store. A container gets the same behavior from a key, `Veriqa:CollectedClaims:Store:Enabled` set to
`true`; the store then lives in the database of the transaction store
(`Veriqa:TransactionEngine:Store:ConnectionString`), and the start stops without one.

The roles of the relying party and of the backend need no change. They learn about the viewer from
the tokens and from `/connect/userinfo` alone and call no service of the core in the process; the
name of the `ui_config` parameter is a constant of the sample for that very reason.

## Television browsers

Everything under `wwwroot` targets **ES2018** and the CSS of Chromium 68, with fallbacks for the
Chromium 53 of webOS 4.x where a layout depends on it (the catalog's grid, the sign-in card). The
same engine is why the sign-in's correlation and nonce cookies go out `SameSite=Lax`: Chromium 51-66
rejects `SameSite=None`, and the return from sign-in then fails with "Correlation failed".
Tizen 5.5 and webOS 5 ship Chromium 68-69, where `?.`, `??`, top-level `await` and an optional catch binding are *parse* errors:
the file does not load at all, and nothing on screen says why. `:has()`, `:is()`, `:focus-visible`,
CSS nesting, logical properties, `clamp()`, `aspect-ratio`, flex `gap` and the `<dialog>` element
are out for the same reason — they fail quietly instead.

The limit is checked rather than remembered: the root `eslint.config.mjs` pins
`ecmaVersion: 2018` for `samples/dotnet/showcase/**/*.js`.

```bash
npx eslint samples/dotnet/showcase
```

Nothing is loaded from outside the origin — no CDN fonts, no image files. Covers are generated as
inline SVG from a hash of the title's id, so the same title always gets the same cover.

## Further reading

- Progressive claim enrichment — <https://veriqa.app/docs/concepts/claim-enrichment>
- Server-to-server confirmation API — <https://veriqa.app/docs/reference/confirmation-api>
- Linking from your server — <https://veriqa.app/docs/scenarios/channel-linking>
