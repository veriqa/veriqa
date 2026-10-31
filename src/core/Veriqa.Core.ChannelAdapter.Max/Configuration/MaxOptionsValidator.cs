// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Max.Enums;

namespace Veriqa.Core.ChannelAdapter.Max.Configuration;

/// <summary>
/// MAX adapter settings validator.
/// Checks the mandatory fields when the adapter is enabled.
/// </summary>
public sealed class MaxOptionsValidator : IValidateOptions<MaxOptions>
{
    /// <summary>
    /// Address of the update mode inside the host configuration, as an operator reads it in a refusal.
    /// </summary>
    private static readonly string UpdateModePath =
        MaxOptions.SectionName + ":" + nameof(MaxOptions.UpdateMode);

    /// <summary>
    /// Key of the MAX Bot API address inside the host configuration, as an operator reads it in a refusal.
    /// </summary>
    private static readonly string ApiBaseUrlPath =
        MaxOptions.SectionName + ":" + nameof(MaxOptions.ApiBaseUrl);

    /// <summary>
    /// The only path an address of the MAX Bot API may carry: the root.
    /// </summary>
    private const string RootPath = "/";

    /// <summary>
    /// Validates the MAX adapter settings.
    /// </summary>
    /// <param name="name">Options instance name.</param>
    /// <param name="options">Settings to validate.</param>
    /// <returns>Validation result.</returns>
    public ValidateOptionsResult Validate(string? name, MaxOptions options)
    {
        // If the adapter is disabled, no validation is required
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        // The mode is judged by MEMBERSHIP and not by the binder alone: an unknown NAME never reaches
        // this object — the binder stops the start on it — while a raw NUMBER is converted into a
        // member that does not exist and would otherwise travel on unnamed. Both spellings of the same
        // mistake end the same way (SPEC-012 §8.2).
        if (!Enum.IsDefined(options.UpdateMode))
        {
            return ValidateOptionsResult.Fail(
                $"{UpdateModePath} is '{options.UpdateMode}', which is not a known update mode. "
                + $"Allowed values: {string.Join(", ", Enum.GetNames<MaxUpdateMode>())}.");
        }

        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            return ValidateOptionsResult.Fail(
                "BotToken is required when the MAX adapter is enabled.");
        }

        var apiBaseUrlValidationResult = ValidateApiBaseUrl(options.ApiBaseUrl);
        if (apiBaseUrlValidationResult.Failed)
        {
            return apiBaseUrlValidationResult;
        }

        if (options.UpdateMode is MaxUpdateMode.Webhook)
        {
            if (string.IsNullOrWhiteSpace(options.WebhookBaseUrl))
            {
                return ValidateOptionsResult.Fail(
                    "WebhookBaseUrl is required for the Webhook update mode.");
            }

            var webhookValidationResult = ValidateWebhookBaseUrl(options.WebhookBaseUrl);
            if (webhookValidationResult.Failed)
            {
                return webhookValidationResult;
            }

            // SPEC-003 §11.3: WebhookSecretToken is mandatory in the Webhook mode for security
            if (string.IsNullOrWhiteSpace(options.WebhookSecretToken))
            {
                return ValidateOptionsResult.Fail(
                    "WebhookSecretToken is required for the Webhook update mode; " +
                    "configure it to enable inbound webhook signature validation.");
            }
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Validates the MAX Bot API address: an absolute HTTPS URL made of the scheme, the host and an optional port only.
    /// </summary>
    /// <param name="apiBaseUrl">MAX Bot API address.</param>
    /// <returns>Validation result.</returns>
    private static ValidateOptionsResult ValidateApiBaseUrl(string? apiBaseUrl)
    {
        // Every call of the adapter goes to this address with the bot token in a header, so only an
        // absolute HTTPS address passes the start.
        var httpsResult = ValidateAbsoluteHttpsUrl(apiBaseUrl, ApiBaseUrlPath, out var apiBaseUri);
        if (httpsResult.Failed)
        {
            return httpsResult;
        }

        // The paths of the API methods are absolute: both the platform client and the direct calls drop
        // a path of this address, and a query or a fragment would not reach the platform either. An
        // address like that is refused at the start instead of sending the calls elsewhere unnoticed.
        if (!string.Equals(apiBaseUri.AbsolutePath, RootPath, StringComparison.Ordinal)
            || apiBaseUri.Query.Length > 0
            || apiBaseUri.Fragment.Length > 0)
        {
            return ValidateOptionsResult.Fail(
                $"{ApiBaseUrlPath} must contain only the scheme, the host and an optional port, without a path, query or fragment.");
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Validates the webhook base URL: an absolute URI with the HTTPS scheme.
    /// </summary>
    /// <param name="webhookBaseUrl">Webhook base URL.</param>
    /// <returns>Validation result.</returns>
    private static ValidateOptionsResult ValidateWebhookBaseUrl(string webhookBaseUrl) =>
        ValidateAbsoluteHttpsUrl(webhookBaseUrl, nameof(MaxOptions.WebhookBaseUrl), out _);

    /// <summary>
    /// Checks that a setting holds an absolute URL with the HTTPS scheme; the refusal names the key.
    /// </summary>
    /// <param name="url">Value of the setting.</param>
    /// <param name="key">Key of the setting, as the refusal names it.</param>
    /// <param name="uri">The parsed address when the check passes.</param>
    /// <returns>Validation result.</returns>
    private static ValidateOptionsResult ValidateAbsoluteHttpsUrl(string? url, string key, out Uri uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri))
        {
            uri = null!;
            return ValidateOptionsResult.Fail($"{key} must be a correct absolute URL.");
        }

        uri = parsedUri;

        // The MAX Bot API requires HTTPS
        if (!string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail($"{key} must use HTTPS (a MAX Bot API requirement).");
        }

        return ValidateOptionsResult.Success;
    }
}
