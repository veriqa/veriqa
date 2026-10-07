# Veriqa.Core.AuthServer

**Veriqa** — an embeddable OpenIddict connector for cross-device authentication through trusted channels: Telegram, WhatsApp, MAX, and Email.

Plug Veriqa into an existing ASP.NET Core application and get a ready-made OIDC Authorization Code Flow with QR codes, deep links through popular messengers, and email magic links.

---

## Quick start

### 1. Installation

```bash
dotnet add package Veriqa.Core.AuthServer
dotnet add package Veriqa.Core.ChannelAdapter
dotnet add package Veriqa.Core.TransactionEngine
```

### 2. Registering services

```csharp
// Program.cs
using Veriqa.Core.AuthServer.DependencyInjection;

builder.Services.AddVeriqaAuthServer(builder.Configuration, builder.Environment, authServer =>
{
    // OpenIddict store (clients, authorizations, tokens). It has no default — say the choice out
    // loud, or the host fails to start outside Development. For development, the volatile one:
    authServer.UseInMemoryOpenIddictStore();

    // For production — a relational provider instead of the line above (see "EF Core transaction
    // store and migrations" below, which covers the OpenIddict store as well):
    // authServer.UseOpenIddictDatabase(ef => ef.UseNpgsql(connectionString,
    //     npgsql => npgsql.MigrationsAssembly("Veriqa.Core.AuthServer.Migrations.PostgreSql")));

    // Configure the Transaction Engine (transaction store)
    authServer.ConfigureTransactionEngine(te =>
    {
        // For development — the InMemory store:
        te.UseInMemoryStore();

        // For production — EF Core (requires the Veriqa.Core.TransactionEngine.EntityFrameworkCore
        // satellite package; host-owned provider choice, SPEC-001 §10.2):
        // te.UseEfCoreStore(ef => ef.UseNpgsql(connectionString,
        //     npgsql => npgsql.MigrationsAssembly("Veriqa.Core.TransactionEngine.Migrations.PostgreSql")));

        // Or Redis (requires the Veriqa.Core.TransactionEngine.Redis satellite package):
        // te.UseRedisStore(opts => opts.Configuration = "...");
    });

    // Wire up the channel adapters. Each channel ships in a package of its own — the base set as
    // the Veriqa.Core.BaseChannels metapackage, MAX added on top of it, or a single channel taken
    // alone; this package depends on none of them. See Veriqa.Core.ChannelAdapter/README.md.
    authServer.AddChannelAdapters(adapters =>
    {
        adapters.AddTelegram();  // Telegram channel
        adapters.AddWhatsApp();  // WhatsApp channel
        adapters.AddMax();       // MAX channel
        adapters.AddEmail();     // Email channel (magic link / one-tap email)
    });
});
```

### 3. Middleware pipeline

```csharp
// Program.cs
var app = builder.Build();

app.UseSecurityHeaders();   // CSP, X-Frame-Options
app.UseStaticFiles();       // SignalR JS client
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Register all Veriqa AuthServer endpoints.
// The Email channel endpoints (/auth/email/*, /api/channels/email/inbound) are mapped
// automatically when the channel is enabled in configuration (Veriqa:Channels:Email:Enabled = true).
app.MapVeriqaAuthServer();

app.Run();
```

### 4. Configuration (`appsettings.json`)

```json
{
  "Veriqa": {
    "Channels": {
      "Telegram": {
        "Enabled": true,
        "BotToken": "YOUR_BOT_TOKEN",
        "UpdateMode": "Webhook",
        "WebhookBaseUrl": "https://your-domain.com",
        "WebhookSecretToken": "YOUR_SECRET"
      },
      "WhatsApp": {
        "Enabled": true,
        "Provider": "MetaCloudApi",
        "BusinessPhoneNumber": "+15551234567",
        "MetaCloudApi": {
          "PhoneNumberId": "YOUR_PHONE_NUMBER_ID",
          "AccessToken": "YOUR_ACCESS_TOKEN",
          "AppSecret": "YOUR_APP_SECRET",
          "WebhookVerifyToken": "YOUR_VERIFY_TOKEN"
        }
      }
    },
    "TransactionEngine": {
      "TransactionTtlSeconds": 300
    },
    "OpenIddict": {
      "Clients": [
        {
          "ClientId": "my-app",
          "DisplayName": "My Application",
          "AllowedRedirectUris": ["https://localhost:7020/signin-oidc"],
          "AllowedScopes": ["openid", "profile", "phone", "offline_access"],
          "AllowRefreshTokens": true
        }
      ]
    },
    "AuthPageDesign": {
      "Preset": "Branded",
      "PrimaryColor": "#FF5500",
      "LogoUrl": "https://your-domain.com/logo.png"
    }
  }
}
```

