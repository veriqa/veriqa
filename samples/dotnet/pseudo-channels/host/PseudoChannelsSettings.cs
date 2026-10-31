// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Sample.PseudoChannels.Host;

/// <summary>
/// Settings of the sample (the <c>PseudoChannels</c> section). Read once at start: which providers
/// are registered as channels is a start-up decision.
/// </summary>
internal sealed class PseudoChannelsSettings
{
    /// <summary>
    /// Configuration section of the sample.
    /// </summary>
    public const string SectionName = "PseudoChannels";

    /// <summary>
    /// Address the phone reaches this host at; the channel link is built on it.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Address this host reaches itself at to post the relay webhook.
    /// </summary>
    public string RelayBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Key the relay webhook is signed with. Empty — every webhook is rejected.
    /// </summary>
    public string RelaySecret { get; set; } = string.Empty;

    /// <summary>
    /// Client the desktop page signs in as against the embedded issuer.
    /// </summary>
    public string DesktopClientId { get; set; } = string.Empty;

    /// <summary>
    /// Google credentials.
    /// </summary>
    public GoogleSettings Google { get; set; } = new();

    /// <summary>
    /// Sign in with Apple credentials.
    /// </summary>
    public AppleSettings Apple { get; set; } = new();
}

/// <summary>
/// Credentials of the OAuth client registered at Google.
/// </summary>
internal sealed class GoogleSettings
{
    /// <summary>
    /// Client identifier. Empty — Google is not registered as a channel.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Client secret.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Whether the provider is switched on.
    /// </summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>
/// Credentials of the Services ID registered at Apple. Apple has no static client secret: the
/// secret is a JWT signed with the private key of the developer account, so the key and the two
/// identifiers that go into the token are configured instead.
/// </summary>
internal sealed class AppleSettings
{
    /// <summary>
    /// Services ID — the client identifier. Empty — Apple is not registered as a channel.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Team ID of the developer account (the issuer of the client secret).
    /// </summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the private key (the <c>kid</c> of the client secret).
    /// </summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>
    /// Private key in PEM form — the content of the downloaded <c>.p8</c> file.
    /// </summary>
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Whether the provider is switched on.
    /// </summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(ClientId);
}
