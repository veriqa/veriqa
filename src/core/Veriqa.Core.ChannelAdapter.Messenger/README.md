# Veriqa.Core.ChannelAdapter.Messenger

**Facebook Messenger login and authentication by QR code for your website or app.** Your users scan a QR code
on the sign-in page (on a phone, they tap a button) and confirm the sign-in in the Messenger chat
with your Facebook Page: no password, no SMS code and no sign-up form. Your application receives
standard OpenID Connect tokens. Your users can sign in this way on a computer, a TV, a kiosk or any
other device that can show a QR code.

![Signing in on a computer with Messenger: the sign-in window shows a QR code, the phone camera scans it, the user taps Confirm in the Messenger chat, and the chat replies "Sign-in confirmed".](https://gitlab.com/veriqa/veriqa/-/raw/main/gallery-and-docs/media/readme/en-messenger-cross-device.webp)

This package is the Messenger channel of [Veriqa](https://veriqa.app), an open-source
project for passwordless authentication through popular messaging apps and email, over standard
OpenID Connect.
Setup guide: [Messenger channel](https://veriqa.app/docs/guides/channels#messenger).

Gallery: [sign-in and confirmation in Messenger](https://gitlab.com/veriqa/veriqa/-/blob/main/gallery-and-docs/README.md#messenger). Use cases: [passwordless sign-in](https://veriqa.app/docs/scenarios/passwordless-login), [second factor and step-up](https://veriqa.app/docs/scenarios/step-up), [all use cases](https://veriqa.app/docs/scenarios/overview).

## About this package

The adapter turns the user's confirmation in the Messenger chat with a Facebook Page into the
identity the core issues tokens for. Messenger is not part of the `Veriqa.Core.BaseChannels`
metapackage: it is installed with an explicit `dotnet add package` by the deployments that need it.
The package carries no platform SDK — the channel talks plain HTTP.

## Install

```bash
dotnet add package Veriqa.Core.ChannelAdapter.Messenger
```

The channel contour (`Veriqa.Core.ChannelAdapter`) arrives with this package — it is not installed
separately.

## Wiring

```csharp
authServer.AddChannelAdapters(adapters => adapters.AddMessenger());
```

The adapter activates only when its configuration section `Veriqa:Channels:Messenger` is enabled
(`"Enabled": true`); otherwise only its options and their validator are registered.

## Configuration

```json
{
  "Veriqa": {
    "Channels": {
      "Messenger": {
        "Enabled": true,
        "InboundVerification": "signature",
        "PageId": "YOUR_PAGE_ID",
        "PageName": "your.page.username",
        "PageAccessToken": "YOUR_PAGE_ACCESS_TOKEN",
        "AppSecret": "YOUR_APP_SECRET",
        "WebhookVerifyToken": "YOUR_VERIFY_TOKEN"
      }
    }
  }
}
```

`GraphApiVersion` defaults to `v26.0`; set it to run against another Graph API version.

`DeepLinkByPageId` is optional and defaults to `false`. With `true` the sign-in link is built from
`PageId` instead of `PageName`, and `PageName` is not required — for a Page whose link by the
username does not open the thread in the Messenger app.

The webhook is registered on Meta's side: `/api/channels/messenger/webhook` (or
`/api/channels/messenger/webhook/{tenant}` for a tenant with a Meta app of its own). Every delivery
is checked against the `X-Hub-Signature-256` signature keyed with `AppSecret`.

## Sign-in start, profile and phone number

The sign-in starts by the link `https://m.me/<PageName>?ref=auth_<transaction>` — or
`https://m.me/<PageId>?ref=auth_<transaction>` with `DeepLinkByPageId` switched on; the same marker
sent as a message starts it too. Subscribe the Page to the `messages`, `messaging_postbacks` and
`messaging_referrals` webhook fields: a link opened into an existing thread arrives as a referral
event, a link that opens a new thread arrives inside the postback of the Get Started button.

That first tap may arrive without the `ref` value. The channel then answers with a hint, and the
user goes on by sending the code shown on the sign-in page as a message — the digits alone — or by
scanning the QR code again. The sign-in page shows the code after a short delay and hides it once
the Page has answered; `SpareStartCodeEnabled: false` in the channel section switches the code off.
Only a tapped button gets the hint: a typed message that is
neither a code nor a sign-in marker gets no answer. `UnaddressedReplyEnabled: false` in the channel
section switches the hint off, and the code keeps working. A code that matches no sign-in is
answered with one text for every reason: check the code and send it again, or open the sign-in page
again.

The phone number is asked for with the quick reply of the platform. Messenger shows that button only
to a user whose profile has a phone number, and does not state that the number was verified, so the
`phone_number_verified` claim is `false`. A number typed as a message is not accepted.

## Known limitations

- One delivery yields one result: when Meta batches several events into a request, one is
  processed — an event that names a sign-in before a shared phone number, and that before any
  other message.
- A Meta app without Advanced Access delivers events — the referral of an `m.me` link included —
  only for people who have a role in the app.
- The profile (name, picture) needs the Business Asset User Profile Access feature, the locale the
  `pages_user_locale` permission. A profile the app cannot read leaves the identity as the
  page-scoped ID alone; the sign-in does not depend on it.
- The messages are in the language of the user's profile when the app may read the locale, and in
  the language of the sign-in otherwise. An app refused the locale is asked for it again once an
  hour, so a permission granted later takes up to an hour to show.
- Messages are sent as a response within the standard messaging window of 24 hours.
- The link button to the claims completion form and the buttons after a sign-in are sent as a
  button template: at most three buttons, a label of up to 20 characters and a text of up to 640
  characters (longer ones are cut). The buttons beyond the third are left out.
- One Page per webhook address: the tenant is the `{tenant}` segment of the route.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
