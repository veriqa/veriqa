// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Record of the phone number request last sent into one chat (SPEC-041 L44, L73): which transaction
/// the chat's next answer to the request belongs to. The answer itself cannot say — a shared contact
/// carries no transaction identifier on any platform, and neither does a plain keyboard button — so
/// the chat is the key and the last request sent into it wins. The record stays until the terminal
/// outcome receipt of the sign-in removes it, or until <see cref="ExpiresAt"/>. The reference to the
/// request message is not kept here — it lives in
/// <see cref="ChannelPromptMessageRef.PhoneRequestMessage"/>.
/// </summary>
public sealed record PhoneRequestCorrelation
{
    /// <summary>
    /// Tenant of the chat — the tenant of the channel identity snapshot of the transaction. Null — the
    /// default implicit tenant.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Channel type of the chat.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// User of the chat within the channel.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// Transaction the request belongs to.
    /// </summary>
    public required TransactionId TransactionId { get; init; }

    /// <summary>
    /// Secondary action the request offered.
    /// </summary>
    public required PhoneStepAction SecondaryAction { get; init; }

    /// <summary>
    /// Label of the secondary button exactly as it was sent — what a plain keyboard button answers
    /// with as the text of the message.
    /// </summary>
    public required string SecondaryButtonLabel { get; init; }

    /// <summary>
    /// Moment the record stops answering — the deadline of the transaction.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