The WhatsApp delivery provider expected by the installation is declared by
`Veriqa:Channels:WhatsApp:Provider` (`MetaCloudApi` — the official path, the default and the only
shipped provider). A provider supplied by the host names itself with its own code and is registered
with `UseWhatsAppProvider<TProvider>()`; the declared code is matched against the registered one at
startup, and a mismatch stops the host.
The Email channel settings (SMTP for magic links, inbound for the one-tap email) live in the
`Veriqa:Channels:Email` section; `PublicBaseUrl` is mandatory when the channel is enabled.

---

## EF Core transaction store and migrations

The database provider choice (`UseNpgsql`), the connection string, and the
migrations assembly belong to the **host** (SPEC-001 §10.2). The `Veriqa.Core.TransactionEngine`
core carries no ORM at all: `UseEfCoreStore` arrives in a satellite package, and the migrations
live in a separate assembly shipped as a package of the same name:

```bash
dotnet add package Veriqa.Core.TransactionEngine.EntityFrameworkCore
dotnet add package Veriqa.Core.TransactionEngine.Migrations.PostgreSql
```

A host that uses a relational store references the migrations assembly and points to it
via `MigrationsAssembly(...)`. The schema is initialized at startup through a single
`MigrateAsync` path (migration history); `EnsureCreated` is not used.

The same applies to the OpenIddict store:

```bash
dotnet add package Veriqa.Core.AuthServer.Migrations.PostgreSql
```

The OpenIddict store follows the same host-owned model, and it has **no default**: the library does
**not** read a provider out of configuration, and a host that states no choice fails to start
outside the `Development` environment (in `Development` it starts and warns). A relational provider
is supplied as a delegate — through the facade builder or, when wiring the low-level API, through
the `configureDatabase` parameter. The volatile in-memory store is a choice like any other and is
stated with `authServer.UseInMemoryOpenIddictStore()` (or, without the builder,
`services.UseInMemoryOpenIddictStore()`):

This package no longer references any EF Core provider, so a host that opts into a relational
provider adds the `PackageReference` itself — `Npgsql.EntityFrameworkCore.PostgreSQL` for the
`UseNpgsql` call below, plus the matching `Veriqa.Core.AuthServer.Migrations.PostgreSql` assembly
named in `MigrationsAssembly(...)`.

```csharp
builder.Services.AddVeriqaAuthServer(builder.Configuration, builder.Environment, authServer =>
{
    authServer.UseOpenIddictDatabase(ef => ef.UseNpgsql(connectionString,
        npgsql => npgsql.MigrationsAssembly("Veriqa.Core.AuthServer.Migrations.PostgreSql")));
});
```

### Design-time (`dotnet ef`)

To generate migrations, the design-time factories read the connection string from environment
variables: **`TRANSACTION_DB_CONNECTION_STRING`** (the transaction store) and
**`OPENIDDICT_DB_CONNECTION_STRING`** (OpenIddict). If the variable is not set, a placeholder is
used (`Password=dev-local`) — it is only suitable for generating migrations without connecting to a
database; for commands that require a real database (`database update`), the variable is mandatory:

```bash
# example: a new PostgreSQL migration of the transaction store
dotnet ef migrations add <Name> --project src/core/Veriqa.Core.TransactionEngine.Migrations.PostgreSql
```

---

## Offline GeoIP for the initiator context

The confirmation message can show the approximate region the sign-in came from. Resolving it needs
a local GeoIP database, and that database is not something this product can ship: a GeoLite2 file
comes from MaxMind under their own EULA and your account. So the library that reads it travels in a
satellite package instead of riding along with every installation:

```bash
dotnet add package Veriqa.Core.AuthServer.MaxMind
```

