// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Configuration;

/// <summary>
/// Twilio provider settings for WhatsApp (the Twilio Messages API). The sender number is the channel's
/// <c>BusinessPhoneNumber</c>, shared by all providers.
/// </summary>
public sealed class TwilioOptions
{
    /// <summary>
    /// Default Twilio REST API base URL.
    /// </summary>
    public const string DefaultApiBaseUrl = "https://api.twilio.com";

    /// <summary>
    /// Twilio account identifier (<c>AC…</c>).
    /// </summary>
    public string AccountSid { get; set; } = string.Empty;

    /// <summary>
    /// Twilio account Auth Token (secret): authenticates the REST calls and signs the inbound webhooks.
    /// </summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>
    /// Identifier (<c>HX…</c>) of the <c>twilio/quick-reply</c> content of the confirmation prompt. The content
    /// takes the variables <c>1</c> — the body, <c>2</c>/<c>3</c> — the title and the id of the confirm button,
    /// <c>4</c>/<c>5</c> — the title and the id of the decline button.
    /// </summary>
    public string QuickReplyContentSid { get; set; } = string.Empty;

    /// <summary>
    /// Public webhook URL exactly as it is set in the Twilio console: the inbound signature is computed by
    /// Twilio over this string, so any difference (scheme, host, port, path, query) fails the check.
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Twilio REST API base URL.
    /// </summary>
    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;
}
