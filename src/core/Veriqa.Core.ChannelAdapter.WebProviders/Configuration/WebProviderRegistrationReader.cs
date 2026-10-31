// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.WebProviders.Constants;
using Veriqa.Core.ChannelAdapter.WebProviders.Providers;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Configuration;

/// <summary>
/// The single place the web provider channel sections of the configuration are read and checked. It
/// runs once, at the composition of the host: every violation throws, so a host whose web provider
/// channels are misconfigured stops before it accepts the first request, and the message names the
/// full path of the offending key — or, for a wrong name, the channel.
/// <para>
/// A disabled channel is checked by its name and by the subsections it carries only; the rest of its
/// section is not read, the way the section of a disabled built-in channel is not validated either.
/// What only OpenIddict can judge — the settings a provider requires, such as the team of an Apple
/// client — is judged when the client options are built, at the start of the host
/// (<see cref="WebProviderStartupCheck"/>).
/// </para>
/// </summary>
internal static partial class WebProviderRegistrationReader
{
    /// <summary>
    /// Names of the channels shipped with the product, reserved for them. The set is derived from the
    /// public constants of <see cref="ChannelTypes"/> rather than listed here, so a channel added to that
    /// registry is reserved from the version it ships in without a change here.
    /// </summary>
    private static readonly FrozenSet<string> ReservedChannelTypes = typeof(ChannelTypes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(static field => (string)field.GetRawConstantValue()!)
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Reads every web provider channel section of the configuration and checks it. A channel section is
    /// a web provider channel when it carries the <c>WebProvider</c> subsection; every other section is
    /// left alone.
    /// </summary>
    /// <param name="configuration">Application configuration of the host.</param>
    /// <returns>The names of all web provider channels and the model of the enabled ones.</returns>
    /// <exception cref="InvalidOperationException">A section is misconfigured.</exception>
    public static WebProviderRegistrations Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var channelTypes = new List<string>();
        var enabled = new List<WebProviderRegistration>();

        foreach (var channel in ChannelsSection(configuration).GetChildren())
        {
            var webProvider = channel.GetSection(WebProviderConfigKeys.WebProviderSectionName);

            if (!webProvider.Exists())
            {
                continue;
            }

            var channelType = channel.Key;

            CheckName(channelType, channel.Path);
            CheckSingleAdapter(channel, webProvider);
            channelTypes.Add(channelType);

            if (!ReadEnabled(channel))
            {
                continue;
            }

            enabled.Add(ReadRegistration(channelType, webProvider));
        }

        return new WebProviderRegistrations(channelTypes.AsReadOnly(), enabled.AsReadOnly());
    }

    /// <summary>
    /// Reads the core-level <c>Enabled</c> flag of a channel as it stands NOW — the live half of the
    /// declaration of the channel, followed on every read the way the flag of a built-in channel is.
    /// A flag that stopped being a boolean after the start answers "disabled": a channel whose state
    /// cannot be read is not offered.
    /// </summary>
    /// <param name="configuration">Application configuration of the host.</param>
    /// <param name="channelType">Channel type.</param>
    /// <returns>Whether the channel is enabled at the core level.</returns>
    public static bool IsEnabled(IConfiguration configuration, string channelType)
    {
        var value = ChannelsSection(configuration)
            .GetSection(channelType)[WebProviderConfigKeys.EnabledMember];

        return TryParseEnabled(value, out var isEnabled) && isEnabled;
    }

    /// <summary>
    /// Section <c>Veriqa:Channels</c> the channel sections live under.
    /// </summary>
    /// <param name="configuration">Application configuration of the host.</param>
    /// <returns>The section.</returns>
    private static IConfigurationSection ChannelsSection(IConfiguration configuration) =>
        configuration.GetSection(ChannelInboundVerificationConfigKeys.ChannelsCoreSectionName);

    /// <summary>
    /// Checks the name of a web provider channel: the channel-type format and the reserved names.
    /// </summary>
    /// <param name="channelType">Name of the channel section.</param>
    /// <param name="sectionPath">Full path of the channel section.</param>
    /// <exception cref="InvalidOperationException">The name is malformed or reserved.</exception>
    private static void CheckName(string channelType, string sectionPath)
    {
        if (!ChannelTypeRegex().IsMatch(channelType))
        {
            throw new InvalidOperationException(
                $"Invalid web provider channel name '{channelType}' (section '{sectionPath}'): a channel type must "
                + $"match {CustomChannelConstants.ChannelTypePattern} — a lowercase URL-safe token starting with a "
                + "latin letter, up to 64 characters (letters, digits and '-'). Rename the section.");
        }

        if (ReservedChannelTypes.Contains(channelType))
        {
            throw new InvalidOperationException(
                $"Web provider channel name '{channelType}' (section '{sectionPath}') is reserved: the names of the "
                + $"channels shipped with the product ({string.Join(", ", ReservedChannelTypes.Order(StringComparer.Ordinal))}) "
                + "cannot be taken by a web provider channel, whether or not the shipped channel is added to this "
                + "host. Rename the section.");
        }
    }

