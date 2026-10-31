// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

using OpenIddict.Client;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Providers;

/// <summary>
/// The providers of <c>OpenIddict.Client.WebIntegration</c>, read off the package itself: every
/// <c>Add{Provider}(Action&lt;TBuilder&gt;)</c> of <see cref="OpenIddictClientWebIntegrationBuilder"/>
/// is a provider, and its builder states what the provider accepts. The adapter keeps no list of
/// providers and no code per provider, so a provider the package adds in a later version is available
/// from that version without a change here.
/// </summary>
internal static class WebProviderCatalog
{
    /// <summary>
    /// Prefix of the methods of <see cref="OpenIddictClientWebIntegrationBuilder"/> adding a provider.
    /// </summary>
    private const string AddMethodPrefix = "Add";

    /// <summary>
    /// Prefix of the setters of a provider builder.
    /// </summary>
    private const string SetterPrefix = "Set";

    /// <summary>
    /// What a message names as the settings of a provider that has none of its own.
    /// </summary>
    private const string NoSettings = "none";

    /// <summary>
    /// The setters every provider builder has — the values of the registration itself, each of which the
    /// adapter states from a key of its own (or leaves to OpenIddict) — and the signing key, stated by the
    /// pair of keys <c>SigningKey</c>/<c>SigningKeyId</c>. None of them is reachable through
    /// <c>Settings</c>: a value of a registration has exactly one key. The names are taken from the
    /// builder of one provider: these setters are generated for every provider alike.
    /// </summary>
    private static readonly FrozenSet<string> RegistrationSetterNames = new[]
    {
        nameof(OpenIddictClientWebIntegrationBuilder.Google.SetClientId),
        nameof(OpenIddictClientWebIntegrationBuilder.Google.SetClientSecret),
        nameof(OpenIddictClientWebIntegrationBuilder.Google.SetClientType),
        nameof(OpenIddictClientWebIntegrationBuilder.Google.SetRegistrationId),
        nameof(OpenIddictClientWebIntegrationBuilder.Google.SetProviderName),
        nameof(OpenIddictClientWebIntegrationBuilder.Google.SetProviderDisplayName),
        nameof(OpenIddictClientWebIntegrationBuilder.Google.SetRedirectUri),
        nameof(OpenIddictClientWebIntegrationBuilder.Google.SetPostLogoutRedirectUri),
        nameof(OpenIddictClientWebIntegrationBuilder.Google.SetClaimsIssuer),
        nameof(OpenIddictClientWebIntegrationBuilder.Apple.SetSigningKey)
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The providers of the package, keyed by name without regard to case.
    /// </summary>
    private static readonly FrozenDictionary<string, WebProviderDescriptor> Providers = Discover();

    /// <summary>
    /// Names of every provider of the package, in order, for a message naming what may be stated.
    /// </summary>
    public static string ProviderNames { get; } =
        string.Join(", ", Providers.Values.Select(static provider => provider.Name).Order(StringComparer.Ordinal));

    /// <summary>
    /// Finds a provider by name, without regard to case.
    /// </summary>
    /// <param name="name">Name of the provider as stated in configuration.</param>
    /// <param name="provider">The provider found.</param>
    /// <returns>Whether the package has a provider of that name.</returns>
    public static bool TryFind(string name, [NotNullWhen(true)] out WebProviderDescriptor? provider) =>
        Providers.TryGetValue(name, out provider);

    /// <summary>
    /// Whether a setting name addresses a setter of the registration itself rather than one of the
    /// provider's own.
    /// </summary>
    /// <param name="settingName">Name of the setting, as the member of <c>Settings</c> states it.</param>
    /// <returns>Whether the name is refused in <c>Settings</c>.</returns>
    public static bool IsRegistrationSetting(string settingName) =>
        RegistrationSetterNames.Contains(SetterPrefix + settingName);

    /// <summary>
    /// Finds the setter of a setting of the provider's own.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="settingName">Name of the setting, as the member of <c>Settings</c> states it.</param>
    /// <param name="setter">The <c>Set{Name}(string)</c> of the builder of the provider.</param>
    /// <returns>Whether the provider has such a setting.</returns>
    public static bool TryFindSetter(
        WebProviderDescriptor provider,
        string settingName,
        [NotNullWhen(true)] out MethodInfo? setter) =>
        provider.Setters.TryGetValue(SetterPrefix + settingName, out setter);

    /// <summary>
    /// Names of the settings of the provider's own, in order, for a message naming what may be stated.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The names as the members of <c>Settings</c> state them; <c>none</c> for a provider without any.</returns>
    public static string SettingNames(WebProviderDescriptor provider) =>
        provider.Setters.Count is 0
            ? NoSettings
            : string.Join(
                ", ",
                provider.Setters.Keys
                    .Select(static setter => setter[SetterPrefix.Length..])
                    .Order(StringComparer.Ordinal));

    /// <summary>
    /// Reads the providers off <see cref="OpenIddictClientWebIntegrationBuilder"/>: an add method takes a
    /// single configuration delegate over a builder nested in it, and that builder exposes the client
    /// registration it builds.
    /// </summary>
    /// <returns>The providers keyed by name without regard to case.</returns>
    private static FrozenDictionary<string, WebProviderDescriptor> Discover()
    {
        var providers = new Dictionary<string, WebProviderDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var method in typeof(OpenIddictClientWebIntegrationBuilder).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!method.Name.StartsWith(AddMethodPrefix, StringComparison.Ordinal)
                || method.ReturnType != typeof(OpenIddictClientWebIntegrationBuilder))
            {
                continue;
            }

            var parameters = method.GetParameters();

            if (parameters.Length is not 1
                || !parameters[0].ParameterType.IsGenericType
                || parameters[0].ParameterType.GetGenericTypeDefinition() != typeof(Action<>))
            {
                continue;
            }

            var builderType = parameters[0].ParameterType.GetGenericArguments()[0];

            if (builderType.DeclaringType != typeof(OpenIddictClientWebIntegrationBuilder))
            {
                continue;
            }

            var registrationProperty = builderType.GetProperty(
                nameof(OpenIddictClientWebIntegrationBuilder.Google.Registration),
                typeof(OpenIddictClientRegistration));

            if (registrationProperty is null)
            {
                continue;
            }

            var name = method.Name[AddMethodPrefix.Length..];

            providers[name] = new WebProviderDescriptor
            {
                Name = name,
                AddMethod = method,
                BuilderType = builderType,
                RegistrationProperty = registrationProperty,
                Setters = OwnSetters(builderType),
                SigningKeySetter = builderType.GetMethod(
                    nameof(OpenIddictClientWebIntegrationBuilder.Apple.SetSigningKey),
                    new[] { typeof(ECDsaSecurityKey) })
            };
        }

        return providers.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The setters of a builder that take one text and are the provider's own — not a setter of the
    /// registration itself.
    /// </summary>
    /// <param name="builderType">Builder of a provider.</param>
    /// <returns>The setters keyed by method name without regard to case.</returns>
    private static FrozenDictionary<string, MethodInfo> OwnSetters(Type builderType)
    {
        var setters = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var method in builderType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!method.Name.StartsWith(SetterPrefix, StringComparison.Ordinal)
                || RegistrationSetterNames.Contains(method.Name))
            {
                continue;
            }

            var parameters = method.GetParameters();

            if (parameters.Length is 1 && parameters[0].ParameterType == typeof(string))
            {
                setters[method.Name] = method;
            }
        }

        return setters.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
