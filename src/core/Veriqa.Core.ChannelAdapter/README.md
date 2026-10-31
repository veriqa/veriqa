# Veriqa.Core.ChannelAdapter

**One way to plug in every messaging app.** Telegram, WhatsApp, Instagram, Messenger, WeChat, Viber,
MAX and Email are switched on by configuration, and a channel of your own plugs in the same way,
without changes to the core.

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Channels](https://veriqa.app/docs/guides/channels).

## About this package

The channel contour of Veriqa: the registration SPI every channel plugs into, the webhook
pipeline and the settings resolution seam. The channels themselves — the ones shipped with the
product and a third-party one alike — live in their own packages and bring their own platform SDK,
so this package depends on no channel platform at all.

## Packages

Two ways to install the channels, and both are ordinary NuGet:

```bash
# the base set in one package: Telegram, WhatsApp, Email (plus this contour)
dotnet add package Veriqa.Core.BaseChannels
# the other channel packages (Max, Instagram, Messenger, WeChat, Viber) are outside the
# metapackage — install the ones you need explicitly
dotnet add package Veriqa.Core.ChannelAdapter.Max

# or take only the channel you actually use — nothing else arrives with it
dotnet add package Veriqa.Core.ChannelAdapter.Email
```

| Package | Contents | Platform dependency |
|---|---|---|
| `Veriqa.Core.ChannelAdapter` | the contour: `AddChannel`, the webhook pipeline, channel settings | none |
| `Veriqa.Core.ChannelAdapter.Telegram` | the Telegram channel | `Telegram.Bot` |
| `Veriqa.Core.ChannelAdapter.WhatsApp` | the WhatsApp channel (Meta Cloud API or Twilio) | none — plain HTTP |
| `Veriqa.Core.ChannelAdapter.Max` | the MAX channel | `Max.BotClient` |
| `Veriqa.Core.ChannelAdapter.Instagram` | the Instagram channel | none — plain HTTP |
| `Veriqa.Core.ChannelAdapter.Messenger` | the Messenger channel | none — plain HTTP |
| `Veriqa.Core.ChannelAdapter.WeChat` | the WeChat channel | none — plain HTTP |
| `Veriqa.Core.ChannelAdapter.Viber` | the Viber channel | none — plain HTTP |
| `Veriqa.Core.ChannelAdapter.Email` | the Email channel | `MailKit`, `QRCoder` |
| `Veriqa.Core.ChannelAdapter.Http` | HTTP channels: a channel whose adapter runs as a separate service, added by configuration | none — plain HTTP |
| `Veriqa.Core.ChannelAdapter.Redis` | Redis-backed channel stores (multiple replicas) | `StackExchange.Redis` |
| `Veriqa.Core.BaseChannels` | metapackage: the contour + Telegram, WhatsApp, Email | — |

`Veriqa.Core.AuthServer` depends on the contour only, so no channel library reaches a host through
it: a deployment that signs users in through Telegram alone never receives a mail library.
Installing both the metapackage and a separate channel is fine — NuGet resolves it to one copy.

## Registration

The registration call does not change with the package it arrives from: every `Add*` is an
extension method in this assembly's own namespace.

Register the adapters through the AuthServer facade:

```csharp
authServer.AddChannelAdapters(adapters =>
{
    adapters.AddTelegram();  // Telegram channel
    adapters.AddWhatsApp();  // WhatsApp channel
    adapters.AddMax();       // MAX channel
    adapters.AddEmail();     // Email channel (magic-link)
});
```

## Replacing a shipped implementation

The shipped implementations of the public channel ports — `IEmailActionTokenStore`,
`IEmailOutboundSender`, `IWhatsAppProvider` — are registered with `TryAddSingleton`, so **your
registration always wins over the built-in default and the result does not depend on call
order**:

```csharp
// 1. A plain registration made before AddVeriqaChannelAdapters (directly or through
//    AddVeriqaAuthServer, which calls it): yours is already in the collection, so the
//    shipped default is not added at all.
builder.Services.AddSingleton<IEmailActionTokenStore, MyActionTokenStore>();
builder.Services.AddVeriqaAuthServer(configuration, environment, authServer =>
    authServer.AddChannelAdapters(adapters => adapters.AddEmail()));

// 2. RemoveAll + Add after AddVeriqa* — still supported, unchanged.
builder.Services.RemoveAll<IEmailActionTokenStore>();
builder.Services.AddSingleton<IEmailActionTokenStore, MyActionTokenStore>();
```

This is the same discipline the AuthServer extension points follow — see
`Veriqa.Core.AuthServer/README.md`, "Substitution does not depend on call order".

> A port consumed as `IEnumerable<T>` is not covered by this: `IChannelAdapter` collects every
> registration rather than picks one, so a host adds to it with a plain registration and
> replaces nothing.

> Neither is an explicitly requested implementation: `UseRedisChannelStores` (see
> "Multiple replicas") registers its three stores unconditionally, precisely so that it beats the
> `TryAdd`-ed defaults in either call order. A host that wants its own store of one of those ports
> while using the satellite replaces it with `RemoveAll` + `Add` after the call.

## Multiple replicas

The contour keeps three stores of its own, and their shipped implementations are in-process:
`IChannelPromptMessageStore` (the chat a transaction was started from, with the references to the
in-channel prompt and to the phone number request sent into it),
`IEmailActionTokenStore` and `IEmailPushCorrelationStore` (the Email channel's Pull tokens, Push
correlations and inbound-mail deduplication). One replica is fine. Two are not: a magic link opened
on replica B finds no token minted on replica A, an inbound email is not matched to its
transaction, and an expiry that lands on the other instance is not reported to the chat — the
prompt keeps its buttons and a waiting phone number request stays.

Deploying more than one replica — install the Redis satellite and wire all three ports with one
call:

```bash
dotnet add package Veriqa.Core.ChannelAdapter.Redis
```

```csharp
authServer.AddChannelAdapters(adapters =>
{
    adapters.AddEmail();

    adapters.UseRedisChannelStores(redis =>
    {
        // Leave empty to reuse an IConnectionMultiplexer already registered in the container.
        redis.Configuration = "localhost:6379";
    });
});
```

Unlike the shipped defaults, these three registrations are unconditional, so the result does not
depend on whether the call comes before or after `AddEmail()`. Without the call nothing changes —
the in-process defaults stay. Setting `Configuration` while another component registers a non-keyed
`IConnectionMultiplexer` stops the host at start-up with an error naming both connections by their
endpoints, logical database, TLS settings and sentinel master name (credentials are never printed),
rather than quietly sending the channel stores to the other Redis. The check runs when the host
starts, so it holds whichever of the two registrations came first.

Key prefixes and the deduplication window are `RedisChannelStoreOptions`; record lifetimes are not,
because they belong to the stored entity — a token and a correlation live until their own
`ExpiresAt`, prompt coordinates until the longest transaction the engine can create has expired and
the expiry event that consumes them has had time to be published.

## Supported channels

| Channel | Delivery | Notes |
|---|---|---|
| Telegram | Bot API (webhook or long polling) | Full support. |
| MAX | Bot API (webhook or long polling) | Full support. |
| WhatsApp | Meta Cloud API or Twilio | Two shipped providers, picked by `Provider`; a custom one can be registered. |
| Instagram | Instagram API with Instagram Login (webhook) | Instagram Direct; Advanced Access for users without a role on the Meta app. |
| Messenger | Messenger Platform (webhook) | The chat with a Facebook Page; Advanced Access for users without a role on the Meta app. |
| WeChat | Official Account API (webhook) | A verified Service Account; plaintext message mode only. |
| Viber | Viber Bot API (webhook) | Verified against a mock of the Bot API only. |
| Email | SMTP or a custom sender (via DI) | A magic link or the one-tap email, not a pushed prompt. |

A third-party channel is registered exactly the same way — through `AddChannel` on the same builder;
see the [custom channel adapter guide](https://veriqa.app/docs/guides/custom-channel-adapter). Nothing in this package treats a shipped channel
differently from one you wrote.

## WhatsApp: the shipped providers and a custom one

WhatsApp delivery ships with two providers — the **Meta Cloud API** (`MetaCloudApi`, the default) and
**Twilio** (`Twilio`); `Veriqa:Channels:WhatsApp:Provider` picks the one that is registered.
A host plugs its own delivery provider with `UseWhatsAppProvider<TProvider>()` on the channel adapter
builder — before or after `AddWhatsApp()`, the last call wins. A provider names itself with a code
(`WhatsAppProviderNames` holds the shipped ones), and `Veriqa:Channels:WhatsApp:Provider` declares the
code the installation expects. The two are compared at startup: a mismatch stops the host with an
error naming both codes, so the declared and the actual delivery path never diverge silently.

Verifying an inbound webhook and parsing its format belong to the provider, not to the adapter: the
Meta Cloud API checks `X-Hub-Signature-256`, Twilio checks `X-Twilio-Signature`, and a custom provider
implements both members of `IWhatsAppProvider` for its own platform. The user is identified by the
business-scoped user ID (BSUID): a custom provider fills `WhatsAppInboundMessage.SenderUserId` — a message
without it is refused — and receives the BSUID as the recipient of every outbound message.

## Email

The email channel delivers a magic-link email or receives the one-tap email the user sends, rather
than pushing a confirmation prompt, so the generic push/webhook adapter methods do not apply to it.
Its inbound and outbound flows use the dedicated email endpoints and the SMTP or custom outbound
sender. Currently supported outbound providers are `Smtp` and `Custom` (registered via DI).

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
