# Veriqa.Core.ChannelAdapter.Email

Email channel adapter for Veriqa — a satellite package. It signs a user in over mail in two shapes:
a magic link the user opens (Pull) and the one-tap email, a pre-filled letter the user sends (Push). Outbound mail
goes over SMTP; inbound mail arrives from a provider webhook.

It is the channel for users who have none of the messengers installed, and it is one channel out of
several: only the mail libraries travel with this package.

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
