// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Confirmation context factory (SPEC-017 §7.1, ICC-041):
/// builds the ConfirmationPromptContext from the transaction's InitiatorContextSnapshot —
/// in the pipeline (orchestrator), not in the adapter. The adapter is unaware of the transaction structure.
/// </summary>
public interface IConfirmationPromptContextFactory
{
    /// <summary>
    /// Builds the confirmation context from the transaction: filters fields by the DisplayFields
    /// configuration (ICC-015) and applies the anomaly heuristic (SPEC-017 §9).
    /// The result is never null: when there are no initiator details to show, the context says so
    /// explicitly and names the reason — the adapter then uses its default text (ICC-042).
    /// </summary>
    /// <param name="transaction">Transaction (source of the InitiatorContextSnapshot).</param>
    /// <param name="recipient">The one the question is asked of: the channel it is asked in (named by the
    /// text) and the account in that channel (the source of the masked identity slots).</param>
    /// <param name="recipientLocale">Recipient locale from channel data (null — from the transaction's ChannelIdentitySnapshot or the base language).</param>
    /// <param name="surface">Surface the text is shown on — <see cref="MessageSurfaces.InChannel"/> or
    /// <see cref="MessageSurfaces.CorePage"/>; it decides whether the subject of a confirmation may name the
    /// account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Confirmation context, always populated.</returns>
    Task<ConfirmationPromptContext> CreateAsync(
        Transaction transaction,
        ConfirmationPromptRecipient recipient,
        string? recipientLocale,
        string surface,
        CancellationToken cancellationToken = default);
}
