# Veriqa.Core.ChannelAdapter.Instagram

**Instagram login and authentication by QR code for your website or app.**

- Your users scan a QR code on the sign-in page (on a phone, they tap a button) and confirm the
  sign-in in Instagram Direct: no password, no SMS code and no sign-up form.
- Your application receives standard OpenID Connect tokens.
- Your users can sign in this way on a computer, a TV, a kiosk or any other device that can show a
  QR code.

![Signing in on a computer with Instagram: the sign-in window shows a QR code, the phone camera scans it, the user taps Confirm in the Instagram chat, and the chat replies "Sign-in confirmed".](https://gitlab.com/veriqa/veriqa/-/raw/main/gallery-and-docs/media/readme/en-instagram-cross-device.webp)

This package is the Instagram channel of [Veriqa](https://veriqa.app), an open-source
project for passwordless authentication through popular messaging apps and email, over standard
OpenID Connect.
Setup guide: [Instagram channel](https://veriqa.app/docs/guides/channels#instagram).

Gallery: [sign-in and confirmation in Instagram](https://gitlab.com/veriqa/veriqa/-/blob/main/gallery-and-docs/README.md#instagram). Use cases: [passwordless sign-in](https://veriqa.app/docs/scenarios/passwordless-login), [second factor and step-up](https://veriqa.app/docs/scenarios/step-up), [all use cases](https://veriqa.app/docs/scenarios/overview).

## About this package

The adapter turns the user's confirmation in Instagram Direct into the identity the core issues
tokens for. Instagram is not part of the `Veriqa.Core.BaseChannels` metapackage: it is installed
with an explicit `dotnet add package` by the deployments that need it. The package carries no
platform SDK — the channel talks plain HTTP.

## Install

```bash
dotnet add package Veriqa.Core.ChannelAdapter.Instagram
```

The channel contour (`Veriqa.Core.ChannelAdapter`) arrives with this package — it is not installed
separately.

## Wiring

```csharp
authServer.AddChannelAdapters(adapters => adapters.AddInstagram());
```

The adapter activates only when its configuration section `Veriqa:Channels:Instagram` is enabled
(`"Enabled": true`); otherwise only its options and their validator are registered.

## Configuration

```json
{
  "Veriqa": {
    "Channels": {
      "Instagram": {
        "Enabled": true,
        "InboundVerification": "signature",
        "AppSecret": "YOUR_APP_SECRET",
        "WebhookVerifyToken": "YOUR_VERIFY_TOKEN",
        "AccessToken": "YOUR_ACCESS_TOKEN",
        "AccountId": "YOUR_INSTAGRAM_ACCOUNT_ID",
        "Username": "your_login_account"
      }
    }
  }
}
```

The webhook is registered on Meta's side: `/api/channels/instagram/webhook` (or
`/api/channels/instagram/webhook/{tenant}` for a tenant with a Meta app of its own), subscribed to
`messages`, `messaging_postbacks` and `messaging_referral`. Every delivery is checked against the
`X-Hub-Signature-256` signature keyed with `AppSecret`.

## Platform access

The channel needs an Instagram professional account and a Meta app with the permissions
`instagram_business_basic` and `instagram_business_manage_messages`. Signing in users who have no
role on the app takes Advanced Access — Meta App Review and Business Verification.

The sign-in starts by the link `https://ig.me/m/<Username>?ref=auth_<transaction>`. Instagram does
not always deliver the `ref` value: the first tap of an Icebreaker by a new user may arrive without
it. The channel then answers with a hint, and the user goes on by sending the code shown on the
sign-in page as a message — the digits alone — or by scanning the QR code again. Only a tapped button gets the hint: a typed message that is
neither a code nor a sign-in marker gets no answer. `UnaddressedReplyEnabled: false` in the channel
section switches the hint off, and the code keeps working. A code that matches no sign-in is
answered with one text for every reason: check the code and send it again, or open the sign-in page
again.

The limits of the platform — the 24-hour messaging window, the referral that may not arrive while
the app is in development mode, the phone number button shown only in the mobile app — are described
in the channel setup guide: https://veriqa.app/docs/guides/channels#instagram

The link button to the claims completion form and the buttons after a sign-in are sent as a button
template with up to three buttons; a text longer than 640 characters is sent as a separate message
before it.
The Instagram web version does not show the template, so a user in the browser sees the text
without the buttons — what this means for the form:
https://veriqa.app/docs/guides/supported-channels#instagram-on-the-web

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
