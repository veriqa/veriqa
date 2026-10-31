# Pseudo-channels sample (.NET): Google and Apple behind a cross-device sign-in

The desktop shows the QR code of the Veriqa sign-in page; a phone scans it, passes the hop page of
the node and signs in at Google or with Sign in with Apple. The sign-in happens **on the phone**,
and its result completes the sign-in **on the desktop**.

**This is not a delivery channel.** A pseudo-channel is a sign-in at an identity provider wrapped
into the shape of a channel by hop mode. There is no chat and no inbox behind it, so **no message
can be sent to the user "in the channel"**: the adapter's `SendMessageAsync` returns a failure, and
the question "is it you signing in over there?" is asked by a page of this sample on the phone.

| Project | What it is | Veriqa dependencies |
|---|---|---|
| `adapter/` | One `IChannelAdapter` class registered twice — as `google-id` and as `apple-id` | `Veriqa.Core.Contracts` (MIT) only |
| `host/` | One process in three roles: the embedded issuer, the "platform" of the two channels (provider sign-in, phone pages, signed relay webhook) and the relying party the desktop signs in to | the full embedded set |

## The flow

```
(1) desktop: /signin -> /connect/authorize -> Veriqa sign-in page with a QR code (a hop URL)
(2) phone:   QR -> hop page of the node -> channel link {PublicBaseUrl}/pseudo/{type}/start?tx=...
(3) host:    sends the phone to the provider (authorization code flow)
(4) provider -> {PublicBaseUrl}/pseudo/{type}/callback -> validated claims
(5) host -> signed webhook "start" -> the core calls SendConfirmationPromptAsync -> the adapter
            keeps the question context for the transaction
(6) phone:   page of the sample - where the sign-in goes, from which device, under which
            account; "Yes, sign me in" / "No"
(7) host -> signed webhook "confirm" or "decline"
(8) desktop completes the OIDC flow; the phone sees the outcome
```

`{type}` is `google-id` or `apple-id`. The webhook is `POST /api/channels/{type}/webhook` of this
very host, signed with HMAC-SHA256 of the raw body in the `X-Pseudo-Signature` header: the host
plays the platform that a real channel would have behind it.

## Prerequisites

- .NET SDK 10.0+ and a trusted local HTTPS certificate (`dotnet dev-certs https --trust`).
- **A public HTTPS address** forwarded by an HTTP tunnel to `http://localhost:5370`. A phone cannot
  reach `localhost`, and Apple does not accept `localhost` or an IP address as a return URL at all
  ([Apple: request an authorization][apple-authorize]).
- **Google** — an OAuth client ID and secret with the return address listed among its authorized
  redirect URIs. The address must match exactly, scheme, case and trailing slash included, and
  plain HTTP is refused ([Google: OpenID Connect][google-oidc]).
- **Apple** — a Services ID associated with a primary App ID enabled for Sign in with Apple, the
  domain and the return URL registered for it, and a private key; the Team ID and the key identifier
  go into the client secret, which is a JWT signed with that key
  ([Apple: configure Sign in with Apple for the web][apple-web],
  [Apple: configuring your environment][apple-env]).

A provider with an empty `ClientId` is not registered as a channel, so the sample runs with either
provider, with both, or with none — in the last case there is no channel to show, so
`/connect/authorize` answers `503` with the error `channel_display_failed` and the log says
"No authentication channels available".

## Return addresses to register

| Provider | Return address |
|---|---|
| Google | `https://<public-host>/pseudo/google-id/callback` |
| Apple | `https://<public-host>/pseudo/apple-id/callback` |

## Run it

Secrets come from the environment or from `dotnet user-secrets` (the project has a
`UserSecretsId`), never from `appsettings.json` — the values there are empty.

```bash
cd samples/dotnet/pseudo-channels/host

Veriqa__OpenIddict__Server__Issuer=https://<public-host> \
Veriqa__OpenIddict__Clients__0__AllowedRedirectUris__0=https://<public-host>/signin-oidc \
PseudoChannels__PublicBaseUrl=https://<public-host> \
PseudoChannels__RelaySecret=<any-long-random-string> \
PseudoChannels__Google__ClientId=<google-client-id> \
PseudoChannels__Google__ClientSecret=<google-client-secret> \
PseudoChannels__Apple__ClientId=<apple-services-id> \
PseudoChannels__Apple__TeamId=<apple-team-id> \
PseudoChannels__Apple__KeyId=<apple-key-id> \
PseudoChannels__Apple__PrivateKey="$(cat <path-to-key>.p8)" \
dotnet run
```

Leave out the lines of a provider you do not use. With `Apple__ClientId` set, the other three Apple
values are required — the host refuses to start without them.

**Behind the tunnel three settings name the same public address — set them together:**

| Variable | What it moves |
|---|---|
| `Veriqa__OpenIddict__Server__Issuer` | the issuer, every hop URL built on it, and the `Authority` of the desktop OIDC client (the client reads this very key) |
| `Veriqa__OpenIddict__Clients__0__AllowedRedirectUris__0` | the redirect URI of the desktop client: `https://<public-host>/signin-oidc` |
| `PseudoChannels__PublicBaseUrl` | the channel link and the provider return addresses |

`PseudoChannels:RelayBaseUrl` stays `http://localhost:5370`: the host posts the relay webhook to
itself over loopback, not through the tunnel; the host refuses to start when the value is not an
absolute `http` or `https` URL. Without `RelaySecret` a provider is still registered,
but every webhook is rejected and the relay logs a warning instead of sending.

