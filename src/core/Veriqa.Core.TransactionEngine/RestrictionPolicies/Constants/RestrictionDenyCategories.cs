// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Generalized reasons a rule refused the sender, as the audit record states them (RP-053, RP-061a).
/// They tell a missing claim from a claim outside the list and never carry the claim itself.
/// </summary>
internal static class RestrictionDenyCategories
{
    /// <summary>
    /// The claim the rule needs is absent.
    /// </summary>
    public const string MissingClaim = "missing_claim";

    /// <summary>
    /// The claim is present and is not among the allowed values.
    /// </summary>
    public const string NotInAllowList = "not_in_allow_list";

    /// <summary>
    /// The claim is present and is among the refused values, or is present where it must not be.
    /// </summary>
    public const string InDenyList = "in_deny_list";
}
