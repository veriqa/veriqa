// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Viber.Constants;

namespace Veriqa.Core.ChannelAdapter.Viber.Configuration;

/// <summary>
/// Viber adapter settings (SPEC-003 §17.1).
/// </summary>
public sealed class ViberOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:Channels:Viber";

    /// <summary>
    /// Default Bot API base URL — the host of the Viber REST Bot API.
    /// </summary>
    public const string DefaultApiBaseUrl = "https://chatapi.viber.com";

    /// <summary>
    /// Whether the Viber adapter is active.
    /// Disabled by default so that a missing configuration section does not enable the channel implicitly.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Authentication token of the bot. The same token authorizes the Bot API calls and is the
    /// key the signature of an inbound webhook is verified with.
    /// </summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>
    /// URI of the bot, as it stands in a <c>viber://pa</c> link.
    /// </summary>
    public string BotUri { get; set; } = string.Empty;

    /// <summary>
    /// Sender name shown on the messages of the bot.
    /// </summary>
    public string SenderName { get; set; } = ViberAdapterConstants.DefaultSenderName;

    /// <summary>
    /// Public address of the host (scheme and host) the webhook of the bot is registered under.
    /// An installation-level setting, not a tenant one.
    /// </summary>
    public string WebhookBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Bot API base URL. An installation-level setting, not a tenant one.
    /// Default: <see cref="DefaultApiBaseUrl"/>.
    /// </summary>
    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

    /// <summary>
    /// Returns the tenant-credential group (tenant level, CFG-202) — derived from the flat properties.
    /// In self-hosted (N=1) — the credentials of the single implicit tenant from the global IOptions.
    /// </summary>
    /// <returns>Tenant-credential settings group.</returns>
    public ViberTenantCredentials GetTenantCredentials() =>
        new(AuthToken, BotUri, SenderName);
}