The listening addresses are `https://localhost:7370` and `http://localhost:5370`
(`Properties/launchSettings.json`). `Program.cs` restores the original scheme and host with
`UseForwardedHeaders()` before `UseVeriqaAuthServer()` and never calls `UseHttpsRedirection` — the
tunnel terminates TLS.

## Walkthrough on two phones

1. On the desktop open `https://<public-host>/` and follow **Sign in**. The Veriqa sign-in page
   shows one QR code (`Veriqa:HopMode:QrMode` is `Unified`).
2. **Android phone, signed in to a Google account.** Scan the QR code with the system camera, pick
   Google on the hop page (when both providers are enabled) and choose the account.
3. The phone lands on **Confirm the sign-in**: *Signing in to* (the client application),
   *Requested from* (the browser and system of the desktop), *Location*, *Started* (in the time
   zone the core knows for the recipient, otherwise in UTC), *Your account*. When the core withholds
   the details of the initiator, the page still asks — with the account alone above the buttons.
   Tap **Yes, sign me in**.
4. The desktop returns to `/` and shows **Signed in** with the claims Veriqa issued: `sub` of the
   form `google-id:<subject at Google>`, `channel_user_id`, `channel_type`, `email`,
   `email_verified`. The phone shows the outcome, or "Answer sent — check the other screen."
5. Follow **Sign out** on the desktop and repeat with an **iPhone**, picking Apple on the hop page.
6. Repeat once more and tap **No**: the desktop shows **Not signed in**. The confirmation address
   opened in any other browser answers that the page works only in the browser that has just signed
   in — the answer is bound to a short-lived cookie set after the provider's return. The question
   is also bound to the account it was asked of: a second account that signs in at the provider for
   the same sign-in sees neither the details nor the buttons, and a sign-in that was canceled or
   ran out of time is not asked about again.

## Limitations

- **Scan with the system camera, not inside a messaging app.** A QR code or link opened in an in-app
  browser reaches Google in an embedded user-agent, which Google refuses with
  `disallowed_useragent` ([Google: OAuth 2.0 policies, "Use secure browsers"][google-policies],
  [Google: authorization errors][google-webserver]).
- **No phone number.** The ID token claims Google documents are `sub`, `email`, `email_verified`,
  `name`, `given_name`, `family_name`, `picture` and `locale` ([Google: OpenID Connect][google-oidc]);
  the scopes Apple accepts are `name` and `email` ([Apple: request an authorization][apple-authorize]).
  An application that needs a verified phone number needs a channel that has one.
- **Apple: the email may be a relay address.** A user may choose an anonymous address that forwards
  to their real one; mail reaches it only from sender domains registered with Apple
  ([Apple: private email relay service][apple-relay]). Treat `email` as a contact address, not as an
  identity — the identity is `sub`.
- **Apple: no name.** The sample requests `openid email` only. Apple returns the name in a separate
  `user` field of the authorization response rather than as an ID token claim
  ([Apple: request an authorization][apple-authorize]) and shares it the first time the user signs
  in, not on later sign-ins ([Apple: authenticating users][apple-users]). The display name of an
  Apple user is therefore the email.
- **Apple: scopes force `form_post`.** With any scope requested Apple posts the response to the
  return URL ([Apple: request an authorization][apple-authorize]); the sample's handler is
  configured accordingly.
- **One device.** When the channel link is opened in the desktop browser itself rather than scanned,
  the flow degenerates into an ordinary sign-in at the provider followed by the same question page.
  Nothing cross-device is shown, and nothing breaks.
- **Why the Yes/No step exists.** A provider may return a result without asking anything: without
  `prompt`, Google shows a consent screen only to a user who has not authorized the application
  yet, so a phone signed in to one Google account that has authorized it passes without a single
  screen. The sample sends `prompt=select_account` so that the account chooser at least shows
  which account is used ([Google: OpenID Connect][google-oidc]); choosing an account still says
  nothing about *which* sign-in it completes. Without step (6) an attacker's QR code shown to a victim would complete the
  attacker's sign-in silently; the page names the application and the requesting device so the
  victim can answer No.
- **State lives in memory.** The question context and the outcome are kept in the process for the
  lifetime of a transaction (`Veriqa:TransactionEngine:TransactionTtlSeconds`); a restart drops
  every sign-in in progress.

The SPI the adapter is written against: [Custom channel adapter](https://veriqa.app/docs/guides/custom-channel-adapter).

[google-oidc]: https://developers.google.com/identity/openid-connect/openid-connect
[google-policies]: https://developers.google.com/identity/protocols/oauth2/policies
[google-webserver]: https://developers.google.com/identity/protocols/oauth2/web-server
[apple-authorize]: https://developer.apple.com/documentation/signinwithapple/incorporating-sign-in-with-apple-into-other-platforms
[apple-env]: https://developer.apple.com/documentation/signinwithapple/configuring-your-environment-for-sign-in-with-apple
[apple-web]: https://developer.apple.com/help/account/capabilities/configure-sign-in-with-apple-for-the-web
[apple-relay]: https://developer.apple.com/documentation/signinwithapple/communicating-using-the-private-email-relay-service
[apple-users]: https://developer.apple.com/documentation/signinwithapple/authenticating-users-with-sign-in-with-apple
