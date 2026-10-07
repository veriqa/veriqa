// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Email.Enums;

namespace Veriqa.Core.ChannelAdapter.Email.Configuration;

/// <summary>
/// Email adapter settings (SPEC-016 §3, §8).
/// Supports independently enabling the Pull and Push modes.
/// </summary>
public sealed class EmailOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:Channels:Email";

    /// <summary>
    /// Whether the Email adapter is active.
    /// Disabled by default so that a missing configuration section does not enable the channel implicitly.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Public base URL for generating magic links and QR codes (EM-053).
    /// Required when the adapter is enabled.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// TTL of the action/correlation token. Defaults to 5 minutes (EM-013).
    /// Must not exceed the transaction TTL.
    /// </summary>
    public TimeSpan TokenTtl { get; set; } = EmailAdapterConfigDefaults.DefaultTokenTtl;

    /// <summary>
    /// The preferred mode that the Email adapter shows the user first.
    /// When both modes (Pull and Push) are enabled it determines which scenario
    /// is offered on the auth page by default. The user can manually switch
    /// to the alternative mode (for example, if the QR code cannot be scanned — fallback to the Pull link).
    /// Default is <see cref="EmailMode.Push"/> (compose flow), as the most convenient
    /// for mobile users. Pull is used as a manual fallback.
    /// </summary>
    public EmailMode PreferredMode { get; set; } = EmailMode.Push;

    /// <summary>
    /// Whether Pull mode (Email Link) is enabled.
    /// Enabled by default — serves as a manual fallback when Push is unavailable.
    /// </summary>
    public bool PullEnabled { get; set; } = true;

    /// <summary>
    /// Whether Push mode (the one-tap email) is enabled.
    /// Disabled by default until the inbound provider and verification policy are configured (EM-051).
    /// Must be explicitly enabled by the administrator together with a correct
    /// <see cref="EmailInboundOptions.VerificationPolicy"/>.
    /// </summary>
    public bool PushEnabled { get; set; } = false;

    /// <summary>
    /// Email address normalization settings.
    /// </summary>
    public EmailNormalizationOptions Normalization { get; set; } = new();

    /// <summary>
    /// Outbound delivery settings for Pull mode.
    /// </summary>
    public EmailOutboundOptions Outbound { get; set; } = new();

    /// <summary>
    /// Inbound processing settings for Push mode.
    /// </summary>
    public EmailInboundOptions Inbound { get; set; } = new();

    /// <summary>
    /// Returns the tenant-credential group (tenant level, CFG-202) — derived from the flat properties.
    /// In self-hosted (N=1) — credentials of the single implicit tenant from the global IOptions.
    /// </summary>
    /// <returns>The tenant-credential settings group.</returns>
    public EmailTenantCredentials GetTenantCredentials() =>
        new(PublicBaseUrl, Outbound, Inbound) { TokenTtl = TokenTtl };
}
