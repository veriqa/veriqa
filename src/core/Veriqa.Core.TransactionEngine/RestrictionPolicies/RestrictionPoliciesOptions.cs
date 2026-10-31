// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Core level of the restriction policies: the section of the host configuration a deployment states
/// them in (SPEC-018 §9.2). It is bound and validated on its own section; the resolver keys read the
/// very same members by address.
/// </summary>
/// <remarks>
/// The channel is the name of an entry of <see cref="ByChannel"/>, not a member of a channel's own
/// options class: a third-party channel gets a policy by its type alone, and the options of the
/// shipped adapters carry nothing of it (RP-071).
/// </remarks>
internal sealed class RestrictionPoliciesOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:RestrictionPolicies";

    /// <summary>
    /// Member of the section the gate is stated at — the name both resolver keys build the address of
    /// the gate from.
    /// </summary>
    public const string GateMemberName = nameof(AllowApplicationOverride);

    /// <summary>
    /// Address of the cross-channel policy in the host configuration.
    /// </summary>
    public const string CommonAddress = SectionName + AddressSeparator + nameof(Common);

    /// <summary>
    /// Address of the map of the policies of single channels in the host configuration.
    /// </summary>
    public const string ByChannelAddress = SectionName + AddressSeparator + nameof(ByChannel);

    /// <summary>
    /// Separator of the segments of a configuration address.
    /// </summary>
    private const string AddressSeparator = ":";

    /// <summary>
    /// The cross-channel policy. Disabled by default (RP-080).
    /// </summary>
    public ChannelRestrictionPolicyOptions Common { get; set; } = new();

    /// <summary>
    /// Policies of single channels, by channel type. The lookup ignores the case of the type, as the
    /// configuration path addressing the same entry does.
    /// </summary>
    public IDictionary<string, ChannelRestrictionPolicyOptions> ByChannel { get; set; } =
        new Dictionary<string, ChannelRestrictionPolicyOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The gate of both policies (RP-072): whether the policy an application states may REPLACE the
    /// policy of this level instead of only tightening it. Closed by default.
    /// </summary>
    public bool AllowApplicationOverride { get; set; }

    /// <summary>
    /// Address of the policy of one channel in the host configuration: the entry of the map.
    /// </summary>
    /// <param name="channelType">Channel type the entry is named by.</param>
    /// <returns>Address of the entry.</returns>
    public static string ChannelAddress(string channelType) =>
        string.Concat(ByChannelAddress, AddressSeparator, channelType);
}
