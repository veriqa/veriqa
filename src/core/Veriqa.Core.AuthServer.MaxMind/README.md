# Veriqa.Core.AuthServer.MaxMind

**Show the user where a sign-in comes from, without sending their IP address anywhere.** The
approximate region of the device that started the sign-in is resolved from a local MaxMind
database.

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Initiator context](https://veriqa.app/docs/concepts/initiator-context).

## About this package

Offline GeoIP provider for the Veriqa initiator context check — a satellite package. The auth
server shows the person confirming a sign-in what device started it; this package adds the
approximate region to that picture.

It ships separately because the data it needs does not ship at all: a GeoLite2/GeoIP2 `.mmdb` file
comes from MaxMind under their own EULA and an account of yours. Without this package the auth
server behaves exactly as it does today with no database configured — the geo fields stay empty and
the rest of the context is shown as usual.

The lookup is offline by design (ICC-013): the initiator's IP is never sent to an online service.

## Install

```bash
dotnet add package Veriqa.Core.AuthServer.MaxMind
```

## Wiring

```csharp
builder.Services.AddMaxMindGeoIp();

builder.Services.AddVeriqaAuthServer(builder.Configuration, builder.Environment, authServer =>
{
    // ... the rest of the wiring
});
```

Either order works: called before `AddVeriqaAuthServer` this registration is left in place, called
after it wins as the later one. Inside the configure delegate the same line reads
`authServer.Services.AddMaxMindGeoIp()`.

Then point the server at the database file you obtained:

```json appsettings.json
{
  "Veriqa": {
    "InitiatorContext": {
      "CollectGeoLocation": true,
      "GeoIp": {
        "Provider": "OfflineDatabase",
        "DatabasePath": "/var/lib/veriqa/GeoLite2-City.mmdb"
      }
    }
  }
}
```

`CollectGeoLocation` is `true` by default — the line above only spells that out. The flip side is
that a default installation with no GeoIP provider warns at every startup with the code below,
until this package is installed or the flag is set to `false`.

Both City and Country databases are accepted — a Country database resolves the country only. If the
path is unset or the file cannot be opened, the provider reports itself unavailable and the geo
fields stay null (graceful degradation); the startup diagnostics of the auth server warn once with
the code `initiator_context_geoip_unavailable`.

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata. The GeoIP database is
licensed separately by MaxMind and is not part of this package.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
