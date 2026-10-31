// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Codes that belong to the restriction policy itself (RP-060): the codes of its audit events and the
/// code of a configuration error. The code the refused operation returns lives with the other codes of
/// the admissibility gate, not here.
/// </summary>
internal static class RestrictionPolicyResultCodes
{
    /// <summary>
    /// Audit event: a rule of the policy refused the sender.
    /// </summary>
    public const string PolicyDenied = "restriction_policy_denied";

    /// <summary>
    /// The policy configuration is invalid. Opens every start-up validation message and is the audit
    /// event of a refusal caused by a policy that could not be read.
    /// </summary>
    public const string Misconfigured = "restriction_policy_misconfigured";

    /// <summary>
    /// Audit event: an external admissibility check refused the sender (RP-103).
    /// </summary>
    public const string ExternalCheckDenied = "admissibility_check_denied";

    /// <summary>
    /// Reason the audit record states for an external check that failed with an exception instead of
    /// answering. The engine's own stand-in: the check returned no code to write.
    /// </summary>
    public const string CheckFailed = "check_failed";

    /// <summary>
    /// Reason the audit record states for an external check that refused without naming a reason, so
    /// that the record never carries an empty field.
    /// </summary>
    public const string Unspecified = "unspecified";
}
