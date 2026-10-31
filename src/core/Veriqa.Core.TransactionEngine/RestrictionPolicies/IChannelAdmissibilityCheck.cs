// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// An admissibility check a deployment adds to the gate every channel event passes before anything
/// of the channel is written onto a transaction (SPEC-018 §6.3).
/// </summary>
/// <remarks>
/// <para>
/// The gate is an ordered conjunction (RP-100): the checks of the core run first, the registered
/// checks after them, in the order of their registration, up to the first refusal. A check has two
/// answers only — it refuses or it does not object. It cannot admit a sender another check refused
/// and cannot relax a policy of the core (RP-101), which is why it is handed the identifier of the
/// transaction and not the transaction itself.
/// </para>
/// <para>
/// Register any number of implementations in the container. The consumer is a singleton, so an
/// implementation has to be usable from one. The gate may ask about the same channel event twice and
/// about a transaction that is already decided, so an implementation must not have effects that depend
/// on how many times it was called.
/// </para>
/// <para>
/// An exception is a refusal: a check the deployment registered and that could not answer does not
/// count as "no objection". A cancellation of the caller's token is passed on as a cancellation.
/// </para>
/// <para>
/// A check keeps its own configuration under its own resolver keys; the resolution context it is
/// handed is the one the core resolves its own policies with.
/// </para>
/// </remarks>
public interface IChannelAdmissibilityCheck
{
    /// <summary>
    /// States whether this check refuses the sender of a channel event.
    /// </summary>
    /// <param name="transactionId">Transaction the event acts on.</param>
    /// <param name="identity">Identity of the sender as the channel handed it over.</param>
    /// <param name="resolutionContext">Context for resolving the configuration keys of the check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="ChannelAdmissibilityVerdict.Allowed"/> when the check does not object; a verdict made
    /// by <see cref="ChannelAdmissibilityVerdict.Deny"/> when it refuses.
    /// </returns>
    ValueTask<ChannelAdmissibilityVerdict> EvaluateAsync(
        TransactionId transactionId,
        ChannelIdentitySnapshot identity,
        ResolutionContext resolutionContext,
        CancellationToken cancellationToken = default);
}
