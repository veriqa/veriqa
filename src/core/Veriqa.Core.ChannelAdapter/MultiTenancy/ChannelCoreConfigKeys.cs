// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.MultiTenancy;

/// <summary>
/// Registrar of the channel-track Core getter of the <c>channels_enabled</c> capability
/// (TASK-040 SPEC-012 §10.6). The core level is the degenerate N=1 case (self-hosted ≡ core): with
/// empty upper layers the resolver returns this value 1:1 with direct <c>IOptions</c> access.
/// </summary>
/// <remarks>
/// The set is composed of what the registered channels declare about themselves
/// (<see cref="CoreChannelDeclaration"/>) rather than read out of four named channel option types:
/// a channel this deployment never added has no say in the capability, and a channel the product
/// does not ship at all takes part on the same terms as one it does.
/// The credential keys are not bound here either — each channel binds its own, next to the options
/// it reads them from.
/// </remarks>
internal sealed class ChannelCoreConfigKeys : IRegisterConfigKeys
{
    /// <summary>
    /// Core-level facts declared by every registered channel.
    /// </summary>
    private readonly IEnumerable<CoreChannelDeclaration> _channels;

    /// <summary>
    /// Creates the registrar of the channel-track Core getter.
    /// </summary>
    /// <param name="channels">Core-level facts declared by every registered channel.</param>
    public ChannelCoreConfigKeys(IEnumerable<CoreChannelDeclaration> channels)
    {
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
    }

    /// <inheritdoc />
    /// <remarks>
    /// This registrar owns no key: it states HOW a level of somebody's key is read. The keys of the
    /// contour are declared by its CATALOG (<see cref="ChannelConfigKeys.Catalog"/>), which the
    /// configuration half of the contour registers — taken by <c>AddVeriqaChannelAdapters</c> and
    /// <c>AddVeriqaChannelConfiguration</c> alike — and keeping the two steps apart is what lets a key
    /// exist in the schema before anything binds it.
    /// </remarks>
    public void DeclareKeys(IConfigKeyDeclarations keys)
    {
    }

    /// <inheritdoc />
    public void Register(IConfigBindings bindings, IConfigCoreBindings coreBindings)
    {
        // Core-level channels_enabled (N=1): the set of channels with Enabled=true (CA-083 as the
        // degenerate capability case — SPEC-003 §17.7). Global Veriqa:Channels:{Type}:Enabled = source of the set.
        coreBindings.RegisterCore(
            ChannelConfigKeys.ChannelsEnabled,
            BuildCoreChannelsEnabled);
    }

    /// <summary>
    /// Builds the core-level set of available channels from each registered channel's declaration.
    /// </summary>
    /// <returns>Codes of the enabled channels (immutable collection).</returns>
    private IReadOnlyCollection<string> BuildCoreChannelsEnabled()
    {
        // Distinct: a host that ran the same channel's Add* through two separate builders declares
        // the channel twice, and a capability is a set — the same code must not appear twice in it.
        var enabled = _channels
            .Where(channel => channel.IsEnabled)
            .Select(channel => channel.ChannelType)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return enabled.AsReadOnly();
    }
}
