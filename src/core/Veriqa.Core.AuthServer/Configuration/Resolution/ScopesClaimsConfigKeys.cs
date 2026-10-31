// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// Resolver keys of the claim requirements (SPEC-041 C30, SPEC-012 §9): the catalog of scopes is owned
/// by the tenant and narrowed by the application through its allowed scopes; the requirement of a claim
/// is a default of the tenant that the application may only make stricter.
/// <para>
/// The catalog is the ONE source of the level a claim has at the core and tenant levels — the level
/// of its row. What an application adds is a raise of that level (<see cref="ClaimLevel"/>) and the two
/// shorthands (<see cref="RequirePhone"/>, <see cref="RequireEmail"/>); the three are combined with the
/// row by seniority where the requirements of a transaction are written, which is what makes a lower
/// application value change nothing.
/// </para>
/// </summary>
public static class ScopesClaimsConfigKeys
{
    /// <summary>
    /// Name of the identity dimension of <see cref="ClaimLevel"/> — the claim whose level is asked.
    /// </summary>
    public const string ClaimDimensionName = "claim";

    /// <summary>
    /// Catalog of the declarations of this registry. It is initialized before the declarations that
    /// fill it, which is the order the initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// The section AS THE PRODUCT SHIPS IT — a default-constructed options object, read for one thing
    /// only: the value the core level answers with where the section states nothing.
    /// </summary>
    private static readonly ScopesClaimsOptions Shipped = new();

    /// <summary>
    /// Domain of a catalog — the rules of <see cref="ScopesClaimsCatalogRules"/>, the same function the
    /// startup validation of the core catalog calls. A catalog outside it leaves its step unset: a
    /// tenant catalog that breaks a rule is not applied and the core catalog stands.
    /// </summary>
    private static readonly ConfigValueDomain<IReadOnlyList<ScopeDefinition>> CatalogDomain = new(
        ScopesClaimsCatalogRules.IsWellFormed,
        "every scope has a unique name; every claim has a name declared in one row only, under its "
        + "standard scope if it has one; a completion description only on a Channel or Collected claim "
        + "outside phone_number, email and the claims the core issues, with a label and a positive maximum "
        + "length, and always on a Collected claim; a level above IfAvailable only on a claim with a "
        + "completion path");

    /// <summary>
    /// Requirement of the phone_number claim (SPEC-041 C31: a shorthand for its Required level).
    /// Protective ceiling: a level below the one that requires the phone cannot lift the requirement.
    /// </summary>
    public static ConfigKey<bool> RequirePhone { get; } = Declared
        .Of<bool>("ScopesClaims.RequirePhone")
        .At(ConfigLevel.Core, ScopesClaimsOptions.SectionName + ":" + nameof(ScopesClaimsOptions.RequirePhone))
        .At(ConfigLevel.Tenant, nameof(ScopesClaimsOptions.RequirePhone))
        .At(ConfigLevel.Application, nameof(OidcClientOptions.RequirePhone))
        .ProtectiveCeiling(static (u, l) => u || l)
        .Default(Shipped.RequirePhone)
        .Declare();

    /// <summary>
    /// Requirement of the email claim — the same rule as <see cref="RequirePhone"/>.
    /// </summary>
    public static ConfigKey<bool> RequireEmail { get; } = Declared
        .Of<bool>("ScopesClaims.RequireEmail")
        .At(ConfigLevel.Core, ScopesClaimsOptions.SectionName + ":" + nameof(ScopesClaimsOptions.RequireEmail))
        .At(ConfigLevel.Tenant, nameof(ScopesClaimsOptions.RequireEmail))
        .At(ConfigLevel.Application, nameof(OidcClientOptions.RequireEmail))
        .ProtectiveCeiling(static (u, l) => u || l)
        .Default(Shipped.RequireEmail)
        .Declare();

    /// <summary>
    /// Catalog of scopes and claims in effect for a transaction (SPEC-041 C30, SPEC-012 CFG-248): the
    /// catalog of the tenant REPLACES the core one as a whole. The application does not state a catalog —
    /// it narrows the scopes through its allowed scopes.
    /// <para>
    /// Neither level carries a path, so the registrar of the catalogs binds neither: the core level is
    /// the bound <c>Veriqa:ScopesClaims:Scopes</c>, read by a getter (<see cref="ScopesClaimsCoreConfigKeys"/>)
    /// over the options the startup validation has already judged; the tenant level is read by no node
    /// reader of a self-hosted deployment.
    /// </para>
    /// <para>
    /// The OpenIddict registration of scopes and the <c>scopes_supported</c> of discovery follow the
    /// CORE catalog, once per process: a scope a tenant catalog states beyond the core one is refused by
    /// OpenIddict as <c>invalid_scope</c>.
    /// </para>
    /// </summary>
    public static ConfigKey<IReadOnlyList<ScopeDefinition>> ScopeCatalog { get; } = Declared
        .Of<IReadOnlyList<ScopeDefinition>>("ScopesClaims.Catalog")
        .AtIdentity(ConfigLevel.Core)
        .AtIdentity(ConfigLevel.Tenant)
        .Domain(CatalogDomain)
        .Declare();

    /// <summary>
    /// Requirement level an APPLICATION raises a claim to (SPEC-041 C30): the claim is the identity
    /// dimension of the key, read at <c>ClaimLevels:{claim}</c> of the client entry. The key has no
    /// level below the application: the level of the core and of the tenant is the level of the catalog
    /// row (<see cref="ScopeCatalog"/>), and the two are combined by seniority where the requirements of a
    /// transaction are written — so the application raises and never lowers.
    /// </summary>
    public static ConfigKey<ClaimRequirementLevel> ClaimLevel { get; } = Declared
        .Of<ClaimRequirementLevel>("ScopesClaims.ClaimLevel")
        .Identity(ClaimDimensionName)
        .At(
            ConfigLevel.Application,
            nameof(OidcClientOptions.ClaimLevels) + ":{" + ClaimDimensionName + "}")
        .Declare();

    /// <summary>
    /// Declarations of this registry — what the one registrar of the deployment declares and binds.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;
}
