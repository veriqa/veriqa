// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Operators of a restriction rule (SPEC-018 §5.3). The set is closed (RP-030); names are matched
/// exactly, letter case included.
/// </summary>
internal static class RestrictionOperators
{
    /// <summary>
    /// The claim is one of the rule's values (allow-list).
    /// </summary>
    public const string InList = "InList";

    /// <summary>
    /// The claim is none of the rule's values (deny-list).
    /// </summary>
    public const string NotInList = "NotInList";

    /// <summary>
    /// The claim equals the rule's single value. The name is the one the configuration is written
    /// with, hence the deliberate hiding of the inherited static member.
    /// </summary>
    public new const string Equals = "Equals";

    /// <summary>
    /// The claim differs from the rule's single value.
    /// </summary>
    public const string NotEquals = "NotEquals";

    /// <summary>
    /// The claim is present; the rule's values and its missing-claim behaviour are not consulted.
    /// </summary>
    public const string IsPresent = "IsPresent";

    /// <summary>
    /// The claim is absent; the rule's values and its missing-claim behaviour are not consulted.
    /// </summary>
    public const string IsAbsent = "IsAbsent";

    /// <summary>
    /// Every allowed operator name.
    /// </summary>
    public static FrozenSet<string> All { get; } = new[]
    {
        InList,
        NotInList,
        Equals,
        NotEquals,
        IsPresent,
        IsAbsent
    }.ToFrozenSet(StringComparer.Ordinal);
}
