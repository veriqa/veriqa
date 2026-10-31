# Veriqa.Core.ChannelAdapter.Viber

**Viber login and authentication for your website or app.** Your users scan a QR code on a computer,
TV or kiosk, or tap a button on their phone, and confirm the sign-in in your Viber bot: no password,
no SMS code and no sign-up form. Your application receives standard OpenID Connect tokens.

This package is the Viber channel of [Veriqa](https://veriqa.app), an open-source
project for passwordless authentication through popular messaging apps and email, over standard
OpenID Connect.
Setup guide: [Viber channel](https://veriqa.app/docs/guides/channels#viber).

## About this package

The adapter turns the user's confirmation in a Viber bot into the identity the core issues
tokens for. Viber is not part of the `Veriqa.Core.BaseChannels` metapackage: it is installed with an
explicit `dotnet add package` by the deployments that need it. The package carries no platform
SDK — the channel talks plain HTTP.

## Verification status

The adapter has been verified against a mock of the Viber Bot API only; it has not been run
against the live Viber platform. Since 5 February 2024 Viber issues new bots on commercial terms
only, so the bot and its token are obtained from Viber before the channel is deployed.

## Install

```bash
dotnet add package Veriqa.Core.ChannelAdapter.Viber
```

The channel contour (`Veriqa.Core.ChannelAdapter`) arrives with this package — it is not installed
separately.

## Wiring

```csharp
authServer.AddChannelAdapters(adapters => adapters.AddViber());
```

The adapter activates only when its configuration section `Veriqa:Channels:Viber` is enabled
(`"Enabled": true`); otherwise only its options and their validator are registered.

## Configuration

```json
{
  "Veriqa": {
    "Channels": {
      "Viber": {
        "Enabled": true,
        "InboundVerification": "signature",
        "AuthToken": "YOUR_BOT_TOKEN",
        "BotUri": "YOUR_BOT_URI",
        "SenderName": "Veriqa",
        "WebhookBaseUrl": "https://auth.example.com"
      }
    }
  }
}
```

`AuthToken`, `BotUri` and `SenderName` are the credentials of a tenant. `WebhookBaseUrl` and
`ApiBaseUrl` are settings of the installation; `ApiBaseUrl` defaults to `https://chatapi.viber.com`.

The host registers the webhook of the default tenant's bot itself, at
`{WebhookBaseUrl}/api/channels/viber/webhook`. The webhook of a tenant with a bot of its own —
`/api/channels/viber/webhook/{tenant}` — is set by whoever provisions that tenant. Every inbound
request is checked against the `X-Viber-Content-Signature` header: an HMAC-SHA256 of the request
body made with the `AuthToken` of the tenant.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
