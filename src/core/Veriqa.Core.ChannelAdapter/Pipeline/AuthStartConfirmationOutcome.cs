// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// What the confirmation orchestrator did with an AuthStart event — one value per effective
/// confirmation surface, so the caller has no "which surface was it, again" rule left to remember.
/// </summary>
public enum AuthStartConfirmationOutcome
{
    /// <summary>
    /// Surface <c>None</c>: no confirmation is required, so the caller auto-confirms the transaction,
    /// completes it and reports the outcome to the channel.
    /// </summary>
    AutoConfirm,

    /// <summary>
    /// Surface <c>InChannel</c>: the prompt branch was applied (including the case where sending the
    /// prompt failed — the transaction then waits for a retry or its TTL). The caller does nothing else.
    /// </summary>
    HandledInChannel,

    /// <summary>
    /// Surface <c>OnWebPage</c>: the question is asked by the core page, not in the channel, and the
    /// transaction is not completed. The caller attaches the channel identity so the waiting browser
    /// can be taken to the question, and a sign-in sends the chat one notice pointing the sender to the
    /// sign-in page (SPEC-012 §4.4.2).
    /// </summary>
    AwaitingWebConfirmation,

    /// <summary>
    /// Surface <c>InChannel</c>, and the adapter declared it cannot carry this confirmation out
    /// (SPEC-003 CA-191). The caller ends the transaction with that reason code and sends the user
    /// nothing in the channel — it has just said it cannot.
    /// </summary>
    ChannelCannotContinue,

    /// <summary>
    /// Surface <c>InChannel</c> on a transaction whose subject IS the confirmation, and there is no
    /// wording of that subject to show (SPEC-039 E28). Nothing was sent: a sign-in wording may not
    /// stand in for the subject, so the question is not asked at all. The caller ends the transaction
    /// with the reason code of an unavailable subject and shows the user the channel's own neutral
    /// error text.
    /// </summary>
    ConfirmationSubjectUnavailable
}
