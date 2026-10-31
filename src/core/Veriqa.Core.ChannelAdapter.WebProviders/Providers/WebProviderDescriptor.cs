// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Reflection;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Providers;

/// <summary>
/// One provider of <c>OpenIddict.Client.WebIntegration</c> as the adapter addresses it: the method that
/// adds a registration of the provider, the builder type that method hands out and the members of that
/// builder the configuration of a channel reaches.
/// </summary>
internal sealed class WebProviderDescriptor
{
    /// <summary>
    /// Name of the provider as OpenIddict spells it (<c>Google</c>, <c>VkId</c>…).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>Add{Provider}(Action&lt;TBuilder&gt;)</c> of <c>OpenIddictClientWebIntegrationBuilder</c>.
    /// </summary>
    public required MethodInfo AddMethod { get; init; }

    /// <summary>
    /// The builder of the provider the add method hands to its configuration delegate.
    /// </summary>
    public required Type BuilderType { get; init; }

    /// <summary>
    /// The member of the builder exposing the client registration it builds.
    /// </summary>
    public required PropertyInfo RegistrationProperty { get; init; }

    /// <summary>
    /// The settings of the provider's own: every <c>Set{Name}(string)</c> of the builder other than the
    /// setters of the registration itself, keyed by the method name without regard to case.
    /// </summary>
    public required FrozenDictionary<string, MethodInfo> Setters { get; init; }

    /// <summary>
    /// <c>SetSigningKey(ECDsaSecurityKey)</c> of a provider that authenticates the client by a key of the
    /// integrator; <c>null</c> — the provider takes no such key.
    /// </summary>
    public MethodInfo? SigningKeySetter { get; init; }
}
