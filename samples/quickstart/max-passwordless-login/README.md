# MAX passwordless login for ASP.NET Core — OpenID Connect in Docker

Passwordless sign-in through MAX for an ASP.NET Core app: a standard OpenID Connect (OIDC) provider
in one Docker container, plus a minimal client app that signs in against it.

## What you get

Open the client app and it sends you to the sign-in page. On a computer you scan the QR code with
your phone; on the phone itself you tap a button. MAX opens your bot, you confirm with one tap — and
you are back in the app, signed in, looking at the claims the provider issued. No password, no form,
no SMS code.

The provider is [Veriqa](https://veriqa.app): an OpenID Connect server built on OpenIddict that
confirms sign-ins in messaging apps — MAX, Telegram, WhatsApp and Email among them. The client knows
nothing about Veriqa: it is the stock ASP.NET Core OpenID Connect handler (Authorization Code +
PKCE), so the same setup works for any stack with an OIDC library.

Each folder next to `rp/` is one way to connect MAX, with a stand of its own; they share the
client app in `rp/`. `built-in/` uses the MAX channel built into Veriqa.

```
built-in/            the stand with the MAX channel built into Veriqa
  docker-compose.yml   Veriqa from the published image, MAX channel only
  .env.example         the values you fill in
rp/                  the client app (ASP.NET Core, Microsoft.AspNetCore.Authentication.OpenIdConnect)
```

## Before you start

- [Docker](https://docs.docker.com/get-docker/) with Compose
- [.NET SDK 10](https://dotnet.microsoft.com/download) — for the client app and the HTTPS
  certificate
- a MAX bot and its token. Bots are created on the MAX partner platform, which requires a verified
  organization, sole proprietor or self-employed profile — see the
  [MAX bot documentation](https://dev.max.ru/docs). Use a bot no other server is polling.

No public address is needed: the stand runs MAX in long polling mode, the bot fetches its updates
itself.

## Run it

**1. Fill in the settings.**

```bash
cd built-in
cp .env.example .env
```

Put your bot token into `MAX_BOT_TOKEN` and pick any password for `CERT_PASSWORD`.

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

**4. Start the client.**

```bash
dotnet run --project ../rp -- --Oidc:Channel max
```

**5. Sign in.** Open `https://localhost:7020`, scan the QR code with your phone and confirm in MAX.
You land back on the client with the list of your claims. `/signout` drops the local session so you
can go again.

## How it fits together

- `rp/` asks the provider for a sign-in with `acr_values=channel:max`, so the sign-in page goes
  straight to MAX. Without `--Oidc:Channel` it offers every enabled channel.
- `built-in/docker-compose.yml` registers the client (`rp`, redirect
  `https://localhost:7020/signin-oidc`), sets the issuer to `https://localhost:8443` and enables the
  MAX channel.
- The stand runs in the `Development` environment: sessions live in memory and tokens are signed
  with development certificates, so a restart forgets everything. That is what makes it one
  container — it is a local stand, not a deployment.

## Going further

- MAX channel settings, including the webhook mode for a deployed server:
  [Channels → MAX](https://veriqa.app/docs/guides/channels#max).
- A production setup with a database, real certificates and a reverse proxy:
  [Quickstart self-hosted](https://veriqa.app/docs/quickstart/self-hosted).
- Veriqa itself — the source, the other samples and the documentation:
  [gitlab.com/veriqa/veriqa](https://gitlab.com/veriqa/veriqa) · [veriqa.app](https://veriqa.app).

## License

This sample is MIT-licensed — copy it into your own code freely. Veriqa itself is open source under
MPL-2.0.
