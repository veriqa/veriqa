// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Viber.Configuration;

/// <summary>
/// Declares the Viber channel's configuration keys into the schema of the deployment and states how
/// their core level is read (SPEC-012 §10.6). Registered by the channel's own <c>AddViber()</c>,
/// whether or not the channel turns out to be enabled: the schema describes what the deployment HAS,
/// and a key left without a value is named by the startup report instead of being invisible.
/// The core level is the degenerate N=1 case (self-hosted ≡ core): with empty upper layers the
/// resolver returns these values 1:1 with direct <c>IOptions</c> access.
/// </summary>
internal sealed class ViberConfigKeyRegistrar : IRegisterConfigKeys
{
    /// <summary>
    /// Global Viber channel settings.
    /// </summary>
    private readonly IOptionsMonitor<ViberOptions> _options;

    /// <summary>
    /// Creates the registrar of the Viber channel keys.
    /// </summary>
    /// <param name="options">Global Viber channel settings.</param>
    public ViberConfigKeyRegistrar(IOptionsMonitor<ViberOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    /// <remarks>
    /// This registrar declares no key: the keys of the channel are declared by its CATALOG
    /// (<see cref="ViberConfigKeys.Catalog"/>), which <c>AddViber()</c> registers. What is left
    /// here is the other half of the axis — how the core level of those keys is read.
    /// </remarks>
    public void DeclareKeys(IConfigKeyDeclarations keys)
    {
    }

    /// <inheritdoc />
    public void Register(IConfigBindings bindings, IConfigCoreBindings coreBindings)
    {
        // Core-level channel credentials — the tenant-credential group of the global IOptions. The
        // getter is registered with the key's own type: erasing it is the registry's business (one
        // internal Delegate map), not something a consumer of the key has to undo afterwards.
        coreBindings.RegisterCore(
            ViberConfigKeys.Credentials,
            () => _options.CurrentValue.GetTenantCredentials());
    }
}
