// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// What a rule does when the claim it checks is absent (SPEC-018 §7). Names are matched exactly,
/// letter case included.
/// </summary>
internal static class RestrictionOnMissing
{
    /// <summary>
    /// The rule fails. The default: missing data must not open the perimeter (RP-052).
    /// </summary>
    public const string Deny = "Deny";

    /// <summary>
    /// The rule passes.
    /// </summary>
    public const string Allow = "Allow";

    /// <summary>
    /// The rule is left out of the evaluation, as if the policy did not state it.
    /// </summary>
    public const string Skip = "Skip";

    /// <summary>
    /// Every allowed value.
    /// </summary>
    public static FrozenSet<string> All { get; } = new[]
    {
        Deny,
        Allow,
        Skip
    }.ToFrozenSet(StringComparer.Ordinal);
}
