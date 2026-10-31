// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Configuration;

/// <summary>
/// Keys of a web provider channel — the subsection <c>WebProvider</c> of a channel section
/// <c>Veriqa:Channels:{channel}</c>. Every key is declared ONCE, with the channel as its identity
/// dimension, by the example of <see cref="ChannelInboundVerificationConfigKeys"/>: the channels are
/// named by the operator in configuration, so there is no closed list of them to declare a key per
/// channel for. Every key is of the core level only.
/// <para>
/// Reading and using the values are two different acts. A registration is read once at start by
/// <see cref="WebProviderRegistrationReader"/> and stays fixed until the host restarts: the OpenIddict
/// client is configured once. The declarations below put the keys into the schema of the deployment
/// and give the core level its address.
/// </para>
/// <para>
/// <c>Enabled</c> and <c>InboundVerification</c> belong to the section of any channel and are not
/// declared here: the first is read by the registration the way every channel reads it, the second is
/// declared by the core and checked by the start-up pass over all channels.
/// </para>
/// </summary>
internal static class WebProviderConfigKeys
{
    /// <summary>
    /// Name of the subsection that makes a channel section a web provider channel.
    /// </summary>
    public const string WebProviderSectionName = "WebProvider";

    /// <summary>
    /// Name of the subsection that makes a channel section an HTTP channel registration. A section
    /// carrying both is refused: one channel is served by one adapter.
    /// </summary>
    public const string HttpSectionName = "Http";

    /// <summary>
    /// Member of any channel section carrying its core-level <c>Enabled</c> flag (SPEC-003 §17.7).
    /// </summary>
    public const string EnabledMember = "Enabled";

    /// <summary>
    /// Member: name of the provider of <c>OpenIddict.Client.WebIntegration</c>.
    /// </summary>
    public const string ProviderMember = "Provider";

    /// <summary>
    /// Member: identifier of the client registered with the provider.
    /// </summary>
    public const string ClientIdMember = "ClientId";

    /// <summary>
    /// Member: secret of the client registered with the provider.
    /// </summary>
    public const string ClientSecretMember = "ClientSecret";

    /// <summary>
    /// Member: scopes requested beyond those the provider registration asks for by default.
    /// </summary>
    public const string ScopesMember = "Scopes";

    /// <summary>
    /// Subsection: settings of the provider's own, one member per setting.
    /// </summary>
    public const string SettingsSectionName = "Settings";

    /// <summary>
    /// Member: PEM-encoded private key the client authenticates itself with at a provider that signs
    /// its client assertion with a key of the integrator (Apple).
    /// </summary>
    public const string SigningKeyMember = "SigningKey";

    /// <summary>
    /// Member: identifier of the key stated by <see cref="SigningKeyMember"/>.
    /// </summary>
    public const string SigningKeyIdMember = "SigningKeyId";

    /// <summary>
    /// Member: absolute <c>https</c> URL the routes of the channel are published under.
    /// </summary>
    public const string PublicBaseUrlMember = "PublicBaseUrl";

    /// <summary>
    /// Member: name of the channel on the sign-in page.
    /// </summary>
    public const string DisplayNameMember = "DisplayName";

    /// <summary>
    /// Member: inner markup of the channel glyph.
    /// </summary>
    public const string IconSvgPathMember = "IconSvgPath";

    /// <summary>
    /// Prefix of the canonical names of the keys.
    /// </summary>
    private const string KeyNamePrefix = "Channels." + WebProviderSectionName + ".";

    /// <summary>
    /// Core-level address of the <c>WebProvider</c> subsection with the channel dimension in it.
    /// </summary>
    private const string CoreWebProviderSectionTemplate =
        ChannelInboundVerificationConfigKeys.ChannelsCoreSectionName
        + ":{" + ConfigDimensionNames.Channel + "}:"
        + WebProviderSectionName + ":";

    /// <summary>
    /// Catalog of the declarations of the web provider channels — what <c>AddWebProviders()</c>
    /// registers. It is initialized before the declarations that fill it, which is the order the
    /// initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// <c>WebProvider:Provider</c> — name of the provider of <c>OpenIddict.Client.WebIntegration</c>
    /// (<c>Google</c>, <c>Apple</c>, <c>Huawei</c>, <c>Yandex</c>, <c>VkId</c>…), compared without regard
    /// to case. Obligatory for every enabled channel; the obligation is enforced by the registration
    /// reader, which is the one place that knows which channels are web provider channels.
    /// </summary>
    public static ConfigKey<string?> Provider { get; } = Core<string?>(ProviderMember).Declare();

