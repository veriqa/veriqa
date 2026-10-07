# Installing Veriqa

Three delivery models, all available today. Pick one:

- **Embedded (NuGet)** — Veriqa runs inside your ASP.NET Core host as an OpenIddict connector.
- **Standalone (Docker)** — Veriqa runs as its own OIDC auth server; your application, on any
  stack, is a plain OIDC client and references no Veriqa package.
- **OS service (archive)** — the same standalone auth server without Docker: a self-contained
  executable plus an installer that registers it as a systemd unit on Linux or a Windows
  service. The machine needs no .NET runtime.

The step-by-step guides, with every configuration key explained, live on the documentation site:
[Quickstart .NET](https://veriqa.app/docs/quickstart/dotnet),
[Quickstart self-hosted](https://veriqa.app/docs/quickstart/self-hosted) and
[Install as an OS service](https://veriqa.app/docs/quickstart/os-service). This page is the
short version.

> Samples use placeholders (`my-app`, bot tokens). Replace them with your own values and never
> commit real secrets: keep them in user-secrets in development and a secret store in production.

## Embedded (NuGet)

Requires .NET 10.

### 1. Install the packages

```bash
dotnet add package Veriqa.Core.AuthServer
dotnet add package Veriqa.Core.TransactionEngine

# Channels — one metapackage with the base set (Telegram, WhatsApp, Email):
dotnet add package Veriqa.Core.BaseChannels
# MAX is not part of the metapackage — install it separately when you need it:
dotnet add package Veriqa.Core.ChannelAdapter.Max
```

Need a single channel? Install just that one: `Veriqa.Core.ChannelAdapter.Telegram`,
`.WhatsApp`, `.Max`, `.Email`. Each brings only its own SDK.

For production storage on PostgreSQL add the migrations assemblies of both stores:

```bash
dotnet add package Veriqa.Core.AuthServer.Migrations.PostgreSql
dotnet add package Veriqa.Core.TransactionEngine.Migrations.PostgreSql
```

SQL Server is supported the same way (`…Migrations.SqlServer`).

### 2. Register the services

```csharp
using Veriqa.Core.AuthServer.DependencyInjection;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.TransactionEngine.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddVeriqaAuthServer(builder.Configuration, builder.Environment, authServer =>
{
    // OpenIddict store (clients, authorizations, tokens). There is no default — choose it
    // explicitly. In-memory is for development only; see the docs for UseOpenIddictDatabase(...).
    authServer.UseInMemoryOpenIddictStore();

    // Transaction store: in-memory for development; te.UseEfCoreStore(...) for production.
    authServer.ConfigureTransactionEngine(te => te.UseInMemoryStore());

    // Add only the channels you actually use.
    authServer.AddChannelAdapters(adapters =>
    {
        adapters.AddTelegram();
        adapters.AddEmail();
    });
});

var app = builder.Build();

app.UseSecurityHeaders();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapVeriqaAuthServer();   // OIDC endpoints, channel webhooks, transaction status, live updates

app.Run();
```

### 3. Configure

Everything Veriqa owns lives under the root `Veriqa` key: channel sections under
`Veriqa:Channels`, OIDC clients under `Veriqa:OpenIddict:Clients`. Each channel adapter
activates only when its section has `"Enabled": true`. Outside `Development` the server
requires a signing and an encryption certificate for tokens
(`Veriqa:OpenIddict:Server:SigningCertificatePath` / `EncryptionCertificatePath`) and refuses
to start without them.

The full key list, with the channel-specific settings (bot tokens, webhooks, e-mail provider),
is in the [configuration reference](https://veriqa.app/docs/reference/configuration).

A runnable sample: [`samples/dotnet/inproc/login/`](../samples/dotnet/inproc/login/).

## Standalone (Docker)

### 1. Get the image

The image of every release is published to the container registry of this repository:

```bash
docker pull registry.gitlab.com/veriqa/veriqa
```

Without a tag Docker takes `latest` — the newest release. To pin a release in production, add its
version as the tag (`registry.gitlab.com/veriqa/veriqa:<version>`). The published image is `linux/amd64`; on
another architecture, build it from the repository root instead — `build/docker/Dockerfile`
publishes the same host — and use your local tag in place of the image name below:

```bash
docker build -f build/docker/Dockerfile -t veriqa-authserver .
```

The image runs as a non-root user, exposes port **8080** over HTTP and ships a `HEALTHCHECK`
against `/health/live`. TLS terminates at your reverse proxy in front of it. The image declares
its license in its metadata (`org.opencontainers.image.licenses=MPL-2.0`), so an image scanner
reports exactly that.

### 2. Run it

Every setting is an environment variable; nested keys use a double underscore
(`Veriqa:Channels:Telegram:BotToken` → `Veriqa__Channels__Telegram__BotToken`). Put the values
in an env-file rather than on the command line:

```bash
docker run -d --name veriqa -p 8080:8080 \
  -v "$(pwd)/appsettings.Production.json:/app/appsettings.Production.json:ro" \
  --env-file veriqa.env \
  registry.gitlab.com/veriqa/veriqa
```

Three things are mandatory for anything real, and the container refuses to start without them:

- **the OpenIddict store** — `Veriqa__OpenIddict__Database__Provider` (`PostgreSQL` or
  `SqlServer`) and its connection string; `InMemory` is the explicit development choice;
- **the token certificates** — signing and encryption, as PFX files mounted into the container
  (`…SigningCertificatePath` / `…EncryptionCertificatePath`) or inline as Base64;
- **at least one OIDC client** under `Veriqa:OpenIddict:Clients` — easiest as a mounted
  `appsettings.Production.json`.

### 3. Point your application at it

Your app is a standard OIDC client: authority `https://auth.your-domain.com`, the client id you
declared, response type `code`. Nothing Veriqa-specific goes into it — any conformant OIDC
client library works: .NET, Node, Go, Java, Python, PHP, and the popular CMS platforms as an
external OIDC provider.

## OS service (archive)

No container and no .NET on the machine: a release carries three self-contained archives —
`linux-x64`, `linux-arm64`, `win-x64` — and a `SHA256SUMS.txt` file over them. Each archive
holds the auth server and the installer for its OS.

### 1. Download and check the archive

The file name carries the version, so there is no ready-made URL to keep. Take the repository
address and the current version from [veriqa.app/source](https://veriqa.app/source), then:

```bash
VERIQA_VERSION=…       # version of the release you are installing
VERIQA_DOWNLOADS=…     # …/-/releases/permalink/latest/downloads, from veriqa.app/source

curl -fLO "$VERIQA_DOWNLOADS/veriqa-authserver-$VERIQA_VERSION-linux-x64.tar.gz"
curl -fLO "$VERIQA_DOWNLOADS/veriqa-authserver-$VERIQA_VERSION-SHA256SUMS.txt"

sha256sum --ignore-missing -c "veriqa-authserver-$VERIQA_VERSION-SHA256SUMS.txt"
```

`--ignore-missing` matters: the sums file lists all three archives, and without the flag
`sha256sum` fails on the two you did not download. On Windows the equivalent is `Get-FileHash`
against the line of `win-x64.zip`.

### 2. Run the installer of your OS

The archive unpacks into a single `veriqa-authserver/` directory. Run the installer **from that
directory** — on Linux as root, on Windows in an elevated PowerShell (5.1 and 7 both work):

```bash
tar -xzf "veriqa-authserver-$VERIQA_VERSION-linux-x64.tar.gz"
cd veriqa-authserver
sudo ./install.sh
```

```powershell
Expand-Archive ".\veriqa-authserver-$version-win-x64.zip" -DestinationPath .
cd .\veriqa-authserver
.\install.ps1
```

The installer copies the program, creates the configuration and data directories, generates the
token certificates, writes a start-up configuration and registers the service — on Linux as the
systemd unit `veriqa` under a system account of the same name, on Windows as the service
`Veriqa` under the virtual account `NT SERVICE\Veriqa`. It registers the service but leaves it
stopped.

Defaults worth knowing: the program goes to `/opt/veriqa` or `%ProgramFiles%\Veriqa`, the
configuration to `/etc/veriqa` or `%ProgramData%\Veriqa`, and the service listens on
`http://127.0.0.1:8080` — **loopback only**. Both installers take `--install-directory` /
`-InstallDirectory` and `--urls` / `-Urls` to change that, and `--uninstall` / `-Uninstall` to
take the installation off again.

### 3. Start it and check

```bash
sudo systemctl start veriqa && curl -f http://127.0.0.1:8080/health/live
```

```powershell
Start-Service Veriqa
```

> **What you have now is a running service, not a production one.** The start-up configuration
> the installer wrote uses an **in-memory** store — clients and tokens are lost on every
> restart — and **self-signed** token certificates generated on this machine. A real store, real
> certificates, your OIDC clients and the channels are the move to production, and they are the
> same settings as in the Docker model.

**SQL Server on Windows** needs one file the `win-x64` archive does not ship:
`Microsoft.Data.SqlClient.SNI.dll`, Microsoft's native network library for the driver, under
Microsoft's own license terms. With `SqlServer` selected and the file missing, the service refuses
to start and says which package to take it from; the steps are in
[SQL Server on Windows](https://veriqa.app/docs/quickstart/os-service#sql-server-on-windows).
PostgreSQL and the Linux archives need nothing.

Everything else — where the files live, the environment variables of the service, HTTPS,
updating, removal and the common failures — is on
[Install as an OS service](https://veriqa.app/docs/quickstart/os-service).

## Source code

Veriqa is developed in the open. The repository and its mirrors are listed at
[veriqa.app/source](https://veriqa.app/source).
