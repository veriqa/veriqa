// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// One restriction policy: a flat set of rules over the claims of the confirming sender and the mode
/// the rules combine in (SPEC-018 §5.1). It is both the shape the configuration binds to and the
/// policy an element of <see cref="RestrictionPolicySet"/> carries.
/// </summary>
/// <remarks>
/// The strings stay strings because the configuration states them as strings; every allowed value
/// exists as a constant (RP-012). A policy that is disabled or has no rules restricts nothing
/// (RP-013), which is what a default-constructed instance is.
/// </remarks>
internal sealed class ChannelRestrictionPolicyOptions
{
    /// <summary>
    /// Whether the policy is in force. Off by default: a deployment that states nothing behaves as it
    /// did before the policy existed (RP-080).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How the rules combine — one of <see cref="RestrictionPolicyModes"/>.
    /// </summary>
    public string Mode { get; set; } = RestrictionPolicyModes.AllMustPass;

    /// <summary>
    /// The rules, in the order they are evaluated.
    /// </summary>
    public IReadOnlyList<RestrictionRuleOptions> Rules { get; set; } = [];
}
