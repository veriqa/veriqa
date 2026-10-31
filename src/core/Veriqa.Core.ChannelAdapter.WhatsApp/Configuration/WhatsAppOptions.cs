// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.WhatsApp.Constants;

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Configuration;

/// <summary>
/// WhatsApp adapter settings (SPEC-003 §10, §17.1).
/// There is a single channel adapter, but the delivery providers may differ.
/// </summary>
public sealed class WhatsAppOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:Channels:WhatsApp";

    /// <summary>
    /// Whether the WhatsApp adapter is active.
    /// Disabled by default so that a missing section does not enable the channel implicitly.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Code of the expected message delivery provider. Declares which provider the installation expects;
    /// a value that does not match the code of the registered provider stops the host at startup.
    /// Default — Meta Cloud API (the official path).
    /// </summary>
    public string Provider { get; set; } = WhatsAppProviderNames.MetaCloudApi;

    /// <summary>
    /// WhatsApp business number for the deep link (E.164 format, a leading <c>+</c> is allowed).
    /// Shared by all providers — used for deep link generation.
    /// </summary>
    public string BusinessPhoneNumber { get; set; } = string.Empty;

    /// <summary>
    /// Meta Cloud API provider settings.
    /// Filled when <see cref="Provider"/> is <see cref="WhatsAppProviderNames.MetaCloudApi"/>.
    /// </summary>
    public MetaCloudApiOptions MetaCloudApi { get; set; } = new();

    /// <summary>
    /// Twilio provider settings.
    /// Filled when <see cref="Provider"/> is <see cref="WhatsAppProviderNames.Twilio"/>.
    /// </summary>
    public TwilioOptions Twilio { get; set; } = new();

    // --- Backward compatibility: flat properties duplicating MetaCloudApi ---
    // Keep the existing configuration working without changes.

    /// <summary>
    /// WhatsApp Business phone number identifier (Phone Number ID).
    /// Backward compatibility — using <see cref="MetaCloudApi"/> is recommended.
    /// </summary>
    public string PhoneNumberId
    {
        get => MetaCloudApi.PhoneNumberId;
        set => MetaCloudApi.PhoneNumberId = value;
    }

    /// <summary>
    /// WhatsApp Cloud API access token.
    /// Backward compatibility — using <see cref="MetaCloudApi"/> is recommended.
    /// </summary>
    public string AccessToken
    {
        get => MetaCloudApi.AccessToken;
        set => MetaCloudApi.AccessToken = value;
    }

    /// <summary>
    /// Meta app secret for webhook signature verification.
    /// Backward compatibility — using <see cref="MetaCloudApi"/> is recommended.
    /// </summary>
    public string AppSecret
    {
        get => MetaCloudApi.AppSecret;
        set => MetaCloudApi.AppSecret = value;
    }

    /// <summary>
    /// Verify Token for the Hub Challenge webhook verification.
    /// Backward compatibility — using <see cref="MetaCloudApi"/> is recommended.
    /// </summary>
    public string WebhookVerifyToken
    {
        get => MetaCloudApi.WebhookVerifyToken;
        set => MetaCloudApi.WebhookVerifyToken = value;
    }

    /// <summary>
    /// Graph API base URL.
    /// Backward compatibility — using <see cref="MetaCloudApi"/> is recommended.
    /// </summary>
    public string GraphApiBaseUrl
    {
        get => MetaCloudApi.GraphApiBaseUrl;
        set => MetaCloudApi.GraphApiBaseUrl = value;
    }

    /// <summary>
    /// Graph API version.
    /// Backward compatibility — using <see cref="MetaCloudApi"/> is recommended.
    /// </summary>
    public string GraphApiVersion
    {
        get => MetaCloudApi.GraphApiVersion;
        set => MetaCloudApi.GraphApiVersion = value;
    }

    /// <summary>
    /// Returns the tenant-credential group (tenant level, CFG-202) — derived from the flat properties.
    /// WhatsApp has no core-transport group (the channel is webhook-only, §17.3 edge case).
    /// In self-hosted (N=1) — the credentials of the single implicit tenant from the global IOptions.
    /// </summary>
    /// <returns>Tenant-credential settings group.</returns>
    public WhatsAppTenantCredentials GetTenantCredentials() =>
        new(Provider, BusinessPhoneNumber, MetaCloudApi, Twilio);
}