```csharp
builder.Services.AddMaxMindGeoIp();
```

Without the package the shipped provider resolves nothing: geo fields stay null and the rest of the
context is collected as usual (graceful degradation). `Veriqa:InitiatorContext:CollectGeoLocation`
is `true` by default, so a default installation with no provider behind it does get a startup
warning carrying the code `initiator_context_geoip_unavailable` — on every boot, until either this
satellite is installed or the setting is turned off (`"CollectGeoLocation": false`). That is the
same behaviour an installation with an unconfigured database already had. The database path and the
rest of the settings are described in the satellite's own README.

---

## Endpoints

`MapVeriqaAuthServer` maps the standard OIDC contract and the auxiliary endpoints:

| Endpoint | Method | Description |
|---|---|---|
| `/connect/authorize` | GET | Entry point of the OIDC Authorization Code Flow |
| `/connect/authorize/callback` | GET | Callback after confirmation in the channel |
| `/connect/authorize/callback/confirm` | POST | Confirming the sign-in with a button on the web page (page confirmation mode) |
| `/connect/token` | POST | Exchange the authorization code for tokens |
| `/connect/userinfo` | GET | Retrieve the user's claims |
| `/connect/revoke` | POST | Token revocation (RFC 7009) — handled natively by OpenIddict |
| `/connect/introspect` | POST | Token introspection (RFC 7662) — handled natively by OpenIddict; token metadata only, for confidential clients with `AllowIntrospection` |
| `/api/transaction/{id}/status` | GET | Transaction status polling (fallback for non-WebSocket) |
| `/hubs/auth` | WebSocket (SignalR) | Live transaction status for the sign-in page |
| `/api/channels/telegram/webhook` | POST | Telegram channel adapter webhook |
| `/api/channels/whatsapp/webhook` | GET/POST | WhatsApp channel adapter Hub Challenge and webhook |
| `/api/channels/max/webhook` | POST | MAX channel adapter webhook |

The Email channel endpoints are mapped by the same `MapVeriqaAuthServer` automatically — only
when the channel is enabled (`Veriqa:Channels:Email:Enabled = true`). The channel contributes them
itself, through the endpoint seam every channel may use, so this package holds no knowledge of them:

| Endpoint | Method | Description |
|---|---|---|
| `/auth/email/start` | GET/POST | Email entry page and magic link sending (Pull) |
| `/auth/email/confirm` | GET/POST | Magic link confirmation (Pull) |
| `/auth/email/push/compose` | GET | Push-mode compose page (the one-tap email) |
| `/api/channels/email/inbound` | POST | Inbound email webhook from the inbound provider (Push) |

---

## Extension points (replaceable services)

Veriqa provides several extension points for customizing behavior:

### Substitution does not depend on call order

The shipped defaults of `IAuthPageRenderer` and `IClaimsMapper` are registered with `TryAdd*`, so a
substitution you declare **always wins over the built-in default** — the result is the same for
every reachable call order:

```csharp
// 1. Use*<T>() inside the AddVeriqaAuthServer configure delegate
builder.Services.AddVeriqaAuthServer(configuration, environment, authServer =>
{
    authServer.UseAuthPageRenderer<MyAuthPageRenderer>();
    authServer.UseClaimsMapper<MyClaimsMapper>();
});

// 2. Use*<T>() after AddVeriqaAuthServer, through the builder captured from the delegate
VeriqaAuthServerBuilder? captured = null;
builder.Services.AddVeriqaAuthServer(configuration, environment, authServer => captured = authServer);
captured!.UseAuthPageRenderer<MyAuthPageRenderer>();

// 3. A plain registration made before AddVeriqa* — no builder involved.
//    You pick the lifetime yourself here, so take it from the table below.
builder.Services.AddSingleton<IAuthPageRenderer, MyAuthPageRenderer>();
builder.Services.AddScoped<IClaimsMapper, MyClaimsMapper>();
builder.Services.AddVeriqaAuthServer(configuration, environment);

// 4. RemoveAll + Add after AddVeriqa* — still supported, unchanged
builder.Services.RemoveAll<IAuthPageRenderer>();
builder.Services.AddSingleton<IAuthPageRenderer, MyAuthPageRenderer>();
```

