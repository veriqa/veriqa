// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;

using OpenIddict.Abstractions;

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Rules of a scopes-and-claims catalog, whichever level states it (SPEC-041 C31, L92) — the ONE
/// judgement of a catalog: the startup validation of the core catalog and the domain of the resolver
/// key of the catalog ask it alike, so the catalog of a tenant is held to exactly the rules of the core
/// one. It judges the bound catalog, not the text a deployment wrote: what the configuration binder
/// could not convert is the business of the startup validation, which reads that text.
/// </summary>
internal static class ScopesClaimsCatalogRules
{
    /// <summary>
    /// Claims the core issues or computes itself (SPEC-041 C31, as of its revision 1.18): no catalog row
    /// may describe their completion, and the requirements of a transaction leave them out.
    /// </summary>
    public static IReadOnlySet<string> CoreIssuedClaims { get; } = new[]
    {
        OpenIddictConstants.Claims.Subject,
        OpenIddictConstants.Claims.AuthenticationMethodReference,
        OpenIddictConstants.Claims.AuthenticationTime,
        OpenIddictConstants.Claims.Zoneinfo,
        OpenIddictConstants.Claims.UpdatedAt,
        OpenIddictConstants.Claims.EmailVerified,
        OpenIddictConstants.Claims.PhoneNumberVerified,
        VeriqaClaimTypes.ChannelType,
        VeriqaClaimTypes.ChannelUserId,
        VeriqaClaimTypes.IsBot,
        VeriqaClaimTypes.VeriqaChannel,
        VeriqaClaimTypes.VeriqaEmailMode
    }.ToFrozenSet(VeriqaClaimTypes.NameComparer);

    /// <summary>
    /// Claims with a completion path of their own (SPEC-041 L92): the phone number is obtained through
    /// the channel, the email through its own confirmation — neither needs a completion description.
    /// The reading of the configured claim levels states a level for them even without a catalog row.
    /// </summary>
    public static IReadOnlySet<string> OwnCompletionPathClaims { get; } = new[]
    {
        VeriqaClaimTypes.PhoneNumber,
        VeriqaClaimTypes.Email
    }.ToFrozenSet(VeriqaClaimTypes.NameComparer);

    /// <summary>
    /// Whether Veriqa can complete a claim at all (SPEC-041 L92): always for the phone number and the
    /// email, and for any other claim only when its catalog row describes the completion. The switched
    /// off completion form of an application is not part of this predicate — it is a condition of the
    /// transaction, not of the catalog.
    /// </summary>
    /// <param name="catalog">Effective catalog.</param>
    /// <param name="claim">Claim name.</param>
    /// <returns><c>true</c> when the claim has a completion path.</returns>
    public static bool HasCompletionPath(IReadOnlyList<ScopeDefinition> catalog, string claim) =>
        HasCompletionPath(claim, FindClaim(catalog, claim));

    /// <summary>
    /// Whether a catalog passes every rule of <see cref="Violations"/>.
    /// </summary>
    /// <param name="catalog">Catalog stated by a level.</param>
    /// <returns><c>true</c> when the catalog is admitted.</returns>
    public static bool IsWellFormed(IReadOnlyList<ScopeDefinition>? catalog) =>
        catalog is not null && !Violations(catalog).Any();

    /// <summary>
    /// Every rule a catalog breaks, one message per breach, each addressed relative to the catalog
    /// (<c>Scopes[i]:Claims[j]…</c>). Names of scopes and claims appear in the messages; values of claims
    /// never do — a catalog carries none.
    /// </summary>
    /// <param name="catalog">Catalog stated by a level.</param>
    /// <returns>Messages of the broken rules; empty when the catalog is admitted.</returns>
    public static IEnumerable<string> Violations(IReadOnlyList<ScopeDefinition> catalog)
    {
        // The method walks the catalog once: scope names first, then every claim row against the
        // rules of its own (source × completion × level) and against the rows seen before it.

        var scopeNames = new HashSet<string>(StringComparer.Ordinal);
        var claimRows = new Dictionary<string, string>(VeriqaClaimTypes.NameComparer);

        for (var i = 0; i < catalog.Count; i++)
        {
            var scope = catalog[i];
            var scopeAddress = $"Scopes[{i}]";

            if (scope is null)
            {
                yield return $"{scopeAddress} is empty.";
                continue;
            }

            if (string.IsNullOrWhiteSpace(scope.Name))
            {
                yield return $"{scopeAddress} has an empty scope name.";
            }
            else if (!scopeNames.Add(scope.Name))
            {
                yield return $"{scopeAddress} repeats the scope name '{scope.Name}'.";
            }

            var claims = scope.Claims ?? [];

            for (var j = 0; j < claims.Count; j++)
            {
                foreach (var violation in ClaimRowViolations(scope, claims[j], $"{scopeAddress}:Claims[{j}]", claimRows))
                {
                    yield return violation;
                }
            }
        }
    }

