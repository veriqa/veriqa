# Veriqa.Core.ChannelAdapter.Max

**MAX login and authentication by QR code for your website or app.** Your users scan a QR code on the sign-in
page (on a phone, they tap a button) and confirm the sign-in with one tap in the MAX messaging app:
no password, no SMS code and no sign-up form. Your application receives standard OpenID Connect
tokens. Your users can sign in this way on a computer, a TV, a kiosk or any other device that can
show a QR code.

This package is the MAX channel of [Veriqa](https://veriqa.app), an open-source
project for passwordless authentication through popular messaging apps and email, over standard
OpenID Connect.
Setup guide: [MAX channel](https://veriqa.app/docs/guides/channels#max).

Use cases: [passwordless sign-in](https://veriqa.app/docs/scenarios/passwordless-login), [second factor and step-up](https://veriqa.app/docs/scenarios/step-up), [all use cases](https://veriqa.app/docs/scenarios/overview).

## About this package

The adapter turns the user's confirmation in your MAX bot into the identity the core issues
tokens for. MAX is not part of the `Veriqa.Core.BaseChannels` metapackage: it is installed with an
explicit `dotnet add package` by the deployments that need it.

## Install

```bash
dotnet add package Veriqa.Core.ChannelAdapter.Max
```

The channel contour (`Veriqa.Core.ChannelAdapter`) arrives with this package — it is not installed
separately.

## Wiring

```csharp
authServer.AddChannelAdapters(adapters => adapters.AddMax());
```

The adapter activates only when its configuration section `Veriqa:Channels:Max` is enabled
(`"Enabled": true`); otherwise only its options and their validator are registered.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