Calling `Use*<T>()` more than once is allowed — the last call wins. Without any of these calls
the built-in default is resolved.

> `Use*<T>()` **before** `AddVeriqaAuthServer` is not a supported order: the builder only exists
> inside the configure delegate. Use option 3 when you want to register without a builder.

#### Lifetimes of the extension points

Which registration wins is one question; which lifetime your implementation gets is another.
`Use*<T>()` picks the lifetime for you; options 3 and 4 leave that choice to you, so match the
column below. This table is where the README settles lifetimes — the sections further down point
back to it instead of restating it, and the source of truth behind it is the registrations in
`VeriqaAuthServerBuilder` and `VeriqaServicesExtensions`.

| Extension point | Shipped default | `Use*<T>()` registers your type as | Register it yourself as (options 3 and 4) |
|---|---|---|---|
| `IAuthPageRenderer` | `TryAddSingleton` | `AddSingleton` | `AddSingleton` |
| `IClaimsMapper` | `TryAddSingleton` | `AddScoped` | `AddScoped` — the seam runs per authorization request, and `AddSingleton` here would make a scoped dependency (a `DbContext`, a unit of work) captive |

Both defaults are safe as singletons, for two different reasons: the default mapper is stateless
and takes no dependencies at all, while the default renderer depends only on
`IConfirmationPromptLocalizer`, itself a singleton. The lifetime of a substitution follows what
that seam is used for, not what its default happens to be.

### Replacing the UI renderer

Implement `IAuthPageRenderer` for full control over the authentication HTML page:

```csharp
public sealed class MyAuthPageRenderer : IAuthPageRenderer
{
    public string RenderAuthPage(AuthPageRenderContext context)
    {
        // Your HTML with QR codes and channel buttons
        return $"<html>...</html>";
    }
}

// Registration
authServer.UseAuthPageRenderer<MyAuthPageRenderer>();
```

### Replacing the claims mapper

Implement `IClaimsMapper` for custom mapping of the resolved identity into OIDC claims:

```csharp
public sealed class MyClaimsMapper(IMyDirectory directory) : IClaimsMapper
{
    public async Task<Result<IReadOnlyList<Claim>>> MapToClaimsAsync(
        ResolvedIdentitySnapshot resolvedIdentity,
        CompletionSnapshot completion,
        ClaimsMappingContext context,
        CancellationToken cancellationToken = default)
    {
        // Your mapping logic
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, resolvedIdentity.Subject)
        };

        // Additional channel claims live in the resolvedIdentity.Claims dictionary
        if (resolvedIdentity.Claims?.TryGetValue("name", out var name) is true)
        {
            claims.Add(new(ClaimTypes.Name, name));
        }

        // The mapping runs on the request thread, so enriching the claims from your own
        // directory or database is a plain await — no sync-over-async is needed
        var profile = await directory.LoadAsync(resolvedIdentity.Subject, cancellationToken);
        claims.Add(new(ClaimTypes.Role, profile.Role));

        return Result<IReadOnlyList<Claim>>.Success(claims.AsReadOnly());
    }
}

// Registration
authServer.UseClaimsMapper<MyClaimsMapper>();
```

The mapper is **scoped** — one instance per authorization request (see the lifetime table above).
That is what lets it take a scoped dependency (`DbContext`, a unit of work, anything per-request)
straight through the constructor, as `IMyDirectory` above: no `IServiceScopeFactory` dance is
needed.

`ClaimsMappingContext` states who the claims are being built for:

| Member | Meaning |
|---|---|
| `ClientId` | `client_id` of the requesting relying party; `null` when the request carries none |
| `TenantId` | tenant attribution of the request; `null` when the tenant level is not stated |
| `Scopes` | scopes requested by the relying party |

The attribution is what a pairwise subject identifier needs (OIDC Core 1.0 §8.1): the same end user
gets a different `sub` per relying party. The built-in mapper does not use it — the `sub` it issues is
global across relying parties.

### Adding a transaction event handler

Implement `ITransactionEventHandler` for custom handling of transaction events:

```csharp
authServer.AddTransactionEventHandler<MyTransactionEventHandler>();
```

