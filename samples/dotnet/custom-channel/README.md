# Custom channel adapter sample (.NET)

A third-party channel plugged into embedded Veriqa through the public SPI — no core fork.

| Project | What it is | Veriqa dependencies |
|---|---|---|
| `adapter/` | The channel adapter itself: `IChannelAdapter` + optional `IChannelDisplayMetadata` | `Veriqa.Core.Contracts` (MIT) only |
| `host/` | Reference wiring: registration, core-level declaration, webhook route, sign-in button | the full embedded set, including the MPL-2.0 `Veriqa.Core.ChannelAdapter` |

The split is the point: an adapter builds against the MIT contracts package alone. Only the host —
the application that embeds the server — needs the server core.

## Run it

```bash
cd samples/dotnet/custom-channel/host
AcmeChat__WebhookSecret=dev-secret dotnet run
```

The secret is read from configuration; keep it in `dotnet user-secrets` or an environment variable,
never in `appsettings.json`.

Open the sign-in window:

```
https://localhost:7320/connect/authorize?client_id=my-app&redirect_uri=https%3A%2F%2Flocalhost%3A7320%2Fsignin-oidc&response_type=code&scope=openid%20profile&state=st1&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256
```

The window shows an **Acme Chat** button whose deep link carries the transaction id
(`?tx=…`). The real platform would open its app there; the sample simulates the platform by
posting the webhook by hand:

```bash
curl -k -X POST https://localhost:7320/api/channels/acme-chat/webhook \
  -H "X-Acme-Signature: dev-secret" \
  -H "Content-Type: application/json" \
  -d '{"transaction_id":"<tx from the deep link>","user_id":"u-42","display_name":"Alice Acme"}'
```

The transaction moves to confirmed and then completed, and the browser finishes the OIDC flow.

### Start by a code

Some platforms have no link that both opens the chat with the bot and carries a payload. Such a
channel declares a start by code: Veriqa gives the transaction a short code, shows it on the sign-in
page, and the user sends it to the bot. Turn it on and restart the host:

```bash
AcmeChat__WebhookSecret=dev-secret AcmeChat__StartByCode=true dotnet run
```

Open the same sign-in address. In place of the usual button the window shows the message to send —
`/veriqa 483 920`, the digits will differ — and a **Copy and open Acme Chat** button: it puts the
message on the clipboard and opens the chat. The deep link carries no `tx` now: it only opens the
chat. Play the platform that received the message — the adapter of a real platform strips its command
and hands the code over:

```bash
curl -k -X POST https://localhost:7320/api/channels/acme-chat/webhook \
  -H "X-Acme-Signature: dev-secret" \
  -H "Content-Type: application/json" \
  -d '{"code":"<code from the sign-in page>","user_id":"u-42","display_name":"Alice Acme"}'
```

The QR code of the window does not carry the link of the channel any more: a phone that scanned it
would land in a chat with nothing to send. It carries a hop link to this host,
`https://localhost:7320/r/…`, and that link opens a page with the same message and button, with no
automatic launch — whether or not hop mode is switched on. Open the address the QR code holds in a
browser to see the page. The QR code gets such a link because the host declares the channel
(`CoreChannelDeclaration` in `host/Program.cs`, next to `AddChannel`) and states its issuer as an
HTTPS address (`Veriqa:OpenIddict:Server:Issuer` in `host/appsettings.json`). To scan the code with a
real phone, put the host behind a tunnel and set the issuer to the public address.

The code may be sent as shown, `483 920`, or without the space. The sign-in goes on exactly as after
a start by the link. A code works once and lives as long as its transaction: the same request sent
again starts nothing, and the bot answers it — as it answers any code no sign-in answers to — with
the text the registration declares for it (`UnmatchedCodeReplyText` in `host/Program.cs`; the sample
adapter writes the message to the log instead of sending it).

### A code as a fallback beside the link

Other platforms have a link that carries the transaction but do not always deliver it. Such a
channel keeps its link and declares the code as a second way to start:

```bash
AcmeChat__WebhookSecret=dev-secret AcmeChat__CodeFallback=true dotnet run
```

The window shows the usual **Acme Chat** button with the `?tx=…` link. The block
*Send the code in the chat with the bot if the bot has not sent you a confirmation* with the code and
a **Copy** button appears under it 8 seconds after the page loads — when the link has had time to
fail. It goes away, and is not offered again, once the channel has answered: the start reached the
server and the question went into the chat. The caption names the chat when `BotName` is stated in
the section of the channel (`Veriqa:Channels:acme-chat:BotName`), and
`Veriqa:Channels:acme-chat:SpareStartCodeEnabled: false` switches the block off.
The sign-in starts by either request — the one with `transaction_id` above or the one with `code` —
whichever arrives first. With both settings on, `StartByCode` wins.

## What the SPI guarantees

- `POST /api/channels/{channelType}/webhook` (plus the tenant-segment variant) is mapped for the
  channel with the same rate-limit policy and the same 1 MB body limit as the built-in channels.
- `ValidateWebhookAsync` runs before the adapter's processing — but after the tenant demultiplexer,
  which answers 403 on an unknown tenant segment without calling the adapter, and after the single
  read of the body into the envelope.
  A rejection — and an exception thrown by the validation itself — answers 403.
- After a successful validation the answer is always 200, even if the adapter fails or throws. A
  body over the limit is answered 200 as well: reading it is Veriqa's job and its failure is not a
  validation verdict.
- Both `ValidateWebhookAsync` and `ProcessInboundEventAsync` receive the same
  `ChannelInboundRequest` envelope: the HTTP method, the **raw body bytes** (a signature is computed
  over exactly those, so nothing in between can swallow a byte-order mark), the headers and the
  query parameters (names compared case-insensitively, repeated values joined with `", "`).
  Decoding and parsing the body are the adapter's job — the core imposes no body format.
- The channel type must match `\A[a-z][a-z0-9-]{0,63}\z`; a violation, a duplicate, a collision with
  a built-in channel or a mismatch with the adapter's own `ChannelType` fails the startup with an
  explicit message.
- The registration ↔ adapter cross-check runs on host start, so it also covers a channel registered
  with `mapWebhook: false`. It is exact for native adapters and per-adapter class proxies, and
  best-effort under a sweeping decorator over every `IChannelAdapter` (Scrutor `Decorate`, a Castle
  interface proxy on the whole set), which hides the adapters' runtime types.
- Display metadata is degraded, never trusted: a blank `DisplayName` — or one over 32 characters —
  falls back to the channel type, and glyph markup outside the allowed shape elements (or over 4096
  characters) is dropped (the channel renders without an icon).

Known limitation of this SPI version: the generic route accepts **POST only** — a GET verification
handshake (the WhatsApp Hub Challenge analogue) is not supported.

The full guide: [Custom channel adapter](https://veriqa.app/docs/guides/custom-channel-adapter).
