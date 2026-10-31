// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// The two settings of the restriction policies (SPEC-018 §9, RP-071): the cross-channel policy and
/// the policy of one channel. They are ordinary keys of the canonical resolver — there is no second
/// precedence mechanism (RP-070) — and they are independent: the channel is a DIMENSION of the second
/// key, not a level and not a key per channel (RP-045).
/// <para>
/// Both keys are protective ceilings (RP-072). The value of a level is the policy it states, marked
/// with the level; "the stricter of two" keeps both in force by concatenation, so a level below can
/// only narrow what the levels above admit, and relaxing takes the gate of the core level.
/// </para>
/// <para>
/// The class is internal on purpose, unlike the other key registries: a public key would carry its
/// value type — the policy model, an internal contract of the engine (RP-102) — onto the public
/// surface. The catalog is registered by the composition of this assembly.
/// </para>
/// <para>
/// The <c>Tenant</c> level is declared as SUPPORTED, not as shipped (RP-074): the core contour
/// registers no reader for it, and a level nobody binds states nothing.
/// </para>
/// </summary>
internal static class RestrictionPolicyConfigKeys
{
    /// <summary>
    /// Identity dimension of the channel key: the policy of WHICH channel. Its values are channel
    /// types as the channel identity carries them.
    /// </summary>
    public const string ChannelDimensionName = ConfigDimensionNames.Channel;

    /// <summary>Canonical name of the cross-channel policy key.</summary>
    public const string CommonKeyName = "RestrictionPolicy.Common";

    /// <summary>Canonical name of the per-channel policy key.</summary>
    public const string ChannelKeyName = "RestrictionPolicy.Channel";

    /// <summary>
    /// Member of the record of a level above the core the cross-channel policy is stated at. Written
    /// as text: the record of an application belongs to the auth server, which references this
    /// assembly and not the other way round.
    /// </summary>
    private const string RecordCommonMember = "RestrictionPolicy";

    /// <summary>
    /// Member of the record of a level above the core holding the policies of single channels.
    /// </summary>
    private const string RecordByChannelMember = "RestrictionPolicyByChannel";

    /// <summary>
    /// Substitution of the channel type in an address.
    /// </summary>
    private const string ChannelSubstitution = ":{" + ChannelDimensionName + "}";

    /// <summary>
    /// Address of the policy of one channel in the host configuration: the entry of the map, not a
    /// member beside a plain field — the key has no value "for every channel".
    /// </summary>
    private const string CoreChannelAddress = RestrictionPoliciesOptions.ByChannelAddress + ChannelSubstitution;

    /// <summary>
    /// Address of the policy of one channel inside the record of a level above the core.
    /// </summary>
    private const string RecordChannelAddress = RecordByChannelMember + ChannelSubstitution;

    /// <summary>
    /// Catalog of the declarations of this owner. It is initialized before the declarations that fill
    /// it, which is the order the initializers of this class run in.
    /// </summary>
    private static readonly ConfigKeyCatalog Declared = new();

    /// <summary>
    /// The cross-channel policy: declares no dimension and is asked with none.
    /// </summary>
    public static ConfigKey<RestrictionPolicySet?> Common { get; } = Declared
        .Of<RestrictionPolicySet?>(CommonKeyName)
        .At(ConfigLevel.Core, RestrictionPoliciesOptions.CommonAddress)
        .At(ConfigLevel.Tenant, RecordCommonMember)
        .At(ConfigLevel.Application, RecordCommonMember)
        .ProtectiveCeiling(static (upper, lower) => RestrictionPolicySet.Concat(upper!, lower!))
        .Parse(static (node, path, _) => ReadLevel(node, path))

        // The hook marks the policy with its level, reads the gate beside it and answers a broken
        // core policy with a refusing value — none of which a walk of the address reproduces: the
        // address holds a policy, while the value of the key is a set of them.
        .NotWalked()
        .Declare();

    /// <summary>
    /// The policy of one channel. The channel is the identity dimension, so the chain has the one
    /// step addressing it: what holds for every channel is the other key's to state.
    /// </summary>
    public static ConfigKey<RestrictionPolicySet?> Channel { get; } = Declared
        .Of<RestrictionPolicySet?>(ChannelKeyName)
        .Identity(ChannelDimensionName)
        .At(ConfigLevel.Core, CoreChannelAddress)
        .At(ConfigLevel.Tenant, RecordChannelAddress)
        .At(ConfigLevel.Application, RecordChannelAddress)
        .ProtectiveCeiling(static (upper, lower) => RestrictionPolicySet.Concat(upper!, lower!))
        .Parse(static (node, path, _) => ReadLevel(node, path))

