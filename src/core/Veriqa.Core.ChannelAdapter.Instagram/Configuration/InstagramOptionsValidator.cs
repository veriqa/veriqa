// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Instagram.Constants;

namespace Veriqa.Core.ChannelAdapter.Instagram.Configuration;

/// <summary>
/// Instagram adapter settings validator.
/// Checks the mandatory fields when the adapter is enabled. A failure names the configuration key,
/// never its value: the app secret and the tokens are secrets.
/// </summary>
internal sealed class InstagramOptionsValidator : IValidateOptions<InstagramOptions>
{
    /// <summary>
    /// Validates the Instagram adapter settings.
    /// </summary>
    /// <param name="name">Options instance name.</param>
    /// <param name="options">Settings to validate.</param>
    /// <returns>Validation result.</returns>
    public ValidateOptionsResult Validate(string? name, InstagramOptions options)
    {
        // If the adapter is disabled, no validation is required
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        // The mandatory fields, in the order the section lists them: the first empty one is named.
        if (string.IsNullOrWhiteSpace(options.AppSecret))
        {
            return Required(nameof(InstagramOptions.AppSecret));
        }

        if (string.IsNullOrWhiteSpace(options.WebhookVerifyToken))
        {
            return Required(nameof(InstagramOptions.WebhookVerifyToken));
        }

        if (string.IsNullOrWhiteSpace(options.AccessToken))
        {
            return Required(nameof(InstagramOptions.AccessToken));
        }

        if (string.IsNullOrWhiteSpace(options.AccountId))
        {
            return Required(nameof(InstagramOptions.AccountId));
        }

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            return Required(nameof(InstagramOptions.Username));
        }

        var graphBaseUrlValidation = ValidateAbsoluteHttpsUrl(
            options.GraphApiBaseUrl,
            Key(nameof(InstagramOptions.GraphApiBaseUrl)));
        if (graphBaseUrlValidation.Failed)
        {
            return graphBaseUrlValidation;
        }

        if (string.IsNullOrWhiteSpace(options.GraphApiVersion))
        {
            return Required(nameof(InstagramOptions.GraphApiVersion));
        }

        if (!options.GraphApiVersion.StartsWith(
                InstagramAdapterConstants.GraphApiVersionPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                $"{Key(nameof(InstagramOptions.GraphApiVersion))} must start with the "
                + $"'{InstagramAdapterConstants.GraphApiVersionPrefix}' prefix "
                + $"(for example, {InstagramOptions.DefaultGraphApiVersion}).");
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Failure for a missing mandatory field.
    /// </summary>
    /// <param name="field">Name of the field in <see cref="InstagramOptions"/>.</param>
    /// <returns>Validation failure naming the configuration key.</returns>
    private static ValidateOptionsResult Required(string field) =>
        ValidateOptionsResult.Fail($"{Key(field)} is required when the Instagram adapter is enabled.");

    /// <summary>
    /// Full configuration key of a field (<c>Veriqa:Channels:Instagram:&lt;field&gt;</c>).
    /// </summary>
    /// <param name="field">Name of the field in <see cref="InstagramOptions"/>.</param>
    /// <returns>The configuration key.</returns>
    private static string Key(string field) =>
        $"{InstagramOptions.SectionName}:{field}";

    /// <summary>
    /// Validates the URL of the external API: absolute and HTTPS.
    /// </summary>
    /// <param name="url">URL to validate.</param>
    /// <param name="keyName">Name of the setting the failure message names.</param>
    /// <returns>Validation result.</returns>
    private static ValidateOptionsResult ValidateAbsoluteHttpsUrl(string url, string keyName)
    {
        // Check that the URL is absolute.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return ValidateOptionsResult.Fail(
                $"{keyName} must be a correct absolute URL.");
        }

        // Only HTTPS is used for the external API.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                $"{keyName} must use HTTPS.");
        }

        return ValidateOptionsResult.Success;
    }
}
