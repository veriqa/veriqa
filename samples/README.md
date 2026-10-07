# Veriqa samples

Copy-into-your-code examples of integrating Veriqa — not a demo application. Everything under
`samples/**` is **MIT-licensed** (see `.editorconfig` in this directory), so the code here is meant
to be lifted into a consuming project verbatim.

One thing is worth knowing before reading any of them: **Veriqa lives on the issuer side**. An
application that only signs users in stays a plain OpenID Connect relying party — no Veriqa package,
no SDK. Veriqa packages appear only where the application *is* the issuer (embedded, in-process) or
where it calls the confirmation API server-to-server.

## Two kinds of sample

- **Runnable** — real .NET projects, part of `Veriqa.sln`; each has its own `README.md` with
  prerequisites, ports and configuration. Start one with `dotnet run --project <path>`.
- **Code to copy** — a single file per language (Java, Node, Python, and the external-issuer .NET
  form): the complete client wiring for that stack, ready to be lifted into your own application.
  It ships without a project file, environment or dependency manifest, so it is read and copied
  rather than started — the surrounding application is yours.

## The samples

| Path | What it shows | Form |
|---|---|---|
| `dotnet/inproc/login` | Sign-in with Veriqa embedded in the application (`AddVeriqaAuthServer` / `MapVeriqaAuthServer`); the OpenIddict issuer runs inside the process | runnable · `https://localhost:7300` |
| `dotnet/inproc/login-client` | The relying party of that issuer: a stock ASP.NET Core OIDC client (cookie + `AddOpenIdConnect`) with nothing Veriqa-specific in it | runnable · `https://localhost:7020` |
| `dotnet/inproc/confirmation` | Server-to-server confirmation of a backend action ("Approve a payment of 42.00 EUR?") — no sign-in, no browser redirect | runnable · `https://localhost:7310` |
| `dotnet/inproc/step-up` | Step-up before a destructive action: only the owner of the account may confirm the deletion | runnable · `https://localhost:7330` |
| `dotnet/inproc/agent-approval` | Human-in-the-loop approval of an AI agent's tool call: the money-moving tool waits for a person | runnable · `https://localhost:7340` |
| `dotnet/aspire/login` | The same sign-in under .NET Aspire orchestration: the transaction store is an orchestrated resource reached through service discovery (needs Docker) | runnable · app `https://localhost:7320`, dashboard `https://localhost:17320` |
| `dotnet/custom-channel` | A third-party channel plugged in through the public SPI: `adapter/` builds against the MIT contracts package alone, `host/` is the reference wiring | runnable · `https://localhost:7320` |
| `dotnet/showcase/streaming-tv` | **Nova Stream** — a streaming service for a television: sign-in by QR from the sofa, a welcome card built from the channel's claims, recognition of a returning viewer, step-up when buying a title. Remote-control navigation, written for TV browsers (Tizen 5.5, webOS 5) and runnable behind an HTTP tunnel | runnable · `https://localhost:7350` |
| `dotnet/showcase/support-desk` | **Helio** — the support section of a company's site: a public page, sign-in, the customer's card, step-up before closing or deleting a ticket | runnable · `https://localhost:7360` |
| `dotnet/remote/login` | A client of an **external** issuer (self-hosted instance or Veriqa Cloud) — identical code for both, only the configuration differs | code to copy |
| `java/Application.java` | The same external-issuer client on Spring Security OAuth2 Client | code to copy |
| `node/index.ts` | The same client on `openid-client` + Express | code to copy |
| `python/main.py` | The same client on Authlib + FastAPI | code to copy |
| `claude-code/` | A Claude Code plugin: a `git push`, the start of a long command cycle or a costly run waits until a person approves it in the messenger; the costly run takes two approvers in turn | Claude Code plugin + local host |
| `quickstart/<channel>-passwordless-login` | One directory per channel: Veriqa from the published Docker image with that one channel, and the shared minimal OIDC client `quickstart/rp` that lists the claims after sign-in. Each directory is also published as its own repository — [Telegram](https://github.com/veriqa/telegram-passwordless-login), [WhatsApp](https://github.com/veriqa/whatsapp-passwordless-login), [Email](https://github.com/veriqa/email-passwordless-login) and other channels | runnable · Docker + `https://localhost:7020` |
| `docker-compose/` | Self-hosted deployment of the auth server behind Nginx with PostgreSQL, Redis and RabbitMQ — **demonstration only**, production belongs on an orchestrator with real secret management | deployment sample |

The Aspire sample and the custom-channel host both listen on port 7320, so run them one at a time.

## Showcase samples

The two samples under `dotnet/showcase/` are the exception to "not a demo application": each one
imitates a whole product — a fictional streaming service and a fictional support desk — so the
flows can be seen end to end as a user meets them. Each is still one process in three roles: the
embedded issuer, the OIDC client of the browser, and the backend that creates confirmations. What
they show on top of the smaller samples:

- **The person's card** from the claims the channel gave: name, picture, `@username` and a masked
  user id (Telegram), a masked phone (WhatsApp), an address (Email). Identifiers are masked on the
  server; the full value never reaches the browser.
- **Step-up addressed to the signed-in person.** The confirmation names the identity of the current
  session (`expected_identities`), and the operation is applied only when `matched_type` agrees — a
  confirmation from another account changes nothing.
- **Receipts that differ by purpose.** A sign-in's question is replaced by "Sign-in to … confirmed ✅"
  (`ReplacePrompt`, global); a confirmation keeps its question and gets the receipt as a message of
  its own (`NewMessage`, stated in the backend client's entry).
- **The Veriqa pages left as they are.** The sign-in page and the confirmation window are the
  product's own screens; only brand, colour, theme and — for the television — a stylesheet are set
  through `AuthPageDesign`.

A Telegram bot delivers its updates to one webhook address, so with one bot token only one of the
two samples can take sign-ins at a time. Running behind a tunnel (and on a television) is described
in the README of `streaming-tv`.

## Agent harness sample

`claude-code/` shows Veriqa inside a ready-made agent harness rather than in code of your own. The
plugin's hooks stop chosen actions — tool calls, long command cycles, costly runs — create a
confirmation server-to-server, show the person the QR code Veriqa returned and let the action run
only on `confirmed`; every answer lands in the Veriqa audit trail. It is not a .NET project and is
not part of `Veriqa.sln`: it runs in Claude Code against a Veriqa host on the same machine, which
is there only to make the demo quick to set up. Claude Code is the example; the approach carries
over to another harness. Setup, rules and limits are in its README.

## Running the .NET samples

- .NET SDK 10.0+ and a trusted local HTTPS certificate (`dotnet dev-certs https --trust`).
- **Enable at least one channel and give it credentials.** Every sample ships with all channels
  `"Enabled": false`, and with none enabled the sign-in page has nothing to offer and reports
  `no_channels_available`; the confirmation samples have nowhere to send their question. A demo bot
  token is the quickest route — see the `Veriqa:Channels` section of the sample's `appsettings.json`
  and its own README.
- The pairs (`login` + `login-client`) start issuer first: the client fails discovery if the issuer
  is not up.

## Editing a sample

The sample files carry `region:snippet` markers. The product site cuts the code shown in its demo
builder out of exactly these files, and the `demo-sample-consistency` gate fails when a snippet in
the documentation drifts from its sample. Keep the markers intact, and re-run the gate after
changing marked code.

## Further reading

- [.NET quickstart](https://veriqa.app/docs/quickstart/dotnet) — embedded mode end to end
- [Self-hosted quickstart](https://veriqa.app/docs/quickstart/self-hosted) — Veriqa as a standalone issuer
- [Scenarios](https://veriqa.app/docs/guides/scenarios) — sign-in, confirmation, step-up, agent approval
- [Approvals in Claude Code](https://veriqa.app/docs/research/claude-code-approvals) — what the `claude-code` plugin shows
- [Custom channel adapter](https://veriqa.app/docs/guides/custom-channel-adapter) — the SPI the `custom-channel` sample uses

Russian mirrors of these pages live under `https://veriqa.app/docs/ru/…`.
