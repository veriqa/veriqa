// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// The one reading of what the configuration requires of the claims (SPEC-041 C30, L40): the effective
/// catalog and, for every claim it can state a level for, the senior of the level of its catalog row, the
/// raise of the application (<see cref="ScopesClaimsConfigKeys.ClaimLevel"/>) and the require flags
/// (<see cref="ScopesClaimsConfigKeys.RequirePhone"/>, <see cref="ScopesClaimsConfigKeys.RequireEmail"/>).
/// Combining by seniority is what makes a lower application value change nothing.
/// <para>
/// The writer of the requirements of a transaction and the startup report on an unobtainable phone read
/// the levels here, so the two cannot disagree about which claim a level requires.
/// </para>
/// </summary>
internal sealed class ClaimRequirementSettings
{
    /// <summary>
    /// The canonical resolver of the effective setting values.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Creates the reading.
    /// </summary>
    /// <param name="resolver">Canonical resolver.</param>
    public ClaimRequirementSettings(IConfigurationResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// Resolves the catalog and the configured levels of the claims in one scope of resolution.
    /// </summary>
    /// <param name="scope">Scope of resolution — the ownership context the values are resolved for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The effective catalog and the configured level of each claim.</returns>
    public async ValueTask<ConfiguredClaimRequirements> ResolveAsync(
        ConfigResolutionScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // The method takes the level of each catalog row, raises it by the level the application states
        // for that claim, and then applies the require flags of phone_number and email.

        var catalog = (await _resolver.ResolveAsync(
                ScopesClaimsConfigKeys.ScopeCatalog, scope, ConfigDimensionValues.None, cancellationToken))
            .Value ?? [];

        var levels = new Dictionary<string, ClaimRequirementLevel>(VeriqaClaimTypes.NameComparer);

        foreach (var row in catalog
            .SelectMany(scopeRow => scopeRow?.Claims ?? Enumerable.Empty<ClaimDefinition>())
            .Where(claim => claim is not null && !string.IsNullOrWhiteSpace(claim.Name)))
        {
            levels.TryAdd(row.Name, row.Level);
        }

        // Claims with a completion path of their own (SPEC-041 L92) are levelled even without a catalog
        // row: the application may raise them and the require flags address them.
        foreach (var claim in ScopesClaimsCatalogRules.OwnCompletionPathClaims)
        {
            levels.TryAdd(claim, ClaimRequirementLevel.IfAvailable);
        }

        foreach (var claim in levels.Keys.ToList())
        {
            var raised = (await _resolver.ResolveAsync(
                    ScopesClaimsConfigKeys.ClaimLevel,
                    scope,
                    ConfigDimensionValues.Of((ScopesClaimsConfigKeys.ClaimDimensionName, claim)),
                    cancellationToken))
                .Value;

            // A value outside the enumeration raises nothing: the startup validation refuses such a text,
            // and a live edit of the client entry after the start must not invent a level.
            if (Enum.IsDefined(raised) && raised > levels[claim])
            {
                levels[claim] = raised;
            }
        }

        if ((await _resolver.ResolveAsync(
                ScopesClaimsConfigKeys.RequirePhone, scope, ConfigDimensionValues.None, cancellationToken)).Value)
        {
            levels[VeriqaClaimTypes.PhoneNumber] = ClaimRequirementLevel.Required;
        }

        if ((await _resolver.ResolveAsync(
                ScopesClaimsConfigKeys.RequireEmail, scope, ConfigDimensionValues.None, cancellationToken)).Value)
        {
            levels[VeriqaClaimTypes.Email] = ClaimRequirementLevel.Required;
        }

        return new ConfiguredClaimRequirements(catalog, levels);
    }
}
