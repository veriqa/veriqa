### Prerequisites

- .NET SDK 10.0+
- A running Docker daemon (the transaction store is a container started by Aspire)
- `dotnet add package Veriqa.Core.AuthServer` and `Veriqa.Core.TransactionEngine.Redis`
- Channels: `dotnet add package Veriqa.Core.BaseChannels` (Telegram, WhatsApp, Email) and
  `dotnet add package Veriqa.Core.ChannelAdapter.Max`; if you need a single channel, install just
  its package (`Veriqa.Core.ChannelAdapter.Telegram`, …)
- (optional) a demo bot token for a real sign-in through a messaging app

### Run steps

1. Paste the orchestration fragment into the AppHost `Program.cs`.
2. `dotnet run --project samples/dotnet/aspire/login`
3. In a second terminal run the client application the issuer signs users in to, pointed at
   this issuer:
   `dotnet run --project samples/dotnet/inproc/login-client -- --Oidc:Authority=https://localhost:7320`
4. Open `https://localhost:7020/`. The sign-in starts by itself: the client sends you to the
   sign-in page of the issuer, and after you confirm in a channel it brings you back and shows
   `Hello, {name}!`.

The application the dashboard links to (`https://localhost:7320`) is the issuer: its `/` page
starts no sign-in — it only answers `Hello!`.

### About this combination

login — standard user sign-in through a trusted channel. The aspire mode differs
from inproc not in how Veriqa is plugged in — that is the same `AddVeriqaAuthServer` call — but
in who owns the infrastructure: the transaction store is declared as an AppHost resource and its
address reaches the application through service discovery (`ConnectionStrings:transactions`)
rather than configuration. Channels: `telegram`, `whatsapp`, `max`, `email` — all 4 are registered and
toggled individually (`Veriqa:Channels:<Channel>:Enabled`), all disabled by default.

:::note Running the application without its AppHost is not supported: with no store connection
string the startup fails explicitly — a guard against silently falling back to another store.
