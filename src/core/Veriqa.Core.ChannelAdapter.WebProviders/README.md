# Veriqa.Core.ChannelAdapter.WebProviders

**Let your users sign in on their own phone with a Google, Apple, Huawei, Yandex or VK ID account, or
with another provider: [80+ in all](https://documentation.openiddict.com/integrations/web-providers).**
The sign-in device shows a QR code, the user signs in to the provider on the phone and
confirms there, and the sign-in completes on the device. No password is typed on the sign-in device,
and your application receives the same standard OpenID Connect tokens as from any other channel.

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Sign in with a provider account](https://veriqa.app/docs/scenarios/provider-account-sign-in),
setup: [Channel setup](https://veriqa.app/docs/guides/channels#sign-in-with-a-provider-account).

## About this package

The package turns a client you register with an identity provider into a Veriqa channel. Any provider
of `OpenIddict.Client.WebIntegration` works: the protocol of each one — the redirect, the code
exchange, the validation of the ID token, the userinfo call — is the one OpenIddict describes, and the
package has no protocol code of its own. One client registration is one channel with a name you
choose, so two clients of Google are two channels.

A provider has no chat, so the package draws a page on the phone that plays the part of one: it
shows the question Veriqa asks with **Yes** and **No**, the messages Veriqa sends after the sign-in
(a welcome message, a menu, a redirect button, a link to the claims completion form) and the outcome.
The page is the same for a sign-in and for a confirmation.

The package is not part of the `Veriqa.Core.BaseChannels` metapackage. The standard Veriqa host
references it and registers the channels from its configuration; an embedded host adds the package
explicitly.

## Install

```bash
dotnet add package Veriqa.Core.ChannelAdapter.WebProviders
```

## Wiring

```csharp
authServer.AddChannelAdapters(adapters => adapters.AddWebProviders());
```

The routes of the channels are mapped together with the other channel endpoints of Veriqa, so there
is nothing else to map. A configuration with no enabled provider channel registers no OpenIddict
client at all.

## Configuration

A channel is a section `Veriqa:Channels:{channel}` with a `WebProvider` subsection:

```json
{
  "Veriqa": {
    "Channels": {
      "google": {
        "Enabled": true,
        "InboundVerification": "outbound_fetch",
        "WebProvider": {
          "Provider": "Google",
          "ClientId": "1234567890-abc.apps.googleusercontent.com",
          "Scopes": [ "email", "profile" ],
          "Settings": { "Prompt": "select_account" },
          "PublicBaseUrl": "https://id.example.com"
        }
      }
    }
  }
}
```

The client identifier in the example is synthetic. Keep `ClientSecret` and `SigningKey` in a secret
store or in environment variables (`Veriqa__Channels__google__WebProvider__ClientSecret`), never in a
committed `appsettings.json`.

The channel name is the name of the section: lowercase Latin letters, digits and `-`, starting with a
letter, up to 64 characters. It cannot be the name of a channel shipped with Veriqa (`telegram`,
`email` and so on).

| Key | Required | Meaning |
| --- | --- | --- |
| `Enabled` | no, `false` by default | Turns the channel on |
| `InboundVerification` | yes, when enabled | `outbound_fetch`: the adapter gets the identity of the user from the provider with requests of its own |
| `WebProvider:Provider` | yes | The provider name in `OpenIddict.Client.WebIntegration` (`Google`, `Apple`, `Huawei`, `Yandex`, `VkId`…), compared case-insensitively |
| `WebProvider:ClientId` | yes | The client registered with the provider |
| `WebProvider:ClientSecret` | no | The secret of that client; Apple takes a key instead |
| `WebProvider:Scopes` | no | Scopes requested on top of the ones OpenIddict requests for the provider by default |
| `WebProvider:Settings:{Name}` | no | A setting of this provider's own, applied as `Set{Name}(string)` of its OpenIddict builder (`TeamId`, `Prompt`, `AccessType`…). The setters every registration has (`ClientId`, `ClientSecret`, `RedirectUri` and the like) and `SigningKey` are refused here: they have keys of their own |
| `WebProvider:SigningKey` | Apple only | The PEM-encoded EC private key Apple authenticates the client with |
| `WebProvider:SigningKeyId` | Apple only | The Key ID of that key |
| `WebProvider:PublicBaseUrl` | yes | The absolute `https` address of the host, path base included: the base of the channel link and of the return address |
| `WebProvider:DisplayName` | no | The name of the channel on the sign-in page and in the header of the phone page; by default the provider's display name in OpenIddict |
| `WebProvider:IconSvgPath` | no | The channel glyph, by the rules of `IconSvgPath` of the HTTP channel |

The host does not start, and the message names the key, when an enabled channel has no `Provider`,
`ClientId` or `PublicBaseUrl`; when the provider name is unknown; when `PublicBaseUrl` is not an
absolute `https` address or carries a query; when a `Settings` name is unknown to the provider or is
refused; when OpenIddict refuses the registration (Apple without `TeamId`); when Apple has no
`SigningKey` or `SigningKeyId`, or another provider has either; when the channel name breaks the rule
above; and when one section carries both `Http` and `WebProvider`.

A disabled channel has no OpenIddict registration, no routes and no adapter.

## The return address

Register this address with the provider:

```text
{PublicBaseUrl}/auth/web-provider/{channel}/callback
```

It takes both `GET` and `POST`: Apple returns the user with `form_post`. The channel link, which
starts the sign-in at the provider, is `{PublicBaseUrl}/auth/web-provider/{channel}/start?tx={transaction}`.

## The five starting providers

| `Provider` | Scopes to add | `Settings` | What the user identity gets | `email_verified` | Documentation |
| --- | --- | --- | --- | --- | --- |
| `Google` | `email`, `profile` | `Prompt`: `select_account` shows the account chooser on every sign-in | identifier, name, first and last name, email, avatar, locale | the `email_verified` claim of the ID token | [Google: OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect) |
| `Apple` | `email` | `TeamId` (required); the key goes into `SigningKey` and `SigningKeyId` | identifier, email | the `email_verified` claim of the ID token | [Apple: request an authorization](https://developer.apple.com/documentation/signinwithapple/incorporating-sign-in-with-apple-into-other-platforms) |
| `Huawei` | `email` | `Display`: `touch` adjusts the provider page to a phone | identifier, name (`display_name` of the ID token), email | the `email_verified` claim of the ID token, when it carries one | [Huawei: obtaining an access token](https://developer.huawei.com/consumer/en/doc/HMSCore-Guides/web-get-access-token-0000001050048946) |
| `Yandex` | none: the access rights (email address; name; profile picture) are chosen when you register the app in Yandex ID | `DeviceId`, `DeviceName`, optional | identifier (`id`), name (`display_name`), first and last name, email (`default_email`) | not stated by Yandex: unknown | [Yandex ID: user information](https://yandex.ru/dev/id/doc/en/user-information) |
| `VkId` | `email` | none | identifier (`user_id`), first and last name, avatar, email | not stated by VK ID: unknown | [VK ID: API description](https://id.vk.ru/about/business/go/docs/en/vkid/latest/vk-id/connection/api-integration/api-description) |

Without added scopes OpenIddict requests only `openid` from an OpenID Connect provider, so Google
returns no email and no name. `email_verified` is `true` or `false` only when the provider says so
next to the email; otherwise the claim stays unknown and is never assumed `true`. The phone number is
never taken from the provider, and the claims completion form does not ask for it either. This
channel is not offered to a sign-in that requires a phone number; a sign-in where the number is
optional completes without it.

The adapter keeps no provider token. `AccessType` set to `offline` (Google, Huawei) gets a refresh
token that nothing uses.

## What the adapter adds to the OpenIddict client

The adapter adds its client to the same `AddOpenIddict()` the Veriqa server is registered on. If your
host calls `AddClient()` itself, these are the settings the adapter adds to it; every other setting of
your client stays as you set it, the state token lifetime included:

- `AllowAuthorizationCodeFlow()`;
- the return address of every channel, appended to `RedirectionEndpointUris` (your own addresses are
  kept), and `UseAspNetCore().EnableRedirectionEndpointPassthrough()`;
- an event handler that passes an error at the return addresses of its channels (a provider error, a
  sign-in the user canceled, a state that did not pass) to the channel route, which answers with its
  page. Errors at your other client endpoints keep the answer you configured: the error pass-through
  mode of the client is not turned on;
- `UseSystemNetHttp()` and `UseWebProviders()` with one registration per channel, where
  `RegistrationId` and `ProviderName` are the channel name;
- the signing and encryption credentials of the Veriqa server of the same host, added to those of the
  client: no new key material is introduced;
- `AddAntiforgery()` for the forms of the phone page.

The state of a sign-in lands in the token store the Veriqa server already uses, so the database
schema does not change and the existing token pruning clears it. A sign-in on the phone does not
create a session in your host.

The OpenIddict version is 7.4.0, the same as the rest of the Veriqa core.

## Several replicas

The state of the phone pages — the sessions, the questions, the messages and the outcomes — lives in
the process by default. With `UseRedisChannelStores` from `Veriqa.Core.ChannelAdapter.Redis` it lives
in Redis under the `WebProviderStateKeyPrefix` prefix (`veriqa:ch:webprovider:` by default). The
cookie that binds the browser to its page is protected by ASP.NET Core Data Protection, so the
replicas need one shared key ring. Records live until the transaction expires, plus the time a
completed transaction is kept.

## Limitations

- **Registrations are read at start.** Adding a channel or changing one takes a restart of the host.
- **The channel name is part of `sub`.** `sub` has the form `{channel}:{the user's identifier at the
  provider}`, so renaming a channel changes the `sub` of every user who signed in through it.
- **Scan the QR code with the system camera, not inside a messaging app.** A link opened in an app's
  built-in browser reaches Google in an embedded user agent, and Google refuses the sign-in with
  `disallowed_useragent`
  ([Google: OAuth 2.0 policies](https://developers.google.com/identity/protocols/oauth2/policies)).
- **Apple: the email may be a relay address** that forwards to the real one
  ([Apple: private email relay service](https://developer.apple.com/documentation/signinwithapple/communicating-using-the-private-email-relay-service)).
  Treat it as a contact address, not as an identifier.
- **Apple: no name.** Apple sends the name in a separate `user` field of the first authorization
  response rather than in the ID token, and the user identity does not take it. The display name of an
  Apple user is their email address.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
