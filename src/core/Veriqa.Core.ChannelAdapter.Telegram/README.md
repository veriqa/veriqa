# Veriqa.Core.ChannelAdapter.Telegram

**Telegram login and authentication by QR code for your website or app.** Your users scan a QR code on the
sign-in page (on a phone, they tap a button) and confirm the sign-in with one tap in Telegram: no
password, no SMS code and no sign-up form. Your application receives standard OpenID Connect tokens
with the user's name, language and photo and, with their consent, their phone number. Your users can
sign in this way on a computer, a TV, a kiosk or any other device that can show a QR code.

![Signing in on a computer with Telegram: the sign-in window shows a QR code, the phone camera scans it, the user taps Confirm in the Telegram chat, and the chat replies "Sign-in confirmed".](https://gitlab.com/veriqa/veriqa/-/raw/main/gallery-and-docs/media/readme/en-telegram-cross-device.webp)

This package is the Telegram channel of [Veriqa](https://veriqa.app), an open-source
project for passwordless authentication through popular messaging apps and email, over standard
OpenID Connect.
Setup guide: [Telegram channel](https://veriqa.app/docs/guides/channels#telegram).

Gallery: [sign-in and confirmation in Telegram](https://gitlab.com/veriqa/veriqa/-/blob/main/gallery-and-docs/README.md#telegram). Use cases: [passwordless sign-in](https://veriqa.app/docs/scenarios/passwordless-login), [second factor and step-up](https://veriqa.app/docs/scenarios/step-up), [all use cases](https://veriqa.app/docs/scenarios/overview).

## About this package

The adapter turns the user's confirmation in your Telegram bot into the identity the core issues
tokens for. It is one channel out of several: a deployment installs the channels it actually uses,
and only the Telegram SDK travels with this one.

## Install

```bash
dotnet add package Veriqa.Core.ChannelAdapter.Telegram
```

The channel contour (`Veriqa.Core.ChannelAdapter`) arrives with this package — it is not installed
separately.

## Wiring

```csharp
authServer.AddChannelAdapters(adapters => adapters.AddTelegram());
```

The adapter activates only when its configuration section `Veriqa:Channels:Telegram` is enabled
(`"Enabled": true`); otherwise only its options and their validator are registered.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