    /// <summary>
    /// Refuses a channel section that is both a web provider channel and an HTTP channel registration:
    /// one channel is served by one adapter.
    /// </summary>
    /// <param name="channel">Channel section.</param>
    /// <param name="webProvider">Its <c>WebProvider</c> subsection.</param>
    /// <exception cref="InvalidOperationException">The section also carries the <c>Http</c> subsection.</exception>
    private static void CheckSingleAdapter(IConfigurationSection channel, IConfigurationSection webProvider)
    {
        var http = channel.GetSection(WebProviderConfigKeys.HttpSectionName);

        if (http.Exists())
        {
            throw new InvalidOperationException(
                $"Invalid web provider channel configuration: section '{channel.Path}' carries both '{webProvider.Path}' "
                + $"and '{http.Path}', while a channel is served by one adapter. Keep one of the two subsections, or "
                + "move one of them into a section of its own.");
        }
    }

    /// <summary>
    /// Reads the core-level <c>Enabled</c> flag of a channel at start; unstated means disabled, as for
    /// every built-in channel.
    /// </summary>
    /// <param name="channel">Channel section.</param>
    /// <returns>Whether the channel is enabled.</returns>
    /// <exception cref="InvalidOperationException">The flag is not a boolean.</exception>
    private static bool ReadEnabled(IConfigurationSection channel)
    {
        var (value, path) = ReadValue(channel, WebProviderConfigKeys.EnabledMember);

        if (!TryParseEnabled(value, out var isEnabled))
        {
            throw Invalid(path, "is not a boolean (true or false)");
        }

        return isEnabled;
    }

