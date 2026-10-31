# Veriqa sample — .NET · client (relying party) · login

Runnable sample of the **client side** of the login scenario: an ordinary ASP.NET Core application
that signs users in against a Veriqa issuer using the standard authentication stack
(`AddAuthentication` + `AddCookie` + `AddOpenIdConnect`).

There is **nothing Veriqa-specific in this project** — no Veriqa package, no `ProjectReference` to
`src/core`. Veriqa lives on the issuer side; this application is a plain OpenID Connect relying
party (Authorization Code + PKCE) and would look identical against any conformant OIDC provider.
That is the point of the sample.

> The issuer counterpart is [`../login`](../login) (`samples/dotnet/inproc/login`), where Veriqa is
> embedded in the process. This directory is **not** part of the demo-stand sample gallery: the
> gallery addresses form directories by axis codes (language × mode × scenario), and `login-client`
> is not a scenario code, so no combination resolves here. Editing this sample cannot break the
> gallery's SSOT snippets.

## Prerequisites

- .NET SDK 10.0+
- A trusted local HTTPS development certificate — `dotnet dev-certs https --trust`. The OIDC handler
  fetches issuer metadata over HTTPS and refuses an untrusted certificate.
- The issuer sample running (see below).
- **At least one channel enabled on the issuer, with its credentials.** The issuer sample ships with
  all four channels `"Enabled": false`, and with none enabled the sign-in page has nothing to offer
  and reports `no_channels_available`. Enable the channel you want in
  `samples/dotnet/inproc/login/appsettings.json` (or `appsettings.Development.json`) and set its
  credentials — a demo Telegram bot token is the quickest route. This is required to
  *finish* a sign-in, not optional.

## Running

Two hosts, in this order — the client fails its discovery call if the issuer is not up yet:

```bash
# terminal 1 — the issuer (Veriqa embedded, in-process)
dotnet run --project samples/dotnet/inproc/login

# terminal 2 — this client
dotnet run --project samples/dotnet/inproc/login-client
```

The client starts at `https://localhost:7020` and the issuer at `https://localhost:7300` (see each
project's `Properties/launchSettings.json`). Open `https://localhost:7020/` — an anonymous visitor
is challenged straight into the Veriqa login page; after confirming in a trusted channel you land
back here and see `Hello, {name}!`.

Use the `https` address. The profile also publishes `http://localhost:5020`, but the application
redirects it to HTTPS on purpose: a flow started over plain HTTP would send
`redirect_uri=http://localhost:5020/signin-oidc`, which is not in `AllowedRedirectUris` (the
comparison is literal), and the handler's correlation cookie — `Secure`, `SameSite=None` — would
not survive the round trip either.

Reaching `Hello, {name}!` requires an enabled channel (see prerequisites). With all channels off
the flow stops at the sign-in page with `no_channels_available` — the pair is wired correctly, but
there is nothing to confirm in.

`GET /signout` drops the local cookie and returns to `/`, which lets you replay the flow without
clearing browser state by hand.

Pressing **Cancel sign-in** on the Veriqa page brings the browser
back with `error=access_denied`. The handler's `AccessDeniedPath` turns that answer into a redirect
to `GET /signin-cancelled`, a plain page that says so; without the path the answer would be handled
as any other failed return, below. The page is a route of its own because `/` challenges an anonymous
visitor and would put the user straight back on the sign-in page.

Any other failed return — `error=server_error` when the sign-in broke down on the issuer's side, for
one — raises the handler's `OnRemoteFailure` event. The sample handles it and redirects to
`GET /signin-failed`, an equally plain page that offers to start over and does not show the error
text, which is written to the log instead; unhandled, that return ends in an HTTP 500.

## The pairing, and why it needs no configuration

The issuer sample already registers exactly the client this project is:

```json
// samples/dotnet/inproc/login/appsettings.json
"Clients": [
  {
    "ClientId": "my-app",
    "AllowedRedirectUris": [ "https://localhost:7020/signin-oidc" ],
    ...
  }
]
```

So three values have to agree, and out of the box they do:

| Client (`appsettings.json` here) | Issuer (`Veriqa:OpenIddict:Clients[]`) |
| --- | --- |
| `Oidc:Authority` = `https://localhost:7300` | the issuer's own address |
| `Oidc:ClientId` = `my-app` | `ClientId` |
| `CallbackPath` = `/signin-oidc` (on port 7020) | `AllowedRedirectUris` entry |

`redirect_uri` is compared **literally** — wildcards are not supported. Change the client's port and
you must change `AllowedRedirectUris` on the issuer to match, or authorization fails with
`invalid_request`.

## No client secret — deliberate

`my-app` is registered without a `ClientSecret`, so Veriqa seeds it as a **public** client and
requires PKCE (S256) from it. `options.UsePkce = true` is set here and `ClientSecret` is left unset;
adding a secret on this side only would break the exchange. For a confidential client, set
`ClientSecret` in both places.

## What the sample demonstrates

1. The standard ASP.NET Core relying-party wiring against a Veriqa issuer (cookie session + OIDC
   challenge).
2. Discovery-driven configuration: only `Authority` and `ClientId` — no endpoint URLs are hardcoded.
3. Claims arriving from the issuer (`GetClaimsFromUserInfoEndpoint`) and tokens retained in the
   session (`SaveTokens`).

## Known limitation — sign-out is local only

Veriqa exposes no end-session (RP-initiated logout) endpoint, so there is no federated sign-out to
propagate. `/signout` clears this application's cookie; the issuer's own session is untouched and a
subsequent challenge may complete without a fresh channel confirmation.

## References

- Quickstart: [.NET quickstart](https://veriqa.app/docs/quickstart/dotnet) (English; Russian mirror at
  [veriqa.app/docs/ru/quickstart/dotnet](https://veriqa.app/docs/ru/quickstart/dotnet)) — the client application section reproduces this
  wiring.
- The external-issuer variant of the same client code (self-hosted / Cloud, display-only):
  `samples/dotnet/remote/login`.
