// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Instagram.Constants;

namespace Veriqa.Core.ChannelAdapter.Instagram.Configuration;

/// <summary>
/// Instagram adapter settings (SPEC-003 §17.1).
/// </summary>
public sealed class InstagramOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:Channels:Instagram";

    /// <summary>
    /// Default Graph API base URL — the host of the Instagram API with Instagram Login.
    /// </summary>
    public const string DefaultGraphApiBaseUrl = "https://graph.instagram.com";

    /// <summary>
    /// Default Graph API version.
    /// </summary>
    public const string DefaultGraphApiVersion = "v26.0";

    /// <summary>
    /// Whether the Instagram adapter is active.
    /// Disabled by default so that a missing configuration section does not enable the channel implicitly.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Meta app secret for webhook signature verification.
    /// </summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>
    /// Verify token for the webhook <c>GET</c> handshake.
    /// </summary>
    public string WebhookVerifyToken { get; set; } = string.Empty;

    /// <summary>
    /// Access token of the Instagram API calls.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the Instagram professional account that sends the messages.
    /// </summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>
    /// Username of the Instagram professional account, as it stands in an <c>ig.me</c> link.
    /// A leading <c>@</c> is dropped.
    /// </summary>
    public string Username
    {
        get => _username;
        set => _username = string.IsNullOrEmpty(value)
            ? value
            : value.Trim().TrimStart(InstagramAdapterConstants.UsernamePrefix);
    }

    // Backing field for Username, holding the value without the leading at-sign
    private string _username = string.Empty;

    /// <summary>
    /// Graph API base URL. An installation-level setting, not a tenant one.
    /// Default: <see cref="DefaultGraphApiBaseUrl"/>.
    /// </summary>
    public string GraphApiBaseUrl { get; set; } = DefaultGraphApiBaseUrl;

    /// <summary>
    /// Graph API version. An installation-level setting, not a tenant one.
    /// Default: <see cref="DefaultGraphApiVersion"/>.
    /// </summary>
    public string GraphApiVersion { get; set; } = DefaultGraphApiVersion;

    /// <summary>
    /// Returns the tenant-credential group (tenant level, CFG-202) — derived from the flat properties.
    /// In self-hosted (N=1) — the credentials of the single implicit tenant from the global IOptions.
    /// </summary>
    /// <returns>Tenant-credential settings group.</returns>
    public InstagramTenantCredentials GetTenantCredentials() =>
        new(AppSecret, WebhookVerifyToken, AccessToken, AccountId, Username);
}