The handler is added **alongside** the built-in ones rather than replacing them: the port is
consumed as `IEnumerable<ITransactionEventHandler>`, so every registered handler receives every
event. The shipped handlers keep running — the sign-in window still gets its completion push, and
the audit trail still gets its records.

---

## Using acr_values

A client application can request a specific authentication channel via the OIDC `acr_values` parameter:

```
GET /connect/authorize
  ?client_id=my-app
  &response_type=code
  &scope=openid profile
  &acr_values=channel:telegram
```

Supported values:
- `channel:telegram` — authentication via Telegram only
- `channel:whatsapp` — authentication via WhatsApp only
- `channel:max` — authentication via MAX only
- `channel:email` — authentication via Email only (magic link)
- multiple values separated by spaces (e.g. `channel:telegram channel:email`) — the user
  chooses from the listed channels; without the parameter all enabled channels are available

---

## CSS variables

The authentication page is styled through CSS variables with the `--veriqa-*` prefix
(SPEC-015 §2.1). The full set declared by the page by default:

```css
:root {
  /* Primary — a neutral canonical value per theme (light: #111827, dark: #f9fafb);
     the client color applies only in the Branded preset via PrimaryColor / CustomCss */
  --veriqa-primary: #111827;
  --veriqa-on-primary: #ffffff;
  /* Surfaces and background (light theme) */
  --veriqa-bg: #ffffff;
  --veriqa-surface: #f9f9fa;
  --veriqa-surface-variant: #e2e2e3;
  --veriqa-surface-container: #eeeeef;
  /* Text */
  --veriqa-text: #1a1c1d;
  --veriqa-muted: #4c4546;
  --veriqa-soft: #767071;
  /* Semantic colors */
  --veriqa-success: #16a34a;
  --veriqa-error: #dc2626;
  --veriqa-warning: #eab308;
  /* Radii, typography, animations */
  --veriqa-radius: 8px;
  --veriqa-radius-lg: 12px;
  --veriqa-font-family: system-ui, -apple-system, "Segoe UI", Roboto, Arial, sans-serif;
  --veriqa-font-mono: "JetBrains Mono", Consolas, monospace;
  --veriqa-transition-fast: 160ms ease;
}
```

Wire up your own CSS through configuration (it is applied **after** the base styles — CFG-032,
which lets you override any variable):

```json
{
  "Veriqa": {
    "AuthPageDesign": {
      "CustomCssPath": "/css/veriqa-custom.css",
      "CssSriHash": "sha384-..."
    }
  }
}
```

### Design presets and theme

The page's look is set by the `Veriqa:AuthPageDesign:Preset` preset (SPEC-012 §4.2):

| Preset | Description |
|--------|-------------|
| `Default` | The standard light theme (the default). |
| `Dark` | The dark theme. |
| `Minimal` | QR and buttons only, without decorative elements; the sign-in window renders no brand row. |
| `Branded` | The client's primary color (`PrimaryColor`), which applies **only** with this preset (SPEC-012 §4.2). |

The brand row of the sign-in window is not tied to a preset: in every preset but `Minimal` it shows the
client's logo (`LogoUrl`) and brand name (`BrandName`), stated independently — the row shows whichever
of them is stated. When neither is stated at any level, the row shows the product's own mark (the red
diamond and `Veriqa`); a single stated value replaces that default as a whole. Service pages (web
confirmation, expired callback, email-adapter pages) render no brand row at all.

The color scheme is set by a separate `Veriqa:AuthPageDesign:Theme` setting (SPEC-015 §2.2):

| Theme | Description |
|-------|-------------|
| `Auto` | Follows the OS setting (`prefers-color-scheme`). The default. |
| `Light` | The light (white) theme. |
| `Dark` | The dark theme. |

```json
{
  "Veriqa": {
    "AuthPageDesign": {
      "Preset": "Default",
      "Theme": "Auto"
    }
  }
}
```

The theme sets the `data-theme` attribute (`auto`/`light`/`dark`) on `<html>`, and the preset sets
`data-preset`. The dark values of the variables are declared under `[data-theme="dark"]` and (for
`auto`) under `@media (prefers-color-scheme: dark)`. An explicit `Theme` (`Light`/`Dark`) takes
priority over the preset's color scheme; with `Theme=Auto`, the `Dark` preset also yields a dark
theme. Animations respect `prefers-reduced-motion` (SPEC-015 §2.3). When `CustomCssPath` is set, the
preset is ignored (CFG-023) — the look is fully determined by the user CSS.