    /// <summary>
    /// Parses a stated <c>Enabled</c> flag; a blank or absent value states nothing and means disabled.
    /// </summary>
    /// <param name="value">Stated value.</param>
    /// <param name="isEnabled">Parsed flag.</param>
    /// <returns>Whether the value could be read.</returns>
    private static bool TryParseEnabled(string? value, out bool isEnabled)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            isEnabled = false;
            return true;
        }

        return bool.TryParse(value, out isEnabled);
    }

    /// <summary>
    /// Reads and checks the <c>WebProvider</c> subsection of one enabled channel.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <param name="webProvider">The <c>WebProvider</c> subsection.</param>
    /// <returns>The model of the channel.</returns>
    /// <exception cref="InvalidOperationException">The subsection is misconfigured.</exception>
    private static WebProviderRegistration ReadRegistration(string channelType, IConfigurationSection webProvider)
    {
        var provider = ReadProvider(webProvider);

        var (clientId, clientIdPath) = ReadValue(webProvider, WebProviderConfigKeys.ClientIdMember);

        if (clientId is null)
        {
            throw Invalid(
                clientIdPath,
                "is not set: every enabled web provider channel names the client registered with its provider");
        }

        var publicBaseUrl = ReadPublicBaseUrl(webProvider);
        var settings = ReadSettings(webProvider, provider);
        var signingKey = ReadSigningKey(webProvider, provider);

        return new WebProviderRegistration
        {
            ChannelType = channelType,
            Provider = provider,
            ClientId = clientId,
            ClientSecret = ReadText(webProvider, WebProviderConfigKeys.ClientSecretMember),
            Scopes = ReadList(webProvider, WebProviderConfigKeys.ScopesMember),
            Settings = settings,
            SigningKey = signingKey,
            PublicBaseUrl = publicBaseUrl,
            RedirectUri = WebProviderRoutes.CallbackUri(publicBaseUrl, channelType),
            DisplayName = ReadText(webProvider, WebProviderConfigKeys.DisplayNameMember),
            IconSvgPath = ReadText(webProvider, WebProviderConfigKeys.IconSvgPathMember)
        };
    }

    /// <summary>
    /// Reads the provider: stated, and a provider of <c>OpenIddict.Client.WebIntegration</c>, compared
    /// without regard to case.
    /// </summary>
    /// <param name="webProvider">The <c>WebProvider</c> subsection.</param>
    /// <returns>The provider.</returns>
    /// <exception cref="InvalidOperationException">The provider is missing or unknown.</exception>
    private static WebProviderDescriptor ReadProvider(IConfigurationSection webProvider)
    {
        var (name, path) = ReadValue(webProvider, WebProviderConfigKeys.ProviderMember);

        if (name is null)
        {
            throw Invalid(
                path,
                "is not set: every enabled web provider channel names the provider of "
                + "OpenIddict.Client.WebIntegration it signs in with");
        }

        if (!WebProviderCatalog.TryFind(name, out var provider))
        {
            throw Invalid(
                path,
                $"names '{name}', which is no provider of OpenIddict.Client.WebIntegration. The providers are: "
                + WebProviderCatalog.ProviderNames);
        }

        return provider;
    }

    /// <summary>
    /// Reads the public base URL: stated, absolute, <c>https</c>, without a query or a fragment — the
    /// routes of the channel are appended to its path.
    /// </summary>
    /// <param name="webProvider">The <c>WebProvider</c> subsection.</param>
    /// <returns>The base URL.</returns>
    /// <exception cref="InvalidOperationException">The URL is missing, relative, of another scheme, or carries a query or a fragment.</exception>
    private static Uri ReadPublicBaseUrl(IConfigurationSection webProvider)
    {
        var (value, path) = ReadValue(webProvider, WebProviderConfigKeys.PublicBaseUrlMember);

        if (value is null)
        {
            throw Invalid(
                path,
                "is not set: every enabled web provider channel names the public https address its link and its "
                + "address of return are built on");
        }

        // The value itself is not quoted: a URL may carry credentials in its user-info part.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var baseUrl))
        {
            throw Invalid(path, "is not an absolute URI");
        }

        if (!string.Equals(baseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                path,
                $"uses the '{baseUrl.Scheme}' scheme: the address of return a provider sends the user back to is "
                + "https only");
        }

        if (baseUrl.Query.Length > 0 || baseUrl.Fragment.Length > 0)
        {
            throw Invalid(path, "carries a query or a fragment: the routes of the channel are appended to its path");
        }

        return baseUrl;
    }

    /// <summary>
    /// Reads the settings of the provider's own: each member of <c>Settings</c> names a setting the
    /// builder of the provider has, never a value of the registration itself, and states a value.
    /// </summary>
    /// <param name="webProvider">The <c>WebProvider</c> subsection.</param>
    /// <param name="provider">The provider of the channel.</param>
    /// <returns>The settings in the order the configuration states them.</returns>
    /// <exception cref="InvalidOperationException">A setting is unknown to the provider, refused, or empty.</exception>
    private static IReadOnlyList<WebProviderSetting> ReadSettings(
        IConfigurationSection webProvider,
        WebProviderDescriptor provider)
    {
        var section = webProvider.GetSection(WebProviderConfigKeys.SettingsSectionName);

        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            throw Invalid(
                section.Path,
                $"is a single value, not a section: state one member per setting ({section.Path}:{{Name}})");
        }

        var settings = new List<WebProviderSetting>();

        foreach (var member in section.GetChildren())
        {
            var (value, path) = ReadValue(section, member.Key);

            if (WebProviderCatalog.IsRegistrationSetting(member.Key))
            {
                throw Invalid(
                    path,
                    "sets a value of the registration itself, which the provider settings do not take: the client is "
                    + $"stated at '{SiblingPath(webProvider, WebProviderConfigKeys.ClientIdMember)}' and "
                    + $"'{SiblingPath(webProvider, WebProviderConfigKeys.ClientSecretMember)}', a signing key at "
                    + $"'{SiblingPath(webProvider, WebProviderConfigKeys.SigningKeyMember)}' and "
                    + $"'{SiblingPath(webProvider, WebProviderConfigKeys.SigningKeyIdMember)}', and the rest is set "
                    + "by the adapter");
            }

            if (!WebProviderCatalog.TryFindSetter(provider, member.Key, out var setter))
            {
                throw Invalid(
                    path,
                    $"is no setting of the {provider.Name} provider. Its settings are: "
                    + WebProviderCatalog.SettingNames(provider));
            }

            if (value is null)
            {
                throw Invalid(path, "is empty");
            }

            settings.Add(new WebProviderSetting(setter, value));
        }

        return settings.AsReadOnly();
    }

    /// <summary>
    /// Reads the signing key: obligatory, with its key identifier, at a provider that authenticates the
    /// client by a key of the integrator (Apple), and refused at any other. The key is imported here, so
    /// a value that is no PEM-encoded EC key stops the start by its address rather than the first sign-in.
    /// Neither the key nor the reason the import refused it is quoted in a message.
    /// </summary>
    /// <param name="webProvider">The <c>WebProvider</c> subsection.</param>
    /// <param name="provider">The provider of the channel.</param>
    /// <returns>The key, or <c>null</c> for a provider that takes none.</returns>
    /// <exception cref="InvalidOperationException">The key or its identifier is missing, refused, or not an EC key.</exception>
    private static ECDsaSecurityKey? ReadSigningKey(IConfigurationSection webProvider, WebProviderDescriptor provider)
    {
        var (pem, keyPath) = ReadValue(webProvider, WebProviderConfigKeys.SigningKeyMember);
        var (keyId, keyIdPath) = ReadValue(webProvider, WebProviderConfigKeys.SigningKeyIdMember);

        if (provider.SigningKeySetter is null)
        {
            if (pem is not null)
            {
                throw Invalid(keyPath, $"is stated, while the {provider.Name} provider takes no signing key: remove it");
            }

            if (keyId is not null)
            {
                throw Invalid(keyIdPath, $"is stated, while the {provider.Name} provider takes no signing key: remove it");
            }

            return null;
        }

        if (pem is null)
        {
            throw Invalid(
                keyPath,
                $"is not set: the {provider.Name} provider authenticates the client by a key of the integrator — "
                + "state the PEM-encoded private key");
        }

        if (keyId is null)
        {
            throw Invalid(
                keyIdPath,
                $"is not set: the {provider.Name} provider authenticates the client by a key of the integrator — "
                + "state the identifier the provider gave the key");
        }

        var algorithm = ECDsa.Create();

        try
        {
            algorithm.ImportFromPem(pem);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            algorithm.Dispose();

            throw Invalid(keyPath, "is not a PEM-encoded EC private key");
        }

        return new ECDsaSecurityKey(algorithm) { KeyId = keyId };
    }

    /// <summary>
    /// Path of a member of the <c>WebProvider</c> subsection, for a message pointing elsewhere.
    /// </summary>
    /// <param name="webProvider">The <c>WebProvider</c> subsection.</param>
    /// <param name="member">Member of the subsection.</param>
    /// <returns>The full path.</returns>
    private static string SiblingPath(IConfigurationSection webProvider, string member) =>
        ConfigurationPath.Combine(webProvider.Path, member);

    /// <summary>
    /// Reads one optional single value of a section.
    /// </summary>
    /// <param name="parent">Section the value lives in.</param>
    /// <param name="member">Member carrying the value.</param>
    /// <returns>The value, or <c>null</c> when unstated.</returns>
    private static string? ReadText(IConfigurationSection parent, string member) => ReadValue(parent, member).Value;

    /// <summary>
    /// Reads a list stated as a configuration array by index (<c>…:0</c>, <c>…:1</c>; in the
    /// environment <c>…__0</c>). Every entry is a non-blank single value.
    /// </summary>
    /// <param name="parent">Section the list lives in.</param>
    /// <param name="member">Member carrying the list.</param>
    /// <returns>The entries in index order; empty when unstated.</returns>
    /// <exception cref="InvalidOperationException">The member is a single value, or an entry is blank.</exception>
    private static IReadOnlyList<string> ReadList(IConfigurationSection parent, string member)
    {
        var list = parent.GetSection(member);

        if (!string.IsNullOrWhiteSpace(list.Value))
        {
            throw Invalid(list.Path, "is a single value, not a list: state the entries by index (…:0, …:1)");
        }

        var entries = new List<string>();

        foreach (var entry in list.GetChildren())
        {
            var (value, path) = ReadValue(list, entry.Key);

            if (value is null)
            {
                throw Invalid(path, "is empty");
            }

            entries.Add(value);
        }

        return entries.AsReadOnly();
    }

    /// <summary>
    /// Reads one single value of a section. A blank value states nothing.
    /// </summary>
    /// <param name="parent">Section the value lives in.</param>
    /// <param name="member">Member carrying the value.</param>
    /// <returns>The value (or <c>null</c> when unstated) and the full path of the member.</returns>
    /// <exception cref="InvalidOperationException">The member is a section rather than a value.</exception>
    private static (string? Value, string Path) ReadValue(IConfigurationSection parent, string member)
    {
        var node = parent.GetSection(member);

        if (node.Value is null && node.GetChildren().Any())
        {
            throw Invalid(node.Path, "is a section, not a single value");
        }

        return (string.IsNullOrWhiteSpace(node.Value) ? null : node.Value, node.Path);
    }

    /// <summary>
    /// The refusal of a key: the full path of the key and what is wrong with it.
    /// </summary>
    /// <param name="path">Full path of the key.</param>
    /// <param name="problem">What is wrong with the value.</param>
    /// <returns>The exception to throw.</returns>
    private static InvalidOperationException Invalid(string path, string problem) =>
        new($"Invalid web provider channel configuration: '{path}' {problem}.");

    /// <summary>
    /// Compiled matcher of the channel-type format.
    /// </summary>
    [GeneratedRegex(CustomChannelConstants.ChannelTypePattern)]
    private static partial Regex ChannelTypeRegex();
}
