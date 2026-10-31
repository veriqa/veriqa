// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// The core's intent "show the user where the transaction stands" (SPEC-003 §6.2): that it reached
/// a terminal state, or — the one non-terminal case — that the sign-in is confirmed and waits for
/// the claims it still lacks (<see cref="TransactionOutcome.AwaitingClaims"/>). Which platform calls
/// that takes — answering a callback, editing a message, sending a new one, or nothing at all — is
/// the adapter's decision.
/// <para>
/// A non-terminal notice is not a promise of another one. A sign-in that went through the claims
/// completion and ended confirmed, declined or failed is always reported by a terminal notice; one
/// that expired is reported on the terms of any expiry — only to a channel whose reference to the
/// question was stored — so the terminal notice may never come.
/// </para>
/// </summary>
public sealed record TransactionOutcomeNotice
{
    /// <summary>
    /// Transaction the outcome belongs to.
    /// </summary>
    public required TransactionId TransactionId { get; init; }

    /// <summary>
    /// What is reported: a terminal outcome of the transaction, or the non-terminal state of its
    /// claims completion.
    /// </summary>
    public required TransactionOutcome Outcome { get; init; }

    /// <summary>
    /// Whether the notice reports the end of the transaction. <see langword="false"/> for
    /// <see cref="TransactionOutcome.AwaitingClaims"/> alone; any other value — an unknown one and
    /// <c>default</c> included — is terminal.
    /// </summary>
    /// <remarks>
    /// Computed over <see cref="Outcome"/> rather than stored beside it: two sources of the same
    /// fact could disagree.
    /// </remarks>
    public bool IsTerminal => Outcome != TransactionOutcome.AwaitingClaims;

    /// <summary>
    /// Recipient within the channel — needed to send a new message when an update is impossible.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// Status text of the reported outcome or state, already localized by the core into the recipient's language
    /// (the Natural Key itself when no translation exists).
    /// </summary>
    public required string StatusText { get; init; }

    /// <summary>
    /// Opaque token of the inbound interaction: present on a live button press, absent on the TTL path.
    /// </summary>
    public ChannelInboundToken? InboundToken { get; init; }

    /// <summary>
    /// Reference to the previously sent prompt: present when the channel supports updates and the
    /// reference was stored.
    /// </summary>
    public ChannelMessageRef? PromptMessage { get; init; }

    /// <summary>
    /// Reference to the message that asked the user for a phone number, when the step it belongs to
    /// is over; <see langword="null"/> — there is none. Stated on a terminal notice only. The adapter
    /// takes away what that request offered the user — its buttons or its input keyboard — by the
    /// means of its platform; a notice without the reference is not an error, and neither is a
    /// platform with nothing to take away.
    /// </summary>
    /// <remarks>
    /// Not <c>required</c> on purpose: a notice built by an adapter or host compiled against the
    /// previous shape of this record carries no reference and behaves exactly as it did before the
    /// field existed.
    /// </remarks>
    public ChannelMessageRef? PhoneRequestMessage { get; init; }

    /// <summary>
    /// How the deployment WANTS this receipt to appear in the conversation: in place of the message
    /// that asked the question, or as a message of its own.
    /// <para>
    /// A desired intent, not a guarantee and not an obligation: the adapter may honour it or may have
    /// no way to — a platform without an editable delivered message cannot replace anything. Leaving
    /// the intent unhonoured is not a refusal, is not logged as an error and does not change the
    /// result of <see cref="Abstractions.IChannelAdapter.ReportOutcomeAsync"/>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Not <c>required</c> on purpose: a notice built without stating the intent — by an adapter or
    /// host compiled against the previous shape of this record — carries the shipped intent and
    /// behaves exactly as it did before the field existed.
    /// </remarks>
    public OutcomeNoticeDisplayIntent DisplayIntent { get; init; }
}
