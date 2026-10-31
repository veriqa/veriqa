// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Performs the post-completion action of a sign-in (SPEC-012 §4.9). The one entry point every path
/// that completes a sign-in calls — the channel event, the web confirmation page, the steps of the
/// claims completion — so the action does not depend on the surface the user confirmed on.
/// The action is a continuation of the outcome and never the outcome itself: the receipt of the
/// outcome is delivered by its own path whatever is resolved here (SPEC-036 TPL-104).
/// </summary>
internal interface ICompletionBehaviorDispatcher
{
    /// <summary>
    /// Runs the post-completion action of a completed sign-in in the channel of its identity snapshot;
    /// never throws, never changes the sign-in result.
    /// </summary>
    /// <param name="completed">The transaction as the completing write returned it: that instance
    /// carries the resolved identity the welcome message is worded with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed operation.</returns>
    Task DispatchAsync(Transaction completed, CancellationToken cancellationToken = default);
}
