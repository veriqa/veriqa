# Veriqa.Core.BaseChannels

**Telegram, WhatsApp and Email login and authentication in one package.** One `dotnet add package`
instead of four.

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Supported messaging apps](https://veriqa.app/docs/guides/supported-channels).

## About this package

A metapackage of the base Veriqa channel set: it carries no library of its own, only the
dependencies that make up the usual starting set — the channel contour plus the Telegram, WhatsApp
and Email channels. No API of its own appears at the consumer.

MAX, Instagram, Messenger, WeChat and Viber are not part of it: each is installed separately with
its own `dotnet add package`. A deployment that wants a single channel takes that channel's package
instead of this one; installing both the metapackage and a separate channel is fine — NuGet resolves
it to one copy.

## Install

```bash
dotnet add package Veriqa.Core.BaseChannels
```

## Wiring

The channels arrive as their own packages, so the registration is theirs:

```csharp
authServer.AddChannelAdapters(adapters =>
{
    adapters.AddTelegram();
    adapters.AddWhatsApp();
    adapters.AddEmail();
});
```

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
