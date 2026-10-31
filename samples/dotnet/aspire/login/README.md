# Veriqa sample — .NET · aspire · login

Runnable sample of the **login** scenario on the **.NET** stack under **.NET Aspire** orchestration
(`aspire`). Veriqa is embedded into the application exactly as in the in-process
sample (`AddVeriqaAuthServer` / `MapVeriqaAuthServer`); what Aspire adds is ownership of the
infrastructure — the transaction store is an orchestrated resource and its address reaches the
application through service discovery.

> This directory is the **single source of truth** for the code snippet and the instruction shown on
> the demo site. The orchestration code is cut out of `Program.cs` by the region marker
> `snippet`; the cell instruction is `doc.<locale>.md`.

## Layout

```text
login/                       ← the AppHost project (the entry point of the sample)
 ├─ Program.cs               orchestration: store resource + the application (region snippet)
 └─ app/                     the application that embeds Veriqa
     ├─ Program.cs           AddVeriqaAuthServer with the store address from Aspire
     └─ appsettings.json     channels, OpenIddict, TTL — no store endpoint at all
```

The application lives inside the AppHost directory on purpose: the demo site serves the whole
form directory as one ZIP, so the sample has to be downloadable as a single unit.

## Prerequisites

- .NET SDK 10.0+
- A running Docker daemon (the transaction store is a container started by Aspire)
- (optional) a demo bot token for the Telegram channel, if you want a real sign-in via a
  messaging app

## Running

```bash
dotnet run --project samples/dotnet/aspire/login
```

Aspire starts the store container, then the application, and opens the dashboard
(`https://localhost:17320`, see `Properties/launchSettings.json`). The application itself is at
`https://localhost:7320`; its root route answers `Hello!` and nothing more — it tells the host is
up.

The application is the issuer, so it starts no sign-in and has no page for a signed-in user. A
sign-in is started by a relying party and ends there: run the client sample against this issuer
and open `https://localhost:7020/` — after the sign-in that page shows `Hello, {name}!`.

```bash
dotnet run --project samples/dotnet/inproc/login-client -- --Oidc:Authority=https://localhost:7320
```

The `my-app` client in `app/appsettings.json` is registered with the redirect URI of that client
(`https://localhost:7020/signin-oidc`), so only the issuer address has to be passed.

Running `app/` alone is not supported: without the AppHost there is no store connection string
and startup fails with an explicit error — that is deliberate, so the sample never silently falls
back to another store.

## Configuration

- `app/appsettings.json` — transaction TTL, OpenIddict, the `my-app` demo client, channels
- `Veriqa:Channels:OutcomeNotice:DisplayIntent` — where the receipt of the outcome appears in the
  conversation. Stated as the shipped `ReplacePrompt`: the receipt takes the place of the message
  that asked the question. `Veriqa:MessageTemplates:outcome-receipt-confirmed` / `…-declined` word
  those receipts so that they name the application through the server slot `{app}`.
  (all disabled by default).
- The transaction store is **not** configured here: `ConnectionStrings:transactions` is injected
  by the AppHost (`WithReference`). The resource name in `Program.cs` and the connection name in
  `app/Program.cs` must stay in sync.

## What the sample demonstrates

1. Declaring the transaction store as an orchestrated resource with a data volume (region
   `snippet` in `Program.cs`).
2. Handing its address to the application through service discovery instead of configuration.
3. The Veriqa wiring itself being identical to in-process mode — the deployment mode changes who
   runs the infrastructure, not how Veriqa is plugged in.

## Further reading

- Quickstart for the .NET stack — <https://veriqa.app/docs/en/quickstart/dotnet>
- How a sign-in flows through a trusted channel — <https://veriqa.app/docs/en/concepts/auth-flow>
- In-process variant of the same scenario — `samples/dotnet/inproc/login`
