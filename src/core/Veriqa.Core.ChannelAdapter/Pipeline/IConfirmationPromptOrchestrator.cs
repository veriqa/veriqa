// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Confirmation message orchestrator in the pipeline (SPEC-003 §13.2, SPEC-017 §7.1):
/// builds the ConfirmationPromptContext from the transaction (ICC-041) and sends the prompt
/// through the adapter. The adapter neither calls the Transaction Engine nor reads the transaction (CA-012).
/// </summary>
public interface IConfirmationPromptOrchestrator
{
    /// <summary>
    /// Resolves the effective confirmation surface of the AuthStart event (SPEC-012 §4.4.2) and,
    /// for <see cref="ConfirmationSurface.InChannel"/> only, sends the confirmation message with the
    /// initiator context. The other two surfaces send nothing: the caller learns from the returned
    /// outcome whether to auto-confirm (<c>None</c>) or to leave the transaction waiting for the
    /// answer on the core page (<c>OnWebPage</c>). A prompt is not sent at all on a transaction
    /// whose decision is already recorded (any terminal state), so a repeat delivery of the same
    /// inbound event never reaches the adapter twice.
    /// </summary>
    /// <param name="transaction">Transaction (source of the InitiatorContextSnapshot).</param>
    /// <param name="adapter">Confirmation channel adapter.</param>
    /// <param name="sender">Identity snapshot of the sender of the AuthStart event — the user the question
    /// is asked of (its <see cref="ChannelIdentitySnapshot.ChannelUserId"/> is the recipient within the
    /// channel) and the account the text of the question may name.</param>
    /// <param name="recipientLocale">Recipient locale from channel data (SPEC-017 §7.2; null — base language).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome the caller must act on.</returns>
    Task<AuthStartConfirmationOutcome> HandleAuthStartConfirmationAsync(
        Transaction transaction,
        IChannelAdapter adapter,
        ChannelIdentitySnapshot sender,
        string? recipientLocale,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a decision arriving from this channel may be acted on at all (SPEC-039 E41).
    /// </summary>
    /// <remarks>
    /// A transaction whose subject IS the confirmation is only ever asked about where its subject was
    /// shown. The set of adapters is open, so "the channel that was not sent the subject does not
    /// answer for it" cannot rest on the discipline of an adapter's own code: the question is asked
    /// here, of the same resolver that decided where to ask, and the answer applies to every channel
    /// alike. A sign-in transaction is never refused — its surface is resolved by the existing
    /// mechanism and its paths are unchanged.
    /// </remarks>
    /// <param name="transaction">Transaction the decision arrived for.</param>
    /// <param name="adapter">Channel adapter the decision arrived through.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the decision may be acted on; <c>false</c> — it is dropped and recorded
    /// as an anomaly of the adapter.</returns>
    ValueTask<bool> AcceptsChannelDecisionAsync(
        Transaction transaction,
        IChannelAdapter adapter,
        CancellationToken cancellationToken = default);
}
