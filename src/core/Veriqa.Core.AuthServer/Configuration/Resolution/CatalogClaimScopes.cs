// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// The scope each claim belongs to, as the effective catalog of scopes and claims states it (SPEC-041
/// C31): the ONE answer to "which scope gates this claim", read alike by the writer of the requirements
/// of a transaction (a claim whose scope is not granted is required of no one — C35) and by the claim
/// destinations (a claim whose scope is not granted reaches no token — R21).
/// </summary>
internal sealed class CatalogClaimScopes
{
    /// <summary>
    /// Scope of each claim a catalog row declares, by claim name.
    /// </summary>
    private readonly Dictionary<string, string> _catalogScopeByClaim;

    /// <summary>
    /// Creates the reading over the scope of each catalog row.
    /// </summary>
    /// <param name="catalogScopeByClaim">Scope of each claim a catalog row declares.</param>
    private CatalogClaimScopes(Dictionary<string, string> catalogScopeByClaim)
    {
        _catalogScopeByClaim = catalogScopeByClaim;
    }

    /// <summary>
    /// Reads the scope of each claim out of a catalog.
    /// </summary>
    /// <param name="catalog">Effective catalog; an empty one declares no claim.</param>
    /// <returns>The reading.</returns>
    public static CatalogClaimScopes Of(IReadOnlyList<ScopeDefinition> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var scopeByClaim = new Dictionary<string, string>(VeriqaClaimTypes.NameComparer);

        foreach (var scope in catalog.Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Name)))
        {
            var rows = (scope.Claims ?? Enumerable.Empty<ClaimDefinition>())
                .Where(claim => claim is not null && !string.IsNullOrWhiteSpace(claim.Name));

            foreach (var row in rows)
            {
                scopeByClaim.TryAdd(row.Name, scope.Name);
            }
        }

        return new CatalogClaimScopes(scopeByClaim);
    }

    /// <summary>
    /// Resolves the effective catalog of a relying party and reads the scope of each claim out of it.
    /// </summary>
    /// <param name="resolver">Canonical resolver of the effective setting values.</param>
    /// <param name="tenantId">Tenant of the relying party; null — the tenant level is not stated.</param>
    /// <param name="clientId">OIDC client identifier of the relying party; null — not stated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reading.</returns>
    public static async ValueTask<CatalogClaimScopes> ResolveAsync(
        IConfigurationResolver resolver,
        string? tenantId,
        string? clientId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        using var scope = new ConfigResolutionScope(ResolutionContext.Of(tenantId, clientId, uiConfigSelector: null));
        var catalog = (await resolver.ResolveAsync(
                ScopesClaimsConfigKeys.ScopeCatalog, scope, ConfigDimensionValues.None, cancellationToken))
            .Value ?? [];

        return Of(catalog);
    }

    /// <summary>
    /// Scope that gates a claim: the scope of its catalog row, otherwise the standard scope of the claim.
    /// </summary>
    /// <param name="claim">Claim name.</param>
    /// <returns>Scope name; null when neither the catalog nor a standard scope names the claim.</returns>
    public string? ScopeOf(string claim) =>
        _catalogScopeByClaim.GetValueOrDefault(claim) ?? ClaimDestinationsHelper.StandardScopeOf(claim);
}
