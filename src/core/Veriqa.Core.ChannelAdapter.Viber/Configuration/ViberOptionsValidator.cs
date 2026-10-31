// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Viber.Constants;

namespace Veriqa.Core.ChannelAdapter.Viber.Configuration;

/// <summary>
/// Viber adapter settings validator.
/// Checks the mandatory fields when the adapter is enabled. A failure names the configuration key,
/// never its value: the token of the bot is a secret.
/// </summary>
public sealed class ViberOptionsValidator : IValidateOptions<ViberOptions>
{
    /// <summary>
    /// Validates the Viber adapter settings.
    /// </summary>
    /// <param name="name">Options instance name.</param>
    /// <param name="options">Settings to validate.</param>
    /// <returns>Validation result.</returns>
    public ValidateOptionsResult Validate(string? name, ViberOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // If the adapter is disabled, no validation is required
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        // The mandatory fields, in the order the section lists them: the first empty one is named.
        if (string.IsNullOrWhiteSpace(options.AuthToken))
        {
            return Required(nameof(ViberOptions.AuthToken));
        }

        if (string.IsNullOrWhiteSpace(options.BotUri))
        {
            return Required(nameof(ViberOptions.BotUri));
        }

        if (string.IsNullOrWhiteSpace(options.SenderName))
        {
            return Required(nameof(ViberOptions.SenderName));
        }

        // The platform refuses a message whose sender name is over its limit, so a name that
        // would make every send fail does not pass the start.
        if (options.SenderName.Length > ViberAdapterConstants.SenderNameMaxLength)
        {
            return ValidateOptionsResult.Fail(
                $"{Key(nameof(ViberOptions.SenderName))} must be at most "
                + $"{ViberAdapterConstants.SenderNameMaxLength} characters long.");
        }

        // The platform delivers events to an HTTPS address only.
        if (!Uri.TryCreate(options.WebhookBaseUrl, UriKind.Absolute, out var webhookBaseUri)
            || !string.Equals(webhookBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                $"{Key(nameof(ViberOptions.WebhookBaseUrl))} must be a correct absolute HTTPS URL.");
        }

        if (!Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail(
                $"{Key(nameof(ViberOptions.ApiBaseUrl))} must be a correct absolute URL.");
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Failure for a missing mandatory field.
    /// </summary>
    /// <param name="field">Name of the field in <see cref="ViberOptions"/>.</param>
    /// <returns>Validation failure naming the configuration key.</returns>
    private static ValidateOptionsResult Required(string field) =>
        ValidateOptionsResult.Fail($"{Key(field)} is required when the Viber adapter is enabled.");

    /// <summary>
    /// Full configuration key of a field (<c>Veriqa:Channels:Viber:&lt;field&gt;</c>).
    /// </summary>
    /// <param name="field">Name of the field in <see cref="ViberOptions"/>.</param>
    /// <returns>The configuration key.</returns>
    private static string Key(string field) =>
        $"{ViberOptions.SectionName}:{field}";
}
