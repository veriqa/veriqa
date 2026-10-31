// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Email.Enums;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Email.Configuration;

/// <summary>
/// Declares the Email channel's configuration keys into the schema of the deployment and states how
/// their core level is read (SPEC-012 §10.6). Registered by the channel's own <c>AddEmail()</c>,
/// whether or not the channel turns out to be enabled: the schema describes what the deployment HAS,
/// and a key left without a value is named by the startup report instead of being invisible.
/// The core level is the degenerate N=1 case (self-hosted ≡ core): with empty upper layers the
/// resolver returns these values 1:1 with direct <c>IOptions</c> access.
/// </summary>
internal sealed class EmailConfigKeyRegistrar : IRegisterConfigKeys
{
    /// <summary>
    /// Global Email channel settings.
    /// </summary>
    private readonly IOptionsMonitor<EmailOptions> _options;

    /// <summary>
    /// Global Email channel settings as bound at the first read — the source of the sign-in mode ceiling.
    /// </summary>
    private readonly IOptions<EmailOptions> _snapshot;

    /// <summary>
    /// Creates the registrar of the Email channel keys.
    /// </summary>
    /// <param name="options">Global Email channel settings.</param>
    /// <param name="snapshot">Global Email channel settings as bound at the first read.</param>
    public EmailConfigKeyRegistrar(IOptionsMonitor<EmailOptions> options, IOptions<EmailOptions> snapshot)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    /// <inheritdoc />
    /// <remarks>
    /// This registrar declares no key: the keys of the channel are declared by its CATALOG
    /// (<see cref="EmailConfigKeys.Catalog"/>), which <c>AddEmail()</c> registers. What is left here is
    /// the other half of the axis — how the core level of those keys is read.
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
            EmailConfigKeys.Credentials,
            () => _options.CurrentValue.GetTenantCredentials());

        // The ceiling of the sign-in modes — the modes this deployment serves. It is composed of two
        // switches rather than written at an address, which is why the key declares its core level
        // without one and the getter is registered here. The ceiling is read from the IOptions snapshot,
        // as the guards of the Email endpoints read PullEnabled and PushEnabled (SPEC-016 EM-054): a change
        // of the served modes takes a restart of the host, and a reload neither adds nor removes a mode.
        coreBindings.RegisterCore(
            EmailConfigKeys.SignInModes,
            () => SignInModeCeiling(_snapshot.Value));
    }

    /// <summary>
    /// The modes the deployment serves — the ceiling the levels above the core one narrow: Pull when
    /// <see cref="EmailOptions.PullEnabled"/> is on, Push when <see cref="EmailOptions.PushEnabled"/> is.
    /// </summary>
    /// <param name="options">Global Email channel settings.</param>
    /// <returns>The modes of the ceiling; empty when both are switched off.</returns>
    internal static IReadOnlyList<EmailMode> SignInModeCeiling(EmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modes = new List<EmailMode>();

        if (options.PullEnabled)
        {
            modes.Add(EmailMode.Pull);
        }

        if (options.PushEnabled)
        {
            modes.Add(EmailMode.Push);
        }

        return modes;
    }
}
