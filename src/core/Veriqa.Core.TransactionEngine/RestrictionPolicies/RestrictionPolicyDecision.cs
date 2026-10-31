// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Outcome of evaluating a resolved set of restriction policies. It goes to the audit record whole,
/// so it names the rule's field and a generalized reason and never carries a claim value or the text
/// of a rule (RP-061a).
/// </summary>
internal sealed record RestrictionPolicyDecision
{
    /// <summary>
    /// Whether the sender is refused.
    /// </summary>
    public bool IsDenied { get; init; }

    /// <summary>
    /// Generalized reason of the refusal — one of <see cref="RestrictionDenyCategories"/>. Null when
    /// the sender is admitted and when a refusing element refused.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Name of the field of the rule that refused — the name, not the claim. Null when the sender is
    /// admitted and when a refusing element refused.
    /// </summary>
    public string? Field { get; init; }

    /// <summary>
    /// Configuration level of the element that refused, taken off the element itself (RP-075).
    /// Always stated on a refusal.
    /// </summary>
    public ConfigLevel? Level { get; init; }

    /// <summary>
    /// The refusal came from a refusing element — a level whose policy could not be read — and not
    /// from a rule.
    /// </summary>
    public bool IsUnreadablePolicy { get; init; }

    /// <summary>
    /// The sender is admitted.
    /// </summary>
    public static RestrictionPolicyDecision Allowed { get; } = new();
}
