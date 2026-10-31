// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

namespace Veriqa.Core.ChannelAdapter.WeChat.Configuration;

/// <summary>
/// WeChat adapter settings validator.
/// Checks the mandatory fields when the adapter is enabled. A failure names the configuration key,
/// never its value: the AppSecret and the token are secrets.
/// </summary>
internal sealed class WeChatOptionsValidator : IValidateOptions<WeChatOptions>
{
    /// <summary>
    /// Validates the WeChat adapter settings.
    /// </summary>
    /// <param name="name">Options instance name.</param>
    /// <param name="options">Settings to validate.</param>
    /// <returns>Validation result.</returns>
    public ValidateOptionsResult Validate(string? name, WeChatOptions options)
    {
        // If the adapter is disabled, no validation is required
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        // The mandatory fields, in the order the section lists them: the first empty one is named.
        if (string.IsNullOrWhiteSpace(options.AppId))
        {
            return Required(nameof(WeChatOptions.AppId));
        }

        if (string.IsNullOrWhiteSpace(options.AppSecret))
        {
            return Required(nameof(WeChatOptions.AppSecret));
        }

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            return Required(nameof(WeChatOptions.Token));
        }

        // The base URL may be plain HTTP: besides the platform itself it names a stand-in of the
        // platform inside the installation's own network.
        if (!Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var apiBaseUrl)
            || (apiBaseUrl.Scheme != Uri.UriSchemeHttps && apiBaseUrl.Scheme != Uri.UriSchemeHttp))
        {
            return ValidateOptionsResult.Fail(
                $"{Key(nameof(WeChatOptions.ApiBaseUrl))} must be an absolute HTTP or HTTPS URL.");
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Failure for a missing mandatory field.
    /// </summary>
    /// <param name="field">Name of the field in <see cref="WeChatOptions"/>.</param>
    /// <returns>Validation failure naming the configuration key.</returns>
    private static ValidateOptionsResult Required(string field) =>
        ValidateOptionsResult.Fail($"{Key(field)} is required when the WeChat adapter is enabled.");

    /// <summary>
    /// Full configuration key of a field (<c>Veriqa:Channels:WeChat:&lt;field&gt;</c>).
    /// </summary>
    /// <param name="field">Name of the field in <see cref="WeChatOptions"/>.</param>
    /// <returns>The configuration key.</returns>
    private static string Key(string field) =>
        $"{WeChatOptions.SectionName}:{field}";
}
