# Veriqa.ServiceDefaults

**OpenTelemetry, health checks and HTTP resilience for a Veriqa host in one call.** The same
defaults the Veriqa hosts use, for your own host.

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Observability](https://veriqa.app/docs/guides/observability).

## About this package

Shared service wiring for the .NET Aspire hosts of Veriqa: OpenTelemetry (traces, metrics,
logs), health check endpoints, service discovery, the default HTTP resilience handler, forwarded
headers for a reverse proxy and rate-limiting helpers. It is the standard Aspire "service defaults"
project of the solution, shipped as a package so a host outside this repository can take the same
defaults.

The package is MIT-licensed: it contains host plumbing, not the authentication core.

## Install

```bash
dotnet add package Veriqa.ServiceDefaults
```

## Wiring

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var app = builder.Build();
app.MapDefaultEndpoints();   // /health and /alive, exempt from rate limiting
```

## License

MIT — see the `PackageLicenseExpression` of this package.

## Trademark

Veriqa™ is a trademark of Dmitrii Erusov. This package's MIT license covers the code, not the name
or the logo — `licenses/TRADEMARK.md` in the repository states what the policy allows without asking.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
