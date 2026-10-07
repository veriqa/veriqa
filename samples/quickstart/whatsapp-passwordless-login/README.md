# WhatsApp passwordless login for ASP.NET Core — OpenID Connect in Docker

Passwordless sign-in through WhatsApp for an ASP.NET Core app: a standard OpenID Connect (OIDC)
provider in one Docker container, plus a minimal client app that signs in against it.

## What you get

Open the client app and it sends you to the sign-in page. On a computer you scan the QR code with
your phone; on the phone itself you tap a button. WhatsApp opens with a prepared message to your
business number, you send it with one tap — and you are back in the app, signed in, looking at the
claims the provider issued. No password, no form, no SMS code.

The provider is [Veriqa](https://veriqa.app): an OpenID Connect server built on OpenIddict that
confirms sign-ins in messaging apps — Telegram, WhatsApp, other channels or Email. WhatsApp runs
over the Meta Cloud API. The client knows nothing about Veriqa: it is the stock ASP.NET Core OpenID
Connect handler (Authorization Code + PKCE), so the same setup works for any stack with an OIDC
library.

Each folder next to `rp/` is one way to connect WhatsApp, with a stand of its own; they share the
client app in `rp/`. `built-in/` uses the WhatsApp channel built into Veriqa.

```
built-in/            the stand with the WhatsApp channel built into Veriqa
  docker-compose.yml   Veriqa from the published image, WhatsApp channel only
  .env.example         the values you fill in
rp/                  the client app (ASP.NET Core, Microsoft.AspNetCore.Authentication.OpenIdConnect)
```

## Before you start

- [Docker](https://docs.docker.com/get-docker/) with Compose
- [.NET SDK 10](https://dotnet.microsoft.com/download) — for the client app and the HTTPS
  certificate
- a Meta app with WhatsApp: a business phone number, its phone number ID, an access token and the
  app secret, from [Meta for Developers](https://developers.facebook.com/)
- **a public HTTPS address.** Unlike the bot channels, WhatsApp has no polling: Meta delivers every
  message to a webhook, so the stand must be reachable from the internet — see
  [A public address for the webhook](#a-public-address-for-the-webhook).

## Run it

**1. Fill in the settings.**

```bash
cd built-in
cp .env.example .env
```

Put the values of your Meta app into the `WHATSAPP_*` variables (the file says where each one is),
choose any random string for `WHATSAPP_WEBHOOK_VERIFY_TOKEN` and any password for `CERT_PASSWORD`.

**2. Create the HTTPS certificate.** OpenID Connect needs HTTPS even locally: the provider serves
it at `https://localhost:8443`, the client at `https://localhost:7020`. Both use the .NET
development certificate:

```bash
dotnet dev-certs https --trust
dotnet dev-certs https -ep certs/localhost.pfx -p <CERT_PASSWORD from .env>
chmod 644 certs/localhost.pfx
```

`--trust` makes your machine trust the certificate; on Linux follow what the command prints. The
`chmod` (Linux and macOS) lets the non-root user of the container read the file; on Windows skip it
and write the path as `certs\localhost.pfx`.

**3. Start the provider.**

```bash
docker compose up -d
docker compose ps
```

Wait until the status reads `healthy`, then check that it is ready:
`https://localhost:8443/health/ready` answers `200`.

**4. Connect the webhook** — once, see the next section. Until it is connected the provider runs,
but the messages users send never reach it, so no sign-in completes.

**5. Start the client.**

```bash
dotnet run --project ../rp -- --Oidc:Channel whatsapp
```

**6. Sign in.** Open `https://localhost:7020`, scan the QR code with your phone and send the
prepared message in WhatsApp. You land back on the client with the list of your claims. `/signout`
drops the local session so you can go again.

## A public address for the webhook

The stand publishes the webhook port `8080` on this machine only (`127.0.0.1:8080`). Put any HTTPS
tunnel in front of it that gives you a public `https://` address forwarding to
`http://localhost:8080`, then in the Meta console of your app, WhatsApp → Configuration:

- callback URL — `https://<your tunnel address>/api/channels/whatsapp/webhook`;
- verify token — the value of `WHATSAPP_WEBHOOK_VERIFY_TOKEN`;
- subscribe to the `messages` field.

Meta checks the URL with a request Veriqa answers by itself; every later delivery is signed and
checked against `WHATSAPP_APP_SECRET`. The sign-in pages themselves stay on `https://localhost` —
only Meta needs the public address. What else differs between a local stand and a deployed one:
[Testing the integration locally](https://veriqa.app/docs/integrations/local-testing).

## How it fits together

- `rp/` asks the provider for a sign-in with `acr_values=channel:whatsapp`, so the sign-in page goes
  straight to WhatsApp. Without `--Oidc:Channel` it offers every enabled channel.
- `built-in/docker-compose.yml` registers the client (`rp`, redirect
  `https://localhost:7020/signin-oidc`), sets the issuer to `https://localhost:8443` and enables the
  WhatsApp channel.
- The stand runs in the `Development` environment: sessions live in memory and tokens are signed
  with development certificates, so a restart forgets everything. That is what makes it one
  container — it is a local stand, not a deployment.

## Going further

- WhatsApp channel settings, including the prefilled message text:
  [Channels → WhatsApp](https://veriqa.app/docs/guides/channels#whatsapp).
- A production setup with a database, real certificates and a reverse proxy:
  [Quickstart self-hosted](https://veriqa.app/docs/quickstart/self-hosted).
- Veriqa itself — the source, the other samples and the documentation:
  [gitlab.com/veriqa/veriqa](https://gitlab.com/veriqa/veriqa) · [veriqa.app](https://veriqa.app).

## License

This sample is MIT-licensed — copy it into your own code freely. Veriqa itself is open source under
MPL-2.0.
