// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Startup check of the core claims catalog (SPEC-041 C31, L92; SPEC-012 §8.2): the catalog rules over
/// the bound catalog, and what the binder dropped or ignored judged on the text the deployment wrote.
/// <para>
/// It answers for ONE named instance of <see cref="ScopesClaimsOptions"/> —
/// <see cref="CheckedInstanceName"/>, the instance validated on start — and skips every other name. The
/// default instance is read by the rules of the client entries and by the core level of the catalog key
/// while the client entries are being built; a failure raised there would surface inside every one of
/// those reads instead of once, as the failure of its own section.
/// </para>
/// </summary>
internal sealed class ScopesClaimsCatalogValidator : IValidateOptions<ScopesClaimsOptions>
{
    /// <summary>
    /// Name of the instance of the claims catalog section that is validated on start.
    /// </summary>
    public const string CheckedInstanceName = "Veriqa.ScopesClaims.StartupCheck";

    /// <summary>
    /// Address of the declared scopes inside the host configuration.
    /// </summary>
    private static readonly string ScopesPath =
        ScopesClaimsOptions.SectionName + ":" + nameof(ScopesClaimsOptions.Scopes);

    /// <summary>
    /// Member a scope or a claim row stated before the requirement levels existed: on a claim it was
    /// replaced by <see cref="ClaimDefinition.Level"/>, on a scope it was removed. The binder ignores a
    /// member the class does not have, so without a check of the text the requirement a deployment
    /// wrote would silently not exist.
    /// </summary>
    private const string RemovedIsRequiredMember = "IsRequired";

    /// <summary>
    /// Host configuration — read for the ONE question the bound object can no longer answer: what the
    /// operator actually wrote where the binder dropped or ignored it.
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Creates the startup check of the claims catalog.
    /// </summary>
    /// <param name="configuration">Host configuration the options are bound from.</param>
    public ScopesClaimsCatalogValidator(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// Validates the claims catalog of the checked instance; skips any other instance.
    /// </summary>
    /// <param name="name">Options instance name.</param>
    /// <param name="options">Options instance to validate.</param>
    /// <returns>Validation result.</returns>
    public ValidateOptionsResult Validate(string? name, ScopesClaimsOptions options)
    {
        if (!string.Equals(name, CheckedInstanceName, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = new List<string>();

        // The core catalog is judged by the same rules as the catalog of any level (SPEC-041 C31, L92)
        failures.AddRange(
            ScopesClaimsCatalogRules.Violations(options.Scopes)
                .Select(violation => ScopesClaimsOptions.SectionName + ":" + violation));

        // What the binder cannot convert or silently ignores is judged on the TEXT the deployment
        // wrote (SPEC-012 §8.2)
        failures.AddRange(StatedCatalogFailures());

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Failures of the catalog TEXT the deployment wrote that the bound catalog can no longer show.
    /// </summary>
    /// <remarks>
    /// The question is asked of the configuration TEXT and not of the bound object on purpose: a claim
    /// is an element of a collection, and the binder drops an element it cannot convert — an entry
    /// saying <c>Source: Bogus</c> or <c>Level: Mandatory</c> takes the whole claim out of the scope
    /// before anything of ours sees it, so the deployment silently loses a claim it declared. A raw
    /// NUMBER is the other half of the same mistake: it converts into a member that does not exist,
    /// which is why what the parse produced still has to BE a member. A member the class no longer has
    /// (<c>IsRequired</c>) is the third case: the binder ignores it, and the requirement it stated
    /// would silently not exist. A deployment that configures the options in code states no text here
    /// and nothing is judged.
    /// </remarks>
    /// <returns>One message per failure of the stated catalog.</returns>
    private IEnumerable<string> StatedCatalogFailures()
    {
        foreach (var scope in _configuration.GetSection(ScopesPath).GetChildren())
        {
            var scopeAddress = $"{ScopesPath}[{scope.Key}]";

            if (scope.GetSection(RemovedIsRequiredMember).Exists())
            {
                yield return
                    $"{scopeAddress}:{RemovedIsRequiredMember} was removed: a scope carries no requirement — "
                    + $"state the requirement on its claims ({nameof(ClaimDefinition.Level)}).";
            }

            foreach (var claim in scope.GetSection(nameof(ScopeDefinition.Claims)).GetChildren())
            {
                var claimAddress = $"{scopeAddress}:{nameof(ScopeDefinition.Claims)}[{claim.Key}]";
                var completion = claim.GetSection(nameof(ClaimDefinition.Completion));
                var completionAddress = $"{claimAddress}:{nameof(ClaimDefinition.Completion)}";

                if (claim.GetSection(RemovedIsRequiredMember).Exists())
                {
                    yield return
                        $"{claimAddress}:{RemovedIsRequiredMember} was replaced by {nameof(ClaimDefinition.Level)}: "
                        + $"state one of {string.Join(", ", Enum.GetNames<ClaimRequirementLevel>())} instead.";
                }

                string?[] failures =
                [
                    StatedConfigurationText.UnknownStatedMember<ClaimSource>(
                        claim.GetSection(nameof(ClaimDefinition.Source)),
                        $"{claimAddress}:{nameof(ClaimDefinition.Source)}"),
                    StatedConfigurationText.UnknownStatedMember<ClaimRequirementLevel>(
                        claim.GetSection(nameof(ClaimDefinition.Level)),
                        $"{claimAddress}:{nameof(ClaimDefinition.Level)}"),
                    StatedConfigurationText.UnknownStatedMember<ClaimFieldFormat>(
                        completion.GetSection(nameof(ClaimCompletionDefinition.Format)),
                        $"{completionAddress}:{nameof(ClaimCompletionDefinition.Format)}"),
                    StatedConfigurationText.UnparsableStatedInteger(
                        completion.GetSection(nameof(ClaimCompletionDefinition.MaxLength)),
                        $"{completionAddress}:{nameof(ClaimCompletionDefinition.MaxLength)}")
                ];

                foreach (var failure in failures.OfType<string>())
                {
                    yield return failure;
                }
            }
        }
    }
}
