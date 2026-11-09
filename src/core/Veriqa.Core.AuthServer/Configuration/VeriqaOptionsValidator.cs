// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

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
    /// Address of the client entries inside the host configuration.
    /// </summary>
    private static readonly string ClientsPath =
        OidcClientsOptions.SectionName + ":" + nameof(OidcClientsOptions.Clients);

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

        // What the binder cannot convert or silently ignores is judged on the TEXT the deployment
        // wrote (SPEC-012 §8.2). The claims catalog itself — its rules and its text — is judged by the
        // startup check of its own section (ScopesClaimsCatalogValidator); the levels the client entries
        // state are judged here.
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
    /// Requirement levels the client entries STATE that the enumeration does not carry. An entry of the
    /// map the binder cannot convert is dropped from it, so the raise the application wrote would
    /// silently not apply — the same reason the catalog text is judged by the check of its section.
    /// </summary>
    /// <returns>One message per stated level that is not a member of the closed set.</returns>
    private IEnumerable<string> StatedClientClaimLevelFailures()
    {
        foreach (var client in _configuration.GetSection(ClientsPath).GetChildren())
        {
            foreach (var level in client.GetSection(nameof(OidcClientOptions.ClaimLevels)).GetChildren())
            {
                var address = $"{ClientsPath}[{client.Key}]:{nameof(OidcClientOptions.ClaimLevels)}:{level.Key}";

                if (StatedConfigurationText.UnknownStatedMember<ClaimRequirementLevel>(level, address) is { } failure)
                {
                    yield return failure;
                }
            }
        }
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
