### Prerequisites

- .NET SDK 10.0+
- A trusted local HTTPS development certificate: `dotnet dev-certs https --trust`. The client
  application fetches the issuer metadata over HTTPS itself and refuses an untrusted certificate —
  `https://localhost:7020/` then answers with `IDX20803 … UntrustedRoot` instead of redirecting to
  the sign-in page. On Linux the command alone is not enough: it prints an `SSL_CERT_DIR` value
  that has to be exported in the client's terminal — see the run step of the
  [.NET quickstart](https://veriqa.app/docs/quickstart/dotnet).
- `dotnet add package Veriqa.Core.AuthServer`
- Channels: `dotnet add package Veriqa.Core.BaseChannels` (Telegram, WhatsApp, Email) and
  `dotnet add package Veriqa.Core.ChannelAdapter.Max`; if you need a single channel, install just
  its package (`Veriqa.Core.ChannelAdapter.Telegram`, …)
- (optional) a demo bot token for a real sign-in through a messaging app

### Run steps

1. Paste the integration fragment into `Program.cs`.
2. `dotnet run --project samples/dotnet/inproc/login` — the issuer, `https://localhost:7300`.
3. In a second terminal run the client application the issuer signs users in to:
   `dotnet run --project samples/dotnet/inproc/login-client` — `https://localhost:7020`.
4. Open `https://localhost:7020/`. The sign-in starts by itself: the client sends you to the
   sign-in page of the issuer, and after you confirm in a channel it brings you back and shows
   `Hello, {name}!`.

The `/` page of the issuer itself starts no sign-in — it only answers `Hello!`.

### About this combination

login — standard user sign-in through a trusted channel. The inproc mode runs the OpenIddict
issuer inside the app process with ephemeral keys. Channels: `telegram`, `whatsapp`,
`max`, `email` — all 4 are registered and toggled individually via configuration
(`Veriqa:Channels:<Channel>:Enabled`). All are disabled by default, so `dotnet run` starts without
external tokens/SMTP; set the credentials of the channel you need for a real sign-in.

### One-tap email over IMAP

The `Email` section of `appsettings.json` is prepared for the one-tap email: the user sends a
pre-filled letter, and the sample reads it from a mailbox over IMAP — no inbound mail provider and
no public address are needed.

1. Dedicate a mailbox to Veriqa. Every recent unread letter of its inbox gets marked as read, so
   do not use a mailbox people read.
2. Turn IMAP access on for the mailbox and get an app password for it in the settings of your mail
   provider.
3. Learn the `AuthservId`: open the source of any letter this mailbox has received and take the
   host name the topmost `Authentication-Results` header value opens with, the one before the first
   `;`. A value that opens with a verdict (`spf=pass …`) states no identifier — such a mailbox does
   not work with this sample, nor does one whose identifier differs from letter to letter.
4. In `Veriqa:Channels:Email:Inbound` set `InboundAddress` to the address of the mailbox, and in
   `Imap` set `Host` (the IMAP server of your provider), `Username` and `AuthservId`.
5. Pass the password through the environment variable
   `Veriqa__Channels__Email__Inbound__Imap__Password` — it is never written to `appsettings.json`.
6. Set `Veriqa:Channels:Email:Enabled` to `true`.
7. Run the sample and start the sign-in as in "Run steps", choose Email and send the prepared
   letter from another mailbox. The page completes the sign-in within a few seconds after the
   letter has arrived.

A mailbox that does not accept a password (Microsoft 365, some Google Workspace organizations) is
signed in to with OAuth 2.0: see "Mailbox sign-in with OAuth" in the Email section of the channel
setup guide of the documentation. OAuth changes only how the mailbox is signed in to: the
`AuthservId` requirement of step 3 stays, and a mailbox whose header states no identifier does not
work with OAuth either. The sample itself stays on the app password.

:::note Real OIDC sign-in: the code and guide apply to the run, no mock.
