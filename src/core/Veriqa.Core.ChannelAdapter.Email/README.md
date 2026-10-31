# Veriqa.Core.ChannelAdapter.Email

**Passwordless email login and authentication for your website or app.** Your users confirm the
sign-in with a magic link, or with the one-tap email — a pre-filled email they send with one tap: no
password and no code to type. It is the channel for users who have none of the supported messaging
apps installed, and your application receives the same standard OpenID Connect tokens.

This package is the Email channel of [Veriqa](https://veriqa.app), an open-source
project for passwordless authentication through popular messaging apps and email, over standard
OpenID Connect.
Setup guide: [Email channel](https://veriqa.app/docs/guides/channels#email).

## About this package

The package signs a user in over email in two modes: a magic link the user opens (Pull) and the
one-tap email, a pre-filled email the user sends (Push). Outbound email goes over SMTP; inbound email
arrives from a provider webhook or is read by Veriqa itself from a mailbox over IMAP
(`Inbound:Provider = ImapPolling`) — then no inbound email provider and no public address are
needed. It is one channel out of several: only the email libraries travel with this package.

## Install

```bash
dotnet add package Veriqa.Core.ChannelAdapter.Email
```

The channel contour (`Veriqa.Core.ChannelAdapter`) arrives with this package — it is not installed
separately.

## Wiring

```csharp
authServer.AddChannelAdapters(adapters => adapters.AddEmail());
```

The adapter activates only when its configuration section `Veriqa:Channels:Email` is enabled
(`"Enabled": true`); otherwise only its options and their validator are registered.

## Shipped dependencies worth knowing about

The Pull-mode letter embeds the sign-in QR as an inline image, so this package encodes one itself
and references `QRCoder 1.8.0`. That reference brings two transitive packages of the .NET 6 era into
the graph: the newest dependency group QRCoder declares is `net6.0`, that group names
`System.Drawing.Common 6.0.0`, and it in turn pulls `Microsoft.Win32.SystemEvents 6.0.0`. Worth
knowing for an on-premise Linux installation — `System.Drawing.Common` is unsupported outside
Windows from .NET 7 on.

**The GDI+ path is never executed.** The only QRCoder renderer this package uses is `PngByteQRCode`
(in `SmtpEmailOutboundSender`), which writes PNG bytes directly and touches no drawing type. The
packages are present, not called.

They are not dropped because there is nowhere to drop them to: QRCoder 1.8.0 is the latest release
(checked on 2026-08-28) and ships no `net8.0`/`net10.0` group, and moving the encoder into a
satellite would help no one — the shipped Pull template embeds the QR, so an ordinary installation
encodes one on every magic link it sends.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
