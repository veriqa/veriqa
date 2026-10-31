// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Validator of the root Veriqa configuration (SPEC-012 §8.1).
/// Performs critical checks at application startup via ValidateOnStart.
/// <para>
/// The type is internal: it is consumed only through the public IValidateOptions&lt;VeriqaOptions&gt;
/// contract it implements, and it is registered by AddVeriqaConfiguration, which is internal to this
/// assembly. That registration constructs the type itself rather than letting the container activate
/// it: the configuration instance the validator judges is the one the integrator handed us, not
/// whatever the container resolves for IConfiguration.
/// </para>
/// </summary>
internal sealed class VeriqaOptionsValidator : IValidateOptions<VeriqaOptions>
{
    /// <summary>
    /// Address of the declared scopes inside the host configuration.
    /// </summary>
    private static readonly string ScopesPath =
        ScopesClaimsOptions.SectionName + ":" + nameof(ScopesClaimsOptions.Scopes);

    /// <summary>
    /// Address of the client entries inside the host configuration.
    /// </summary>
    private static readonly string ClientsPath =
        OidcClientsOptions.SectionName + ":" + nameof(OidcClientsOptions.Clients);

    /// <summary>
    /// Member a scope or a claim row stated before the requirement levels existed: on a claim it was
    /// replaced by <see cref="ClaimDefinition.Level"/>, on a scope it was removed. The binder ignores a
    /// member the class does not have, so without a check of the text the requirement a deployment
    /// wrote would silently not exist.
    /// </summary>
    private const string RemovedIsRequiredMember = "IsRequired";

    /// <summary>
    /// Process-wide registry of the auth-window languages.
    /// </summary>
    private readonly IAuthPageLanguageRegistry _languageRegistry;

    /// <summary>
    /// Host configuration — read for the ONE question the bound object can no longer answer: what the
    /// operator actually wrote where the binder dropped or ignored it.
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Creates the root configuration validator.
    /// </summary>
    /// <param name="languageRegistry">Registry of the auth-window languages.</param>
    /// <param name="configuration">Host configuration the options are bound from.</param>
    public VeriqaOptionsValidator(IAuthPageLanguageRegistry languageRegistry, IConfiguration configuration)
    {
        _languageRegistry = languageRegistry;
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>
    /// Validates the root Veriqa configuration.
    /// </summary>
    /// <param name="name">Options instance name.</param>
    /// <param name="options">Options instance to validate.</param>
    /// <returns>Validation result.</returns>
    public ValidateOptionsResult Validate(string? name, VeriqaOptions options)
    {
        // The method performs critical configuration checks

        var failures = new List<string>();

        // The core catalog is judged by the same rules as the catalog of any level (SPEC-041 C31, L92)
        failures.AddRange(
            ScopesClaimsCatalogRules.Violations(options.ScopesClaims.Scopes)
                .Select(violation => ScopesClaimsOptions.SectionName + ":" + violation));

        // What the binder cannot convert or silently ignores is judged on the TEXT the deployment
        // wrote (SPEC-012 §8.2)
        failures.AddRange(StatedCatalogFailures());
        failures.AddRange(StatedClientClaimLevelFailures());

        // Default language validation: must be in the supported languages registry
        ValidateLocalization(options.Localization, failures);

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
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
                    UnknownStatedMember<ClaimSource>(
                        claim.GetSection(nameof(ClaimDefinition.Source)),
                        $"{claimAddress}:{nameof(ClaimDefinition.Source)}"),
                    UnknownStatedMember<ClaimRequirementLevel>(
                        claim.GetSection(nameof(ClaimDefinition.Level)),
                        $"{claimAddress}:{nameof(ClaimDefinition.Level)}"),
                    UnknownStatedMember<ClaimFieldFormat>(
                        completion.GetSection(nameof(ClaimCompletionDefinition.Format)),
                        $"{completionAddress}:{nameof(ClaimCompletionDefinition.Format)}"),
                    UnparsableStatedInteger(
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

    /// <summary>
    /// Requirement levels the client entries STATE that the enumeration does not carry. An entry of the
    /// map the binder cannot convert is dropped from it, so the raise the application wrote would
    /// silently not apply — the same reason the catalog text is judged.
    /// </summary>
    /// <returns>One message per stated level that is not a member of the closed set.</returns>
    private IEnumerable<string> StatedClientClaimLevelFailures()
    {
        foreach (var client in _configuration.GetSection(ClientsPath).GetChildren())
        {
            foreach (var level in client.GetSection(nameof(OidcClientOptions.ClaimLevels)).GetChildren())
            {
                var address = $"{ClientsPath}[{client.Key}]:{nameof(OidcClientOptions.ClaimLevels)}:{level.Key}";

                if (UnknownStatedMember<ClaimRequirementLevel>(level, address) is { } failure)
                {
                    yield return failure;
                }
            }
        }
    }

    /// <summary>
    /// Failure of a stated value that names no member of a closed set; null when the member states no
    /// value or names a member.
    /// </summary>
    /// <typeparam name="TEnum">Closed set the value must name a member of.</typeparam>
    /// <param name="member">Configuration member holding the value.</param>
    /// <param name="address">Address of the member for the message.</param>
    /// <returns>Failure message, or null.</returns>
    private static string? UnknownStatedMember<TEnum>(IConfigurationSection member, string address)
        where TEnum : struct, Enum
    {
        var stated = member.Value;

        if (string.IsNullOrWhiteSpace(stated)
            || (Enum.TryParse<TEnum>(stated, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)))
        {
            return null;
        }

        return $"{address} is '{stated}', which is not a known value. "
            + $"Allowed values: {string.Join(", ", Enum.GetNames<TEnum>())}.";
    }

    /// <summary>
    /// Failure of a stated value that is not an integer; null when the member states no value or an
    /// integer.
    /// </summary>
    /// <param name="member">Configuration member holding the value.</param>
    /// <param name="address">Address of the member for the message.</param>
    /// <returns>Failure message, or null.</returns>
    private static string? UnparsableStatedInteger(IConfigurationSection member, string address)
    {
        var stated = member.Value;

        if (string.IsNullOrWhiteSpace(stated)
            || int.TryParse(stated, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return $"{address} is '{stated}', which is not an integer.";
    }

    /// <summary>
    /// Checks that the default language is in the data-driven supported languages registry
    /// (SPEC-007 UI-080). The registry is derived from the locale files actually shipped by the
    /// host, so a language claimed in the configuration but not backed by data fails the start
    /// (fail-fast, SPEC-012 §8.1) instead of silently rendering an English page.
    /// The pseudo-locale is excluded: it is a layout test locale, not a configurable default.
    /// </summary>
    /// <param name="localization">Localization settings.</param>
    /// <param name="failures">Failure list to append to.</param>
    private void ValidateLocalization(LocalizationOptions localization, List<string> failures)
    {
        // The method checks that the default language code is valid
        // Normalization (Localization ??= new) is done in VeriqaOptionsPostConfigure

        var configurableLanguages = _languageRegistry.ConfigurableLanguages;

        // Ordinal membership, the configured value is not normalized — an unexpected casing ("RU")
        // is a configuration defect and is reported as such, not silently accepted.
        if (localization.DefaultLanguage is null
            || !configurableLanguages.Contains(localization.DefaultLanguage))
        {
            var supported = string.Join(", ", configurableLanguages.Order(StringComparer.Ordinal));
            failures.Add(
                $"Veriqa:Localization:DefaultLanguage contains an unsupported language " +
                $"'{localization.DefaultLanguage}'. Allowed values: {supported}.");
        }
    }
}