    /// <summary>
    /// <c>WebProvider:ClientId</c> — identifier of the client registered with the provider. Obligatory
    /// for every enabled channel.
    /// </summary>
    public static ConfigKey<string?> ClientId { get; } = Core<string?>(ClientIdMember).Declare();

    /// <summary>
    /// <c>WebProvider:ClientSecret</c> — secret of the client registered with the provider; a provider
    /// that authenticates the client by a key of the integrator takes <see cref="SigningKey"/> instead.
    /// </summary>
    public static ConfigKey<string?> ClientSecret { get; } = Core<string?>(ClientSecretMember)
        .Secret()
        .Declare();

    /// <summary>
    /// <c>WebProvider:Scopes</c> — scopes requested beyond those the provider registration of OpenIddict
    /// asks for by default, a configuration array by index. None by default.
    /// </summary>
    public static ConfigKey<IReadOnlyList<string>> Scopes { get; } = Core<IReadOnlyList<string>>(ScopesMember)
        .Default(Array.Empty<string>())
        .Declare();

    /// <summary>
    /// <c>WebProvider:Settings</c> — settings of the provider's own, one member per setting: the member
    /// <c>{Name}</c> is applied as the <c>Set{Name}(string)</c> of the OpenIddict builder of that provider
    /// (<c>TeamId</c>, <c>Prompt</c>, <c>AccessType</c>…). The setters every registration has, and the
    /// signing key, are refused here: each value of a registration has exactly one key.
    /// </summary>
    public static ConfigKey<IReadOnlyDictionary<string, string>> Settings { get; } =
        Core<IReadOnlyDictionary<string, string>>(SettingsSectionName).Declare();

    /// <summary>
    /// <c>WebProvider:SigningKey</c> — PEM-encoded private EC key the client signs its assertion with at
    /// a provider that authenticates the client this way (Apple): obligatory there, refused at any other.
    /// </summary>
    public static ConfigKey<string?> SigningKey { get; } = Core<string?>(SigningKeyMember)
        .Secret()
        .Declare();

    /// <summary>
    /// <c>WebProvider:SigningKeyId</c> — identifier of the key stated by <see cref="SigningKey"/>; stated
    /// and refused together with it.
    /// </summary>
    public static ConfigKey<string?> SigningKeyId { get; } = Core<string?>(SigningKeyIdMember).Declare();

    /// <summary>
    /// <c>WebProvider:PublicBaseUrl</c> — absolute <c>https</c> URL the routes of the channel are
    /// published under: the base of the link of the channel and of the address of return. Obligatory for
    /// every enabled channel.
    /// </summary>
    public static ConfigKey<string?> PublicBaseUrl { get; } = Core<string?>(PublicBaseUrlMember).Declare();

    /// <summary>
    /// <c>WebProvider:DisplayName</c> — name of the channel on the sign-in page; unstated — the display
    /// name of the provider in OpenIddict.
    /// </summary>
    public static ConfigKey<string?> DisplayName { get; } = Core<string?>(DisplayNameMember).Declare();

    /// <summary>
    /// <c>WebProvider:IconSvgPath</c> — inner markup of the channel glyph; it passes the allowlist of the
    /// sign-in window like the glyph of any channel.
    /// </summary>
    public static ConfigKey<string?> IconSvgPath { get; } = Core<string?>(IconSvgPathMember).Declare();

    /// <summary>
    /// Declarations of the web provider channels — what <c>AddWebProviders()</c> registers into the
    /// container, and what the schema of a deployment carries because of it.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;

    /// <summary>
    /// Opens the declaration of a key of the <c>WebProvider</c> subsection addressed at the core level
    /// only, with the channel as its identity dimension.
    /// </summary>
    /// <typeparam name="T">Type of the setting value.</typeparam>
    /// <param name="member">Member of the <c>WebProvider</c> subsection.</param>
    /// <returns>Builder of the declaration.</returns>
    private static ConfigKeyBuilder<T> Core<T>(string member) => Declared
        .Of<T>(KeyNamePrefix + member)
        .Identity(ConfigDimensionNames.Channel)
        .At(ConfigLevel.Core, CoreWebProviderSectionTemplate + member);
}