    /// <summary>
    /// Rules of one claim row (SPEC-041 C31, L92) and the two rules it is judged by against the rest of
    /// the catalog: one row per claim name, and a claim of a standard scope only under that scope.
    /// </summary>
    /// <param name="scope">Scope the row stands under.</param>
    /// <param name="row">Claim row.</param>
    /// <param name="address">Address of the row relative to the catalog.</param>
    /// <param name="claimRows">Claim names seen so far with the address of their row.</param>
    /// <returns>Messages of the broken rules.</returns>
    private static IEnumerable<string> ClaimRowViolations(
        ScopeDefinition scope,
        ClaimDefinition? row,
        string address,
        Dictionary<string, string> claimRows)
    {
        if (row is null)
        {
            yield return $"{address} is empty.";
            yield break;
        }

        if (string.IsNullOrWhiteSpace(row.Name))
        {
            yield return $"{address} has an empty claim name.";
            yield break;
        }

        // A row is judged against the values of the closed sets as well: a catalog read from a store
        // record arrives without the text check of the startup validation.
        if (!Enum.IsDefined(row.Level) || !Enum.IsDefined(row.Source))
        {
            yield return $"{address} ('{row.Name}') states a {nameof(ClaimDefinition.Level)} or a "
                + $"{nameof(ClaimDefinition.Source)} that is not a known value.";
        }

        if (!claimRows.TryAdd(row.Name, address))
        {
            yield return $"{address} repeats the claim '{row.Name}' already declared at {claimRows[row.Name]} — "
                + "one claim has one row, otherwise its level and its completion are ambiguous.";
        }

        var standardScope = ClaimDestinationsHelper.StandardScopeOf(row.Name);
        if (standardScope is not null && !string.Equals(standardScope, scope.Name, StringComparison.Ordinal))
        {
            yield return $"{address} declares '{row.Name}' under the scope '{scope.Name}', but it is a claim "
                + $"of the standard scope '{standardScope}' and may be declared only under it.";
        }

        if (row.Completion is { } completion)
        {
            if (CompletionRefusal(row) is { } refusal)
            {
                yield return $"{address}:{nameof(ClaimDefinition.Completion)} is not allowed for '{row.Name}': {refusal}";
            }
            else if (!Enum.IsDefined(completion.Format)
                || string.IsNullOrWhiteSpace(completion.Label)
                || (completion.Format is ClaimFieldFormat.Text && completion.MaxLength <= 0))
            {
                yield return $"{address}:{nameof(ClaimDefinition.Completion)} of '{row.Name}' is incomplete: "
                    + $"{nameof(ClaimCompletionDefinition.Label)} must not be blank, "
                    + $"{nameof(ClaimCompletionDefinition.Format)} must be one of "
                    + $"{string.Join(", ", Enum.GetNames<ClaimFieldFormat>())}, and "
                    + $"{nameof(ClaimCompletionDefinition.MaxLength)} of a text field must be positive.";
            }
            else
            {
                foreach (var violation in OptionViolations(completion, row.Name, $"{address}:{nameof(ClaimDefinition.Completion)}"))
                {
                    yield return violation;
                }
            }
        }
        else if (row.Source is ClaimSource.Collected)
        {
            yield return $"{address} ('{row.Name}') is collected by Veriqa and must describe its completion "
                + $"({nameof(ClaimDefinition.Completion)}).";
        }

        if (row.Level > ClaimRequirementLevel.IfAvailable && !HasCompletionPath(row.Name, row))
        {
            yield return $"{address}:{nameof(ClaimDefinition.Level)} {row.Level} of '{row.Name}' cannot be "
                + "enforced: the claim has no completion path — only phone_number, email and a claim "
                + $"describing its {nameof(ClaimDefinition.Completion)} have one.";
        }
    }

