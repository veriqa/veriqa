// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// What the configuration requires of the claims in one ownership context (SPEC-041 C30, L40): the
/// effective catalog and the level of every claim the configuration can state a level for.
/// </summary>
/// <param name="Catalog">Effective catalog of scopes and claims — the tenant's one or the core one.</param>
/// <param name="Levels">
/// Level of each claim of the catalog and of <c>phone_number</c> and <c>email</c>, already combined by
/// seniority across the catalog row, the application raise and the require flags.
/// </param>
internal sealed record ConfiguredClaimRequirements(
    IReadOnlyList<ScopeDefinition> Catalog,
    IReadOnlyDictionary<string, ClaimRequirementLevel> Levels)
{
    /// <summary>
    /// Level the configuration requires of a claim.
    /// </summary>
    /// <param name="claim">Claim name.</param>
    /// <returns>
    /// The level; <see cref="ClaimRequirementLevel.IfAvailable"/> for a claim the configuration says nothing
    /// about.
    /// </returns>
    public ClaimRequirementLevel LevelOf(string claim) =>
        Levels.GetValueOrDefault(claim, ClaimRequirementLevel.IfAvailable);
}
