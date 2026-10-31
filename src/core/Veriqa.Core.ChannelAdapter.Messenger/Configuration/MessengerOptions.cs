// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Messenger.Constants;

namespace Veriqa.Core.ChannelAdapter.Messenger.Configuration;

/// <summary>
/// Messenger adapter settings (SPEC-003 §17.1).
/// </summary>
public sealed class MessengerOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:Channels:Messenger";

    /// <summary>
    /// Default Graph API base URL — the host of the Messenger Platform Send API.
    /// </summary>
    public const string DefaultGraphApiBaseUrl = "https://graph.facebook.com";

    /// <summary>
    /// Default Graph API version.
    /// </summary>
    public const string DefaultGraphApiVersion = "v26.0";

    /// <summary>
    /// Whether the Messenger adapter is active.
    /// Disabled by default so that a missing configuration section does not enable the channel implicitly.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Identifier of the Facebook Page that sends the messages.
    /// </summary>
    public string PageId { get; set; } = string.Empty;

    /// <summary>
    /// Username of the Facebook Page, as it stands in an <c>m.me</c> link.
    /// A leading <c>@</c> is dropped. Mandatory unless <see cref="DeepLinkByPageId"/> is switched on.
    /// </summary>
    public string PageName
    {
        get => _pageName;
        set => _pageName = string.IsNullOrEmpty(value)
            ? value
            : value.Trim().TrimStart(MessengerAdapterConstants.PageNamePrefix);
    }

    // Backing field for PageName, holding the value without the leading at-sign
    private string _pageName = string.Empty;

    /// <summary>
    /// Whether the sign-in link names the Page by <see cref="PageId"/> (<c>m.me/&lt;PageId&gt;</c>)
    /// instead of <see cref="PageName"/>. Default: <c>false</c> — the link by the username, the
    /// form Meta documents. The core level of <see cref="MessengerConfigKeys.DeepLinkByPageId"/>.
    /// </summary>
    public bool DeepLinkByPageId { get; set; }

    /// <summary>
    /// Page access token of the Graph API calls.
    /// </summary>
    public string PageAccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Meta app secret for webhook signature verification.
    /// </summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>
    /// Verify token for the webhook <c>GET</c> handshake.
    /// </summary>
    public string WebhookVerifyToken { get; set; } = string.Empty;

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
    public MessengerTenantCredentials GetTenantCredentials() =>
        new(PageId, PageName, PageAccessToken, AppSecret, WebhookVerifyToken);
}