    /// <summary>
    /// Rules of the list of values of a completion (SPEC-041 C95): a choice states at least one value;
    /// every value is not blank, carries no white space at its edges, is no longer than a completed
    /// value may be and is stated once; every caption is not blank; a format other than a choice states
    /// no list at all.
    /// </summary>
    /// <param name="completion">Completion of the row, its format a known one.</param>
    /// <param name="claim">Claim name of the row.</param>
    /// <param name="address">Address of the completion relative to the catalog.</param>
    /// <returns>Messages of the broken rules. A value is never quoted: only its position is named.</returns>
    private static IEnumerable<string> OptionViolations(ClaimCompletionDefinition completion, string claim, string address)
    {
        // A catalog read from a store record may carry no list at all; that is an empty one.
        var options = completion.Options ?? [];
        var optionsAddress = $"{address}:{nameof(ClaimCompletionDefinition.Options)}";

        if (completion.Format is not ClaimFieldFormat.Choice)
        {
            if (options.Count > 0)
            {
                yield return $"{optionsAddress} of '{claim}' is not allowed: only a field of the "
                    + $"{nameof(ClaimCompletionDefinition.Format)} {nameof(ClaimFieldFormat.Choice)} states a list of values.";
            }

            yield break;
        }

        if (options.Count == 0)
        {
            yield return $"{optionsAddress} of '{claim}' is empty: a field of the "
                + $"{nameof(ClaimCompletionDefinition.Format)} {nameof(ClaimFieldFormat.Choice)} must state at least one value.";
            yield break;
        }

        var values = new HashSet<string>(StringComparer.Ordinal);

        for (var k = 0; k < options.Count; k++)
        {
            var option = options[k];
            var optionAddress = $"{optionsAddress}[{k}]";

            if (option is null)
            {
                yield return $"{optionAddress} of '{claim}' is empty.";
                continue;
            }

            if (string.IsNullOrWhiteSpace(option.Value))
            {
                yield return $"{optionAddress}:{nameof(ClaimCompletionOption.Value)} of '{claim}' must not be blank.";
            }
            else if (!string.Equals(option.Value, option.Value.Trim(), StringComparison.Ordinal))
            {
                // A submitted value is trimmed at the edges before it is judged, so a value stated with
                // edge white space could never be chosen.
                yield return $"{optionAddress}:{nameof(ClaimCompletionOption.Value)} of '{claim}' must not start or end with white space.";
            }
            else if (option.Value.Length > ClaimCompletionDefinition.DefaultMaxLength)
            {
                yield return $"{optionAddress}:{nameof(ClaimCompletionOption.Value)} of '{claim}' is longer than "
                    + $"{ClaimCompletionDefinition.DefaultMaxLength} characters.";
            }
            else if (!values.Add(option.Value))
            {
                yield return $"{optionAddress}:{nameof(ClaimCompletionOption.Value)} of '{claim}' repeats a value "
                    + "stated earlier in the list — the values of one field are distinct.";
            }

            if (string.IsNullOrWhiteSpace(option.Label))
            {
                yield return $"{optionAddress}:{nameof(ClaimCompletionOption.Label)} of '{claim}' must not be blank.";
            }
        }
    }

    /// <summary>
    /// Why a claim row may not describe its completion (SPEC-041 C31); null when it may.
    /// </summary>
    /// <param name="row">Claim row stating a completion.</param>
    /// <returns>Reason of the refusal, or null.</returns>
    private static string? CompletionRefusal(ClaimDefinition row)
    {
        if (row.Source is ClaimSource.External)
        {
            return "an External claim is supplied by the integrator's claims mapper after Veriqa.";
        }

        if (OwnCompletionPathClaims.Contains(row.Name))
        {
            return "the phone number and the email have completion paths of their own.";
        }

        if (CoreIssuedClaims.Contains(row.Name))
        {
            return "the core issues this claim itself.";
        }

        return null;
    }

    /// <summary>
    /// Whether a claim has a completion path, given its catalog row.
    /// </summary>
    /// <param name="claim">Claim name.</param>
    /// <param name="row">Catalog row of the claim; null when the catalog does not declare it.</param>
    /// <returns><c>true</c> when the claim has a completion path.</returns>
    private static bool HasCompletionPath(string claim, ClaimDefinition? row) =>
        OwnCompletionPathClaims.Contains(claim) || row?.Completion is not null;

    /// <summary>
    /// Catalog row of a claim; one name has one row, which the rules above enforce.
    /// </summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="claim">Claim name.</param>
    /// <returns>The row; null when the catalog does not declare the claim.</returns>
    public static ClaimDefinition? FindClaim(IReadOnlyList<ScopeDefinition> catalog, string claim) =>
        catalog
            .SelectMany(scope => scope?.Claims ?? Enumerable.Empty<ClaimDefinition>())
            .FirstOrDefault(row => row is not null && string.Equals(row.Name, claim, VeriqaClaimTypes.NameComparison));
}
