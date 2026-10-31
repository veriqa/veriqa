// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Constants;

/// <summary>
/// Codes of the shipped WhatsApp delivery providers (the same "string code instead of an enum" form as
/// <c>ChannelTypes</c>). The set is open: a provider supplied by the host names itself with its own code,
/// which is matched against the configured <c>Veriqa:Channels:WhatsApp:Provider</c> at startup.
/// </summary>
public static class WhatsAppProviderNames
{
    /// <summary>
    /// Meta Cloud API — the official path, the shipped default.
    /// </summary>
    public const string MetaCloudApi = "MetaCloudApi";

    /// <summary>
    /// Twilio Messages API — the second shipped provider, selected by this code in the configuration.
    /// </summary>
    public const string Twilio = "Twilio";
}
