// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Resolver of the post-completion action applied in a channel (SPEC-012 §4.9). The single point that
/// encapsulates the flow: level resolver (§10.6) asked about the channel → check of the action against
/// the facts the channel declared → replacement of an action the channel cannot render with the
/// channel's default, reported to the log and to the journal.
/// Consumers receive an action the channel can render, never the one that was merely asked for.
/// </summary>
internal interface ICompletionBehaviorResolver
{
    /// <summary>
    /// Resolves the requested action for the transaction's ownership and the adapter's channel and
    /// clamps it to the channel facts.
    /// </summary>
    /// <param name="transaction">The transaction (source of resolution context: tenant, application).</param>
    /// <param name="adapter">The channel adapter (source of the channel type and channel facts).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The action to perform in the channel.</returns>
    ValueTask<CompletionBehaviorAction> ResolveAsync(
        Transaction transaction,
        IChannelAdapter adapter,
        CancellationToken cancellationToken = default);
}
