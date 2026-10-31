// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Evaluates a resolved set of restriction policies against the claims of the confirming sender.
/// </summary>
internal interface IRestrictionPolicyEvaluator
{
    /// <summary>
    /// Evaluates one resolved set. Every element of the set has to admit the sender.
    /// </summary>
    /// <param name="identity">Snapshot the channel handed over.</param>
    /// <param name="policies">The resolved set; null or empty admits the sender (RP-047).</param>
    /// <returns>The decision, with the level of the element that refused.</returns>
    RestrictionPolicyDecision Evaluate(ChannelIdentitySnapshot identity, RestrictionPolicySet? policies);
}
