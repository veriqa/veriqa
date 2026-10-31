# Veriqa sample — .NET · inproc · login

Runnable sample of the **login** scenario on the **.NET** stack in **In-process** mode
(`inproc`): Veriqa is embedded directly into the application as a library (`Veriqa.Core.AuthServer`),
the OpenIddict issuer runs inside the process. This is the only public way of inproc integration
(`AddVeriqaAuthServer` / `MapVeriqaAuthServer`).

> This directory is the **single source of truth** for the code snippet and the instruction shown on
> the demo site. The integration code is cut out of `Program.cs` by the region markers
> `snippet` / `snippet-routing`; the cell instruction is `doc.<locale>.md`.

## Prerequisites

- .NET SDK 10.0+
- A trusted local HTTPS development certificate — `dotnet dev-certs https --trust`. The paired client
  fetches this issuer's metadata over HTTPS and refuses an untrusted certificate. On Linux the
  command also prints an `SSL_CERT_DIR` value to export — see the
  [.NET quickstart](https://veriqa.app/docs/quickstart/dotnet).
- (optional) a demo bot token for the Telegram channel, if you want a real sign-in via a messaging app

## Running

```bash
dotnet run --project samples/dotnet/inproc/login
```

The application starts at `https://localhost:7300` (see `Properties/launchSettings.json`).
Its root route `/` answers `Hello!` and nothing more: it tells the host is up.

This application is the issuer, so it starts no sign-in and has no page for a signed-in user. A
sign-in is started by a relying party and ends there: run the paired client sample in a second
terminal and open `https://localhost:7020/` — after the sign-in that page shows `Hello, {name}!`.
See [`../login-client`](../login-client).

```bash
dotnet run --project samples/dotnet/inproc/login-client
```

## Configuration

- `appsettings.json` — transaction TTL, OpenIddict (in-memory), the `my-app` demo client.
- `appsettings.Development.json` — channel tokens for development (channels are disabled by default).
- `Veriqa:Channels:OutcomeNotice:DisplayIntent` — where the receipt of the outcome appears in the
  conversation. Stated as the shipped `ReplacePrompt`: the receipt takes the place of the message
  that asked the question. `Veriqa:MessageTemplates:outcome-receipt-confirmed` / `…-declined` word
  those receipts so that they name the application through the server slot `{app}`.
- Transaction storage is in-memory (`UseInMemoryStore`); for production switch to
  EF Core / Redis (see the comments in `Program.cs`).

## What the sample demonstrates

1. Wiring up the Veriqa AuthServer in inproc mode (region `snippet` in `Program.cs`).
2. OIDC routing with a single `MapVeriqaAuthServer` call (region `snippet-routing`).
3. The root route as a plain "the host is up" page — the result page belongs to the client.

## Further reading

- Quickstart for this mode — <https://veriqa.app/docs/en/quickstart/dotnet>
- Veriqa as a standalone auth server — <https://veriqa.app/docs/en/quickstart/self-hosted>
- How a sign-in flows through a trusted channel — <https://veriqa.app/docs/en/concepts/auth-flow>
