// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// One rule of a restriction policy: a claim field, an operator and the values the operator compares
/// the claim with (SPEC-018 §5.1).
/// </summary>
internal sealed class RestrictionRuleOptions
{
    /// <summary>
    /// The claim field the rule checks — one of <see cref="RestrictionPolicyFields"/>.
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// The operator — one of <see cref="RestrictionOperators"/>.
    /// </summary>
    public string Operator { get; set; } = string.Empty;

    /// <summary>
    /// The values the claim is compared with. Empty for the presence operators.
    /// </summary>
    public IReadOnlyList<string> Values { get; set; } = [];

    /// <summary>
    /// What the rule does when the claim is absent — one of <see cref="RestrictionOnMissing"/>. The
    /// presence operators do not consult it (RP-051).
    /// </summary>
    public string OnMissing { get; set; } = RestrictionOnMissing.Deny;
}