        // The same reading as the cross-channel key, for the same reason.
        .NotWalked()
        .Declare();

    /// <summary>
    /// Declarations of this owner — what the one registrar of the deployment declares and binds.
    /// </summary>
    public static ConfigKeyCatalog Catalog => Declared;

    /// <summary>
    /// Layer value of one level of either key: the policy the level states at the address, checked
    /// and marked with the level. A level stating no policy states nothing and cedes.
    /// <para>
    /// A policy that cannot be used — the strict read refused it, its form is broken, the gate beside
    /// it cannot be read — is answered differently by the two kinds of level, and the difference is
    /// the guarantee (RP-008). The CORE level never lets the failure out: the boundary that reads a
    /// level would ask this hook again over a record stating nothing, the level would state no policy
    /// at all, and the restriction of the core would be lifted by a typo. It states a REFUSING set
    /// with the gate closed instead. A level ABOVE the core raises: the boundary reports the address
    /// and leaves the step unset, so the record loses its own policy while the policy of the core
    /// stays in force.
    /// </para>
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address the step resolved to.</param>
    /// <returns>Layer value of the level.</returns>
    /// <exception cref="InvalidOperationException">
    /// A level above the core states a policy of a broken form.
    /// </exception>
    private static LayerValue<RestrictionPolicySet?> ReadLevel(ConfigNode node, string path)
    {
        if (node.Level is not ConfigLevel.Core)
        {
            if (ReadPolicy(node, path, out var recordProblems) is not { } recordPolicy)
            {
                return LayerValue<RestrictionPolicySet?>.None;
            }

            if (recordProblems.Count > 0)
            {
                throw new InvalidOperationException(string.Join(' ', recordProblems));
            }

            return LayerValue<RestrictionPolicySet?>.Set(RestrictionPolicySet.Of(node.Level, recordPolicy));
        }

        try
        {
            if (ReadPolicy(node, path, out var problems) is not { } policy)
            {
                return LayerValue<RestrictionPolicySet?>.None;
            }

            if (problems.Count > 0)
            {
                return Refusing();
            }

            // The gate is read on every resolution, next to the policy it governs — a configuration
            // reload reaches the next one. A section that does not state it keeps the gate closed.
            var gateOpen = node.TryRead<bool>(GateAddress(path), out var stated) && stated;

            return LayerValue<RestrictionPolicySet?>.Set(RestrictionPolicySet.Of(ConfigLevel.Core, policy), gateOpen);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return Refusing();
        }
    }

    /// <summary>
    /// Reads the policy a level states at the address and checks its form.
    /// </summary>
    /// <param name="node">Subtree of the level's record.</param>
    /// <param name="path">Address the step resolved to.</param>
    /// <param name="problems">Problems of the form of the policy; empty when there is none or nothing is stated.</param>
    /// <returns>The policy; null when the level states none.</returns>
    private static ChannelRestrictionPolicyOptions? ReadPolicy(
        ConfigNode node,
        string path,
        out IReadOnlyList<string> problems)
    {
        if (!node.TryRead<ChannelRestrictionPolicyOptions>(path, out var policy))
        {
            problems = [];

            return null;
        }

        problems = RestrictionPolicyValidation.Validate(policy, node.ConfigurationKeyOf(path) ?? path);

        return policy;
    }

    /// <summary>
    /// What the core level states where its policy cannot be used: a set refusing every sender, with
    /// the gate closed — a record of an application can add to it and cannot replace it.
    /// </summary>
    /// <returns>Layer value of the core level.</returns>
    private static LayerValue<RestrictionPolicySet?> Refusing() =>
        LayerValue<RestrictionPolicySet?>.Set(
            RestrictionPolicySet.Refusing(ConfigLevel.Core),
            allowLowerOverride: false);

    /// <summary>
    /// Address of the gate, relative to the node the core level is read over. That node stands over
    /// the FIRST segment of the configuration address, and the hook is handed the rest of it — so the
    /// gate is the first segment of the handed path and the gate member. Both keys build it this way,
    /// which is what makes them read ONE gate: the last segment of the address is the policy for one
    /// key and the channel type for the other.
    /// </summary>
    /// <param name="path">Address the step resolved to.</param>
    /// <returns>Address of the gate.</returns>
    private static string GateAddress(string path)
    {
        var separator = path.IndexOf(ConfigNode.PathSeparator);

        return string.Concat(path.AsSpan(0, separator + 1), RestrictionPoliciesOptions.GateMemberName);
    }
}
