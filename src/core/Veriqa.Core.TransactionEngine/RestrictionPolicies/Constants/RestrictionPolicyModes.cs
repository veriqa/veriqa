// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// How the rules of one policy combine (RP-011). Names are matched exactly, letter case included.
/// </summary>
internal static class RestrictionPolicyModes
{
    /// <summary>
    /// Every rule has to pass (logical AND). The default.
    /// </summary>
    public const string AllMustPass = "AllMustPass";

    /// <summary>
    /// One passing rule is enough (logical OR).
    /// </summary>
    public const string AnyMayPass = "AnyMayPass";

    /// <summary>
    /// Every allowed mode name.
    /// </summary>
    public static FrozenSet<string> All { get; } = new[]
    {
        AllMustPass,
        AnyMayPass
    }.ToFrozenSet(StringComparer.Ordinal);
}
