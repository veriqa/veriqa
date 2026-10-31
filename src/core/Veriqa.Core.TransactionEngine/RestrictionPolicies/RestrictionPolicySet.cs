// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// The value of a restriction policy setting: the policies of the configuration levels that stated
/// one, each marked with its level (RP-071).
/// </summary>
/// <remarks>
/// The levels are kept apart rather than merged into one rule set. Each element is evaluated under
/// its own mode and the elements combine by logical AND, so the "stricter of two" of a protective
/// setting is plain concatenation (RP-072): an element added below can only narrow what the elements
/// above admit.
/// </remarks>
internal sealed record RestrictionPolicySet
{
    /// <summary>
    /// Backing field of <see cref="Layers"/> — always a read-only copy.
    /// </summary>
    private readonly IReadOnlyList<RestrictionPolicyLayer> _layers = [];

    /// <summary>
    /// The elements, from the upper owning level down. Copied on assignment: the caller may hand over
    /// a live list, and a later change of it must not reach a resolved value.
    /// </summary>
    public IReadOnlyList<RestrictionPolicyLayer> Layers
    {
        get => _layers;
        init => _layers = Array.AsReadOnly(value.ToArray());
    }

    /// <summary>
    /// A set of the one policy a level stated.
    /// </summary>
    /// <param name="level">Configuration level the policy was read at.</param>
    /// <param name="policy">The policy of the level.</param>
    /// <returns>Set with a single element.</returns>
    public static RestrictionPolicySet Of(ConfigLevel level, ChannelRestrictionPolicyOptions policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return new RestrictionPolicySet { Layers = [new RestrictionPolicyLayer(level, policy)] };
    }

    /// <summary>
    /// A set of one refusing element: the level states a policy that could not be read.
    /// </summary>
    /// <param name="level">Configuration level whose policy could not be read.</param>
    /// <returns>Set that refuses every sender.</returns>
    public static RestrictionPolicySet Refusing(ConfigLevel level) =>
        new() { Layers = [new RestrictionPolicyLayer(level, Policy: null)] };

    /// <summary>
    /// The stricter of two sets: both stay in force, the elements of the upper one first.
    /// </summary>
    /// <param name="upper">Set accumulated from the levels above.</param>
    /// <param name="lower">Set of the level below.</param>
    /// <returns>Set with the elements of <paramref name="upper"/> followed by those of <paramref name="lower"/>.</returns>
    public static RestrictionPolicySet Concat(RestrictionPolicySet upper, RestrictionPolicySet lower)
    {
        ArgumentNullException.ThrowIfNull(upper);
        ArgumentNullException.ThrowIfNull(lower);

        return new RestrictionPolicySet { Layers = [.. upper.Layers, .. lower.Layers] };
    }
}
