// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.WeChat.Constants;

namespace Veriqa.Core.ChannelAdapter.WeChat.Configuration;

/// <summary>
/// WeChat adapter settings (SPEC-003 §17.1).
/// </summary>
public sealed class WeChatOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:Channels:WeChat";

    /// <summary>
    /// Whether the WeChat adapter is active.
    /// Disabled by default so that a missing configuration section does not enable the channel implicitly.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// AppID of the Official Account.
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// AppSecret of the Official Account; the access token is obtained with it.
    /// </summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>
    /// Token entered in the server configuration of the Official Account; the signature of an
    /// inbound request is computed with it.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Base URL of the Official Account API. An installation-level setting, not a tenant one.
    /// Default: <c>https://api.weixin.qq.com</c>.
    /// </summary>
    public string ApiBaseUrl { get; set; } = WeChatAdapterConstants.DefaultApiBaseUrl;

    /// <summary>
    /// Returns the tenant-credential group (tenant level, CFG-202) — derived from the flat properties.
    /// In self-hosted (N=1) — the credentials of the single implicit tenant from the global IOptions.
    /// </summary>
    /// <returns>Tenant-credential settings group.</returns>
    public WeChatTenantCredentials GetTenantCredentials() =>
        new(AppId, AppSecret, Token);
}
