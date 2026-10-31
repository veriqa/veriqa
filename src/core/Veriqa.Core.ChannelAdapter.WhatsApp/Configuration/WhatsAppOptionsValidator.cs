// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.WhatsApp.Constants;

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Configuration;

/// <summary>
/// WhatsApp adapter settings validator.
/// Validation depends on the selected provider.
/// </summary>
public sealed class WhatsAppOptionsValidator : IValidateOptions<WhatsAppOptions>
{
    /// <summary>
    /// Name of the Graph API base URL setting in the failure messages.
    /// </summary>
    private const string GraphApiBaseUrlKey = "MetaCloudApi.GraphApiBaseUrl";

    /// <summary>
    /// Validates the WhatsApp adapter settings.
    /// </summary>
    /// <param name="name">Options instance name.</param>
    /// <param name="options">Settings to validate.</param>
    /// <returns>Validation result.</returns>
    public ValidateOptionsResult Validate(string? name, WhatsAppOptions options)
    {
        // If the adapter is disabled, strict validation is not required.
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.BusinessPhoneNumber))
        {
            return ValidateOptionsResult.Fail(
                "BusinessPhoneNumber is required when the WhatsApp adapter is enabled.");
        }

        // Provider-specific validation exists only for the shipped providers: the settings of a provider
        // supplied by the host are unknown here. A value naming no registered provider is rejected at
        // startup by the provider match check, not by this validator.
        if (string.Equals(options.Provider, WhatsAppProviderNames.MetaCloudApi, StringComparison.Ordinal))
        {
            return ValidateMetaCloudApi(options.MetaCloudApi);
        }

        if (string.Equals(options.Provider, WhatsAppProviderNames.Twilio, StringComparison.Ordinal))
        {
            return ValidateTwilio(options.Twilio);
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Validates the Meta Cloud API provider settings.
    /// </summary>
    /// <param name="meta">Meta Cloud API settings.</param>
    /// <returns>Validation result.</returns>
    private static ValidateOptionsResult ValidateMetaCloudApi(MetaCloudApiOptions meta)
    {
        // The method checks the mandatory Meta Cloud API fields.
        if (string.IsNullOrWhiteSpace(meta.PhoneNumberId))
        {
            return ValidateOptionsResult.Fail(
                "MetaCloudApi.PhoneNumberId is required for the MetaCloudApi provider.");
        }

        if (string.IsNullOrWhiteSpace(meta.AccessToken))
        {
            return ValidateOptionsResult.Fail(
                "MetaCloudApi.AccessToken is required for the MetaCloudApi provider.");
        }

        if (string.IsNullOrWhiteSpace(meta.AppSecret))
        {
            return ValidateOptionsResult.Fail(
                "MetaCloudApi.AppSecret is required for the MetaCloudApi provider.");
        }

        if (string.IsNullOrWhiteSpace(meta.WebhookVerifyToken))
        {
            return ValidateOptionsResult.Fail(
                "MetaCloudApi.WebhookVerifyToken is required for the MetaCloudApi provider.");
        }

        var graphBaseUrlValidation = ValidateAbsoluteHttpsUrl(meta.GraphApiBaseUrl, GraphApiBaseUrlKey);
        if (graphBaseUrlValidation.Failed)
        {
            return graphBaseUrlValidation;
        }

        if (string.IsNullOrWhiteSpace(meta.GraphApiVersion))
        {
            return ValidateOptionsResult.Fail(
                "MetaCloudApi.GraphApiVersion is required for the MetaCloudApi provider.");
        }

        if (!meta.GraphApiVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                "MetaCloudApi.GraphApiVersion must start with the 'v' prefix (for example, v26.0).");
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Validates the Twilio provider settings. A failure names the configuration key, never its value:
    /// the Auth Token is a secret, and the account identifier is not for the log either.
    /// </summary>
    /// <param name="twilio">Twilio settings.</param>
    /// <returns>Validation result.</returns>
    private static ValidateOptionsResult ValidateTwilio(TwilioOptions twilio)
    {
        // The method checks the mandatory Twilio fields, then the two URLs by the same absolute-HTTPS rule.
        if (string.IsNullOrWhiteSpace(twilio.AccountSid))
        {
            return RequiredForTwilio(nameof(TwilioOptions.AccountSid));
        }

        if (string.IsNullOrWhiteSpace(twilio.AuthToken))
        {
            return RequiredForTwilio(nameof(TwilioOptions.AuthToken));
        }

        if (string.IsNullOrWhiteSpace(twilio.QuickReplyContentSid))
        {
            return RequiredForTwilio(nameof(TwilioOptions.QuickReplyContentSid));
        }

        if (string.IsNullOrWhiteSpace(twilio.WebhookUrl))
        {
            return RequiredForTwilio(nameof(TwilioOptions.WebhookUrl));
        }

        var webhookUrlValidation = ValidateAbsoluteHttpsUrl(
            twilio.WebhookUrl,
            TwilioKey(nameof(TwilioOptions.WebhookUrl)));
        if (webhookUrlValidation.Failed)
        {
            return webhookUrlValidation;
        }

        return ValidateAbsoluteHttpsUrl(twilio.ApiBaseUrl, TwilioKey(nameof(TwilioOptions.ApiBaseUrl)));
    }

    /// <summary>
    /// Failure for a missing mandatory Twilio field.
    /// </summary>
    /// <param name="field">Name of the field in <see cref="TwilioOptions"/>.</param>
    /// <returns>Validation failure naming the configuration key.</returns>
    private static ValidateOptionsResult RequiredForTwilio(string field) =>
        ValidateOptionsResult.Fail($"{TwilioKey(field)} is required for the Twilio provider.");

    /// <summary>
    /// Full configuration key of a Twilio field (<c>Veriqa:Channels:WhatsApp:Twilio:&lt;field&gt;</c>).
    /// </summary>
    /// <param name="field">Name of the field in <see cref="TwilioOptions"/>.</param>
    /// <returns>The configuration key.</returns>
    private static string TwilioKey(string field) =>
        $"{WhatsAppOptions.SectionName}:{nameof(WhatsAppOptions.Twilio)}:{field}";

    /// <summary>
    /// Validates the URL of an external API or of a public endpoint: absolute and HTTPS.
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
