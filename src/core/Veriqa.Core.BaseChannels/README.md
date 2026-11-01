# Veriqa.Core.BaseChannels

**Telegram, WhatsApp and Email login and authentication by QR code in one package.** One `dotnet add package`
instead of four. Your users scan a QR code on the sign-in page (on a phone, they tap a button) and
confirm the sign-in with no password and no SMS code. Your users can also sign in with a QR code or
a button through a Google or Apple account and 80+ other services, with the
[`Veriqa.Core.ChannelAdapter.WebProviders`](https://www.nuget.org/packages/Veriqa.Core.ChannelAdapter.WebProviders)
package. Your users can sign in this way on a computer, a TV, a kiosk or any other device that can
show a QR code.

![Signing in on a computer with Telegram: the sign-in window shows a QR code, the phone camera scans it, the user taps Confirm in the Telegram chat, and the chat replies "Sign-in confirmed".](https://gitlab.com/veriqa/veriqa/-/raw/main/gallery-and-docs/media/readme/en-telegram-cross-device.webp)

[Gallery](https://gitlab.com/veriqa/veriqa/-/blob/main/gallery-and-docs/README.md) · [Live demo](https://veriqa.app/demo?lang=en) · [Source code](https://gitlab.com/veriqa/veriqa) · Open source under [MPL-2.0](https://gitlab.com/veriqa/veriqa/-/blob/main/LICENSE)

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Supported messaging apps](https://veriqa.app/docs/guides/supported-channels).

## Use cases

- **Passwordless sign-in and sign-up.** Your users scan a QR code or tap a button instead of typing a
  password, and a new user signs up with the same action.
  [Passwordless sign-in](https://veriqa.app/docs/scenarios/passwordless-login)
- **Second factor and action confirmation.** Your product asks the user to confirm a sensitive action
  with one tap in their messaging app, with no code to type.
  [Second factor and step-up](https://veriqa.app/docs/scenarios/step-up)
- **AI agents.** A risky agent step runs only after a person approves it in their own messaging app.
  [Approving AI agent actions](https://veriqa.app/docs/scenarios/agent-approvals)
- **Replacing SMS codes.** Your users confirm the sign-in with a button in their messaging app, and
  you pay no SMS charges. [Replacing SMS codes](https://veriqa.app/docs/scenarios/sms-replacement)
- **A direct channel to the user.** The same sign-in gives your product a channel for notifications.
  [Sign-in and channel linking](https://veriqa.app/docs/scenarios/channel-linking)
- **Leads and feedback without CAPTCHA.** Your site gets a confirmed contact instead of an address
  typed into a field. This use case has been tested with Telegram.
  [Leads and feedback without CAPTCHA](https://veriqa.app/docs/scenarios/lead-capture)
- **Surfaces with no keyboard.** A smart TV, a set-top box or a kiosk shows a QR code, and the user
  types nothing. [Sign-in surfaces](https://veriqa.app/docs/scenarios/surfaces)

[All use cases](https://veriqa.app/docs/scenarios/overview)

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
