// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Reads the claim a restriction rule checks off the channel identity snapshot (SPEC-018 §5.2).
/// </summary>
internal interface IRestrictionClaimReader
{
    /// <summary>
    /// Reads the value of a policy field in the one string form rules compare.
    /// </summary>
    /// <param name="identity">Snapshot the channel handed over.</param>
    /// <param name="field">Field name — one of <see cref="RestrictionPolicyFields"/>.</param>
    /// <returns>
    /// The value, or <see langword="null"/> when the claim is absent, empty or not in the format the
    /// adapter contract declares for it (RP-021).
    /// </returns>
    string? Read(ChannelIdentitySnapshot identity, string field);
}
