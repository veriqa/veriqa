// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Store record of the chat a transaction was started from (SPEC-003 §4.5): who to tell about a later
/// event that does not come from the chat — a TTL expiry, or the cancel button of the sign-in window or
/// of the entry page of a confirmation while the question stands in the chat — and in which channel, with the optional references to the
/// messages that event has to address — the confirmation prompt and the phone number request.
/// Written by the core once the start of the transaction in the channel is decided, whether the
/// question was asked in the chat or not.
/// </summary>
/// <remarks>
/// The message coordinates themselves live in the MIT contract shape
/// (<see cref="ChannelMessageRef"/>); everything else here is core bookkeeping the adapter never
/// sees — which channel was called, which tenant owned the send, which locale and which recipient.
/// </remarks>
public sealed record ChannelPromptMessageRef
{
    /// <summary>
    /// Type of the channel the transaction was started from (e.g. "telegram", "max").
    /// The expiry handler resolves the adapter by this type when no ambient context is left.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Channel-defined coordinates of the sent prompt message. Null — there is no message to address:
    /// the question was not asked in the chat, or the channel cannot update a message it sent and its
    /// reference is not kept.
    /// </summary>
    public ChannelMessageRef? Message { get; init; }

    /// <summary>
    /// Coordinates of the phone number request sent into the chat for this transaction, as the
    /// platform returned them (SPEC-041 L44) — what a terminal notice hands the adapter to take the
    /// request off the chat. Null — no request was sent, or the platform returned no reference.
    /// </summary>
    public ChannelMessageRef? PhoneRequestMessage { get; init; }

    /// <summary>
    /// Whether the question of this transaction was shown in this channel. False on a record written
    /// without a question (it was asked on the web page, or not asked at all) and on a record read
    /// back without the field. A separate fact from <see cref="Message"/>: a channel that cannot
    /// update its message shows the question and leaves no reference.
    /// </summary>
    public bool PromptShown { get; init; }

    /// <summary>
    /// Recipient within the channel, captured when the record was written. The background TTL path knows nothing
    /// else about the user, and the outcome notice requires a recipient.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// Tenant that owns the record (CA-164). Null — the default implicit tenant (self-hosted N=1).
    /// Neither a background handler nor a request of the sign-in window has an ambient channel tenant,
    /// so the tenant is captured here and restored
    /// (<see cref="MultiTenancy.ChannelTenantContext.BeginScope"/>) before the outcome notice.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Recipient locale captured when the record was written (SPEC-017 §7.2), used to localize the terminal
    /// status text. Null — base language.
    /// </summary>
    public string? RecipientLocale { get; init; }

    /// <summary>
    /// Recipient time zone captured when the record was written (IANA identifier; null — none was stated). The other
    /// half of the same snapshot the locale is: how the recipient READS the terminal text, locale for
    /// its wording and zone for the moments in it (SPEC-036 TPL-016). The background TTL path has no
    /// transaction left to read either from, so both travel here; a record written before this field
    /// existed reads back with none, exactly as a transaction that stated no zone does.
    /// </summary>
    public string? RecipientTimeZone { get; init; }
}
