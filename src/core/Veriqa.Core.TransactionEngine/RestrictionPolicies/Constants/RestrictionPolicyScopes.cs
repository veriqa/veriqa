// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Which of the two resolved policies refused the sender, as the audit record states it (RP-061a).
/// </summary>
internal static class RestrictionPolicyScopes
{
    /// <summary>
    /// The cross-channel policy.
    /// </summary>
    public const string Common = "common";

    /// <summary>
    /// The policy of the channel the confirmation came through.
    /// </summary>
    public const string Channel = "channel";
}
