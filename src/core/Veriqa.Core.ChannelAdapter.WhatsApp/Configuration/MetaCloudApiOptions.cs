// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Configuration;

/// <summary>
/// Meta Cloud API provider settings for WhatsApp.
/// </summary>
public sealed class MetaCloudApiOptions
{
    /// <summary>
    /// Default Graph API base URL.
    /// </summary>
    public const string DefaultGraphApiBaseUrl = "https://graph.facebook.com";

    /// <summary>
    /// Default Graph API version.
    /// </summary>
    public const string DefaultGraphApiVersion = "v26.0";

    /// <summary>
    /// WhatsApp Business phone number identifier (Phone Number ID).
    /// </summary>
    public string PhoneNumberId { get; set; } = string.Empty;

    /// <summary>
    /// WhatsApp Cloud API access token.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Meta app secret for webhook signature verification.
    /// </summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>
    /// Verify Token for the Hub Challenge webhook verification.
    /// </summary>
    public string WebhookVerifyToken { get; set; } = string.Empty;

    /// <summary>
    /// Graph API base URL.
    /// </summary>
    public string GraphApiBaseUrl { get; set; } = DefaultGraphApiBaseUrl;

    /// <summary>
    /// Graph API version.
    /// </summary>
    public string GraphApiVersion { get; set; } = DefaultGraphApiVersion;
}
