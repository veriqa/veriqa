// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Messenger.Constants;

namespace Veriqa.Core.ChannelAdapter.Messenger.Configuration;

/// <summary>
/// Messenger adapter settings validator.
/// Checks the mandatory fields when the adapter is enabled. A failure names the configuration key,
/// never its value: the app secret and the tokens are secrets.
/// </summary>
internal sealed class MessengerOptionsValidator : IValidateOptions<MessengerOptions>
{
    /// <summary>
    /// Validates the Messenger adapter settings.
    /// </summary>
    /// <param name="name">Options instance name.</param>
    /// <param name="options">Settings to validate.</param>
    /// <returns>Validation result.</returns>
    public ValidateOptionsResult Validate(string? name, MessengerOptions options)
    {
        // If the adapter is disabled, no validation is required
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        // The mandatory fields, in the order the section lists them: the first empty one is named.
        if (string.IsNullOrWhiteSpace(options.PageId))
        {
            return Required(nameof(MessengerOptions.PageId));
        }

        // The username of the Page is what the sign-in link is built from, unless the link is
        // switched to the identifier of the Page — then nothing reads the username.
        if (!options.DeepLinkByPageId && string.IsNullOrWhiteSpace(options.PageName))
        {
            return Required(nameof(MessengerOptions.PageName));
        }

        if (string.IsNullOrWhiteSpace(options.PageAccessToken))
        {
            return Required(nameof(MessengerOptions.PageAccessToken));
        }

        if (string.IsNullOrWhiteSpace(options.AppSecret))
        {
            return Required(nameof(MessengerOptions.AppSecret));
        }

        if (string.IsNullOrWhiteSpace(options.WebhookVerifyToken))
        {
            return Required(nameof(MessengerOptions.WebhookVerifyToken));
        }

        var graphBaseUrlValidation = ValidateAbsoluteHttpsUrl(
            options.GraphApiBaseUrl,
            Key(nameof(MessengerOptions.GraphApiBaseUrl)));
        if (graphBaseUrlValidation.Failed)
        {
            return graphBaseUrlValidation;
        }

        if (string.IsNullOrWhiteSpace(options.GraphApiVersion))
        {
            return Required(nameof(MessengerOptions.GraphApiVersion));
        }

        if (!options.GraphApiVersion.StartsWith(
                MessengerAdapterConstants.GraphApiVersionPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                $"{Key(nameof(MessengerOptions.GraphApiVersion))} must start with the "
                + $"'{MessengerAdapterConstants.GraphApiVersionPrefix}' prefix "
                + $"(for example, {MessengerOptions.DefaultGraphApiVersion}).");
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Failure for a missing mandatory field.
    /// </summary>
    /// <param name="field">Name of the field in <see cref="MessengerOptions"/>.</param>
    /// <returns>Validation failure naming the configuration key.</returns>
    private static ValidateOptionsResult Required(string field) =>
        ValidateOptionsResult.Fail($"{Key(field)} is required when the Messenger adapter is enabled.");

    /// <summary>
    /// Full configuration key of a field (<c>Veriqa:Channels:Messenger:&lt;field&gt;</c>).
    /// </summary>
    /// <param name="field">Name of the field in <see cref="MessengerOptions"/>.</param>
    /// <returns>The configuration key.</returns>
    private static string Key(string field) =>
        $"{MessengerOptions.SectionName}:{field}";

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