---

## Localization

The authentication page uses the **Natural Keys** approach: the localization key is the English
text, the base language is `en` (no translation file is needed for it; SPEC-007 §12, SPEC-015 §7).
The registry of supported languages is built data-driven: the `{language}.json` locale files the
host ships in `wwwroot/locales` (the path is configurable via `Veriqa:Channels:Localization:LocalesPath`),
plus the base `en`. To add a language (for example Russian or Chinese), drop a `ru.json` / `zh.json`
translation file into the locales directory — no code changes are required.

The language is derived from the `Accept-Language` header; if the language is not recognized, the
default language is used:

```json
{
  "Veriqa": {
    "Localization": {
      "DefaultLanguage": "en"
    }
  }
}
```

`DefaultLanguage` is validated at startup: the value must be part of the registry of supported
languages (the host's locale files + `en`); otherwise the application does not start. For example,
`"DefaultLanguage": "ru"` requires the host to ship `wwwroot/locales/ru.json`.

---

## Shipped dependencies worth knowing about

Three entries in this package's dependency graph raise a question on a supply-chain or security
review. All three are known and accepted, and this section is the answer written down before the
question is asked. Measured against the restored graph of this package on 2026-08-28.

### `Microsoft.EntityFrameworkCore.InMemory`

Every installation receives it, and nothing but development runs it. The OpenIddict store has no
default: state it with `UseInMemoryOpenIddictStore()` for development, or hand a relational provider
to `UseOpenIddictDatabase(...)`. The volatile provider is also the fallback of the low-level entry
point, taken when no `configureDatabase` delegate was supplied at all.

What closes the risk is the startup gate, not the packaging: a host that states no store choice
**refuses to start outside `Development`** (CFG-118/CFG-119); inside `Development` it starts and
warns. So the provider Microsoft documents as unfit for production cannot silently end up serving
production traffic.

It costs nothing in graph size either — EF Core is already there through
`OpenIddict.EntityFrameworkCore` and `Microsoft.EntityFrameworkCore.Relational`, and this package
adds no transitive dependency of its own beyond `Microsoft.EntityFrameworkCore`.

### `System.Drawing.Common` and `Microsoft.Win32.SystemEvents`

Both arrive transitively, behind `QRCoder 1.8.0`: the newest dependency group that package declares
is `net6.0`, that group names `System.Drawing.Common 6.0.0`, and it in turn pulls
`Microsoft.Win32.SystemEvents 6.0.0`. Two artefacts of the .NET 6 era therefore sit in the graph of
every deployment, and `System.Drawing.Common` is unsupported outside Windows from .NET 7 on — worth
knowing for an on-premise Linux installation.

**The GDI+ path is never executed.** The only QRCoder renderer this package uses is `PngByteQRCode`
(in `QrCodeService`), which writes PNG bytes directly and touches no drawing type; the Email channel
adapter uses the same one and nothing else. The image of the confirmation creation answer, which
carries the attribution mark under the code, takes only the module matrix from QRCoder and is encoded
by this package itself (`System.IO.Compression`, a pre-rasterized mark from its resources). So the
packages are present, not called.

They are not dropped because there is nowhere to drop them to: QRCoder 1.8.0 is the latest release
and ships no `net8.0`/`net10.0` group, and a satellite would help no one — a QR is rendered in every
installation (the page shows it by default) and in the Email channel's letters.

### `UAParser` and the freeze of its rule set

`UAParser 3.1.47` is the latest release and was published on 2021-05-26; the uap-core regular
expressions it embeds are frozen at the same date. Browsers and operating systems released after it
therefore fall into the `Other` family, and the initiator context then reports an undetermined
device type. The package brings no transitive dependencies — this is a signal-quality limitation,
not a packaging one.

That state is accepted rather than unnoticed. Replacing `UAParser`, `System.Drawing.Common` and
possibly other packages of this graph is a decision to be taken **in operation**, from what
deployments actually report, and not pre-emptively here.

---

## Full example

See `samples/dotnet/inproc/login/` in the repository.

---

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
