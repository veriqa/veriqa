# Veriqa.Core.ChannelAdapter.WhatsApp

**WhatsApp login and authentication for your website or app.** Your users scan a QR code on a
computer, TV or kiosk, or tap a button on their phone, and confirm the sign-in in WhatsApp: no
password, no SMS code and no sign-up form. Your application receives standard OpenID Connect tokens
and, when WhatsApp sends it with the message, the user's phone number. Veriqa replies to the user's
own message without a template. WhatsApp charges beyond a free tier: 1,000 service messages a month
per business phone number are free, and since October 1, 2026 each one after that is charged at
[Meta's rates](https://developers.facebook.com/documentation/business-messaging/whatsapp/pricing).

This package is the WhatsApp channel of [Veriqa](https://veriqa.app), an open-source
project for passwordless authentication through popular messaging apps and email, over standard
OpenID Connect.
Setup guide: [WhatsApp channel](https://veriqa.app/docs/guides/channels#whatsapp).

## About this package

The adapter turns the user's confirmation in WhatsApp into the identity the core issues tokens
for. It is one channel out of several: a deployment installs the channels it actually uses. The
package carries no platform SDK — both shipped delivery providers, the Meta Cloud API and Twilio,
talk plain HTTP.

## Install

```bash
dotnet add package Veriqa.Core.ChannelAdapter.WhatsApp
```

The channel contour (`Veriqa.Core.ChannelAdapter`) arrives with this package — it is not installed
separately.

## Wiring

```csharp
authServer.AddChannelAdapters(adapters => adapters.AddWhatsApp());
```

The adapter activates only when its configuration section `Veriqa:Channels:WhatsApp` is enabled
(`"Enabled": true`); otherwise only its options and their validator are registered.

## Delivery providers

The channel logic is shared; a delivery provider (`IWhatsAppProvider`) sends the messages, reports
health, verifies the authenticity of an inbound webhook and parses its format. Two providers ship
with the package, and `Veriqa:Channels:WhatsApp:Provider` picks the one that is registered:

| `Provider` | Provider | Credentials section | Inbound check |
|---|---|---|---|
| `MetaCloudApi` (default) | Meta Cloud API (Graph API) | `MetaCloudApi:*` | `GET` handshake, `X-Hub-Signature-256` |
| `Twilio` | Twilio Messages API | `Twilio:*` — `AccountSid`, `AuthToken`, `QuickReplyContentSid`, `WebhookUrl`, `ApiBaseUrl` | `X-Twilio-Signature` |

With Twilio the confirmation prompt is a `twilio/quick-reply` content the operator creates with the
variables `1` (body), `2`/`3` (confirm title/id) and `4`/`5` (decline title/id), and `WebhookUrl` is
exactly the URL set in the Twilio console — the signature is computed over it.

The interactive messages after the confirmation — the link button to the claims completion form
(`cta_url`) and the buttons after a sign-in (`cta_url`, reply buttons, a list) — are sent by the Meta
Cloud API provider only. Through Twilio they are not delivered: the form falls back to the sign-in
page (with `CrossDevicePageOnly` a missing required claim fails the sign-in), and of the actions
after a sign-in only the welcome message is sent.

A host plugs its own provider with `UseWhatsAppProvider<TProvider>()`; it wins over the shipped one
whatever the call order. The configured `Provider` code is matched against the registered provider
at startup, and a mismatch stops the host.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
