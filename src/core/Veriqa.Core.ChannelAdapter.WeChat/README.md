# Veriqa.Core.ChannelAdapter.WeChat

**WeChat login and authentication by QR code for your website or app.** Your users scan a QR code with WeChat
and confirm the sign-in in the chat with your Official Account: no password, no SMS code and no
sign-up form. Your application receives standard OpenID Connect tokens.

This package is the WeChat channel of [Veriqa](https://veriqa.app), an open-source
project for passwordless authentication through popular messaging apps and email, over standard
OpenID Connect.
Setup guide: [WeChat channel](https://veriqa.app/docs/guides/channels#wechat).

Use cases: [passwordless sign-in](https://veriqa.app/docs/scenarios/passwordless-login), [second factor and step-up](https://veriqa.app/docs/scenarios/step-up), [all use cases](https://veriqa.app/docs/scenarios/overview).

## About this package

The adapter turns the user's confirmation in the chat with a WeChat Official Account into the
identity the core issues tokens for. WeChat is not part of the `Veriqa.Core.BaseChannels`
metapackage: it is installed with an explicit `dotnet add package` by the deployments that need it.
The package carries no platform SDK — the channel talks plain HTTP.

## Install

```bash
dotnet add package Veriqa.Core.ChannelAdapter.WeChat
```

The channel contour (`Veriqa.Core.ChannelAdapter`) arrives with this package — it is not installed
separately.

## Wiring

```csharp
authServer.AddChannelAdapters(adapters => adapters.AddWeChat());
```

The adapter activates only when its configuration section `Veriqa:Channels:WeChat` is enabled
(`"Enabled": true`); otherwise only its options and their validator are registered.

## Configuration

```json
{
  "Veriqa": {
    "Channels": {
      "WeChat": {
        "Enabled": true,
        "InboundVerification": "shared_secret",
        "AppId": "YOUR_APP_ID",
        "AppSecret": "YOUR_APP_SECRET",
        "Token": "YOUR_SERVER_TOKEN"
      }
    }
  }
}
```

`AppId`, `AppSecret` and `Token` are the credentials of a tenant. `ApiBaseUrl` is a setting of the
installation and defaults to `https://api.weixin.qq.com`.

The server address is entered in the settings of the Official Account:
`/api/channels/wechat/webhook` (or `/api/channels/wechat/webhook/{tenant}` for a tenant with an
account of its own), with the same `Token` and the plaintext message mode. Every request — the
address check and a delivery alike — is checked against the SHA-1 signature made with `Token`, and
a request signed more than five minutes away from the clock of the host is refused.

## Sign-in start and identity

The sign-in starts by a temporary QR code the platform creates for the transaction; the link the
adapter returns is the content of that code. Scanning it delivers a `subscribe` or a `SCAN` event,
the user gets a menu message with two options, and the outcome arrives as a message of its own.

The identity is the OpenID of the user, scoped to the account. The platform hands an Official
Account neither a name, nor a picture, nor a language of the user, and no way to ask for a phone
number in the chat.

The link to the claims completion form and the link buttons after a sign-in come as links in the
text of a message, one per line; the callback items of a menu come as the options of a menu
message. A menu with both is two messages: the options with the text first, then the links.

## Known limitations

- Creating a QR code with a scene and sending customer service messages need a verified Service
  Account.
- Outbound calls are accepted only from the addresses on the IP allowlist of the account.
- The platform lets the account send three messages within a minute after a scan and five within
  48 hours after a message of the user; a send outside that window fails and is not retried. The
  confirmation question, the outcome receipt, the form link and the messages after a sign-in all
  count toward these limits.
- Plaintext message mode only: a delivery in the encrypted mode is not read.
- The messages are in the language of the sign-in: an inbound message names no language of the user.
- The created QR code is remembered per process, so two hosts serving one account return different
  links to the same transaction; both start the same sign-in.
- `AppId` and `AppSecret` of a tenant are read when its client is built: a change takes a restart.
- One account per webhook address: the tenant is the `{tenant}` segment of the route.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
