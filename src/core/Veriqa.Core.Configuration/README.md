# Veriqa.Core.Configuration

**Every Veriqa setting is resolved in one place.** A key is declared once, with its owning
level, and one resolver applies the order of the core, tenant and application levels.

Part of [Veriqa](https://veriqa.app), an open-source project for passwordless
authentication through popular messaging apps and email, over standard OpenID Connect.
Documentation: [Configuration keys](https://veriqa.app/docs/guides/config-keys).

## About this package

The settings mechanism shared by the Veriqa core assemblies. A setting is declared once as a
typed key with an owning level, and every read goes through one resolver that applies the declared
precedence of levels and the gate rules of that key — instead of each assembly reading
`IConfiguration` its own way.

A host normally does not install this package by hand: it arrives with `Veriqa.Core.AuthServer` and
with the channel contour. It ships separately because the assemblies on both sides of a settings
axis depend on it and not on each other — the audit journal resolves its logging keys without
referencing the OIDC server.

## Wiring

`AddVeriqaAuthServer` registers the resolver itself. A host that uses the mechanism without the
auth server registers it directly:

```csharp
builder.Services.AddVeriqaConfigurationResolver();

// The builder overload is the only replacement point of the mechanism: what it does not
// offer is not replaceable.
builder.Services.AddVeriqaConfigurationResolver(config => { /* replaceable parts */ });
```

A key declared by catalog is bound at each of its levels over the NODE reader stated for that
level. A contour that keeps the record of a level in a store of its own states its reader before
the composition that ships the default, and the first reader stated for a level wins; a level
nobody else serves gets its only reader the same way:

```csharp
// Before AddVeriqaAuthServer: the application level is read from the contour's store, and the
// tenant level — absent from a self-hosted deployment — is served at all.
builder.Services.AddVeriqaConfigLevelNodeReader<CloudApplicationNodeReader>(ConfigLevel.Application);
builder.Services.AddVeriqaConfigLevelNodeReader<CloudTenantNodeReader>(ConfigLevel.Tenant);
```

## Declaring keys of your own

The catalog is not reserved for the core assemblies. Any extension shipped as a separate assembly —
a channel adapter, an audit sink, an external admissibility check — declares its own keys through
the same public API, on the same levels, with the same semantics, domain, dimensions and cache
policy:

```csharp
public static class AcmeConfigKeys
{
    private static readonly ConfigKeyCatalog Declared = new();

    public static ConfigKey<int> MaxAttempts { get; } = Declared
        .Of<int>("Acme.Approvals.MaxAttempts")
        .At(ConfigLevel.Core, "Acme:Approvals:MaxAttempts")
        .At(ConfigLevel.Application, "AcmeMaxAttempts")
        .At(ConfigLevel.UiConfig, "acme_max_attempts")
        .Default(3)
        .Declare();

    public static ConfigKeyCatalog Catalog => Declared;
}

// From the extension's OWN composition. The call is idempotent per catalog, suppresses no other
// owner's catalogs, and the order of the compositions does not matter.
services.AddVeriqaConfigKeyCatalogs(configuration, AcmeConfigKeys.Catalog);
```

That the readers of the level records are `internal` does not close the application and `ui_config`
levels to an extension. An owner needs an ADDRESS INSIDE the record, not the record: a reader knows
no key name, which is why it stays internal, and the record it fetches takes members nobody declared
in advance — the OIDC client entry is read as a node by path, and the `ui_config` record preserves
unknown fields through `[JsonExtensionData]`. Only the CORE level is addressed from the root of the
host configuration; above it an address is relative to the record of its own level, and an address
that reaches past that record stops the start.

What an extension cannot do is replace the way a level's record is fetched — that is a decision of
the deployment, stated by the node reader above.

The client-facing walkthrough of this path is the
[configuration keys guide](https://veriqa.app/docs/guides/config-keys).

## License

Mozilla Public License 2.0 — see the `LICENSE` file in the repository root; the package carries
the `MPL-2.0` license expression in its metadata.

---
Veriqa™ — © Dmitrii Erusov. Sources and mirrors: https://veriqa.app · devs@veriqa.app
