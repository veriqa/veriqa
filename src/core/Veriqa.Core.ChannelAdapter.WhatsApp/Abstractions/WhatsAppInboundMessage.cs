// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Abstractions;

/// <summary>
/// One inbound WhatsApp message in the provider-neutral form the channel logic of the adapter reads.
/// A message is a text (<see cref="Text"/> set), a pressed button (<see cref="ButtonPayload"/> set) or a
/// shared contact card (<see cref="SharedContact"/> set); the adapter classifies it by those fields, not by
/// <see cref="MessageType"/>.
/// </summary>
/// <param name="MessageId">Provider's message identifier (Meta: the <c>wamid</c>; Twilio: <c>MessageSid</c>).</param>
/// <param name="SenderPhone">
/// Sender number, if the platform sent it, as received with the provider's address prefix (<c>whatsapp:</c>)
/// removed; the adapter normalizes it to E.164. Null when the platform did not send it — a user who turned on
/// a WhatsApp username may hide the number. The identity of the sender is <see cref="SenderUserId"/>, not this.
/// </param>
/// <param name="SenderDisplayName">Sender profile name; null when absent.</param>
/// <param name="MessageType">
/// <see cref="WhatsAppInboundMessageTypes.Text"/>, <see cref="WhatsAppInboundMessageTypes.Button"/>, or the
/// provider's own type as received otherwise; recorded in the identity metadata.
/// </param>
/// <param name="Text">Text body as received; null for a message that is not a text.</param>
/// <param name="ButtonPayload">Identifier (payload) of the pressed button; null for a message that is not a button press.</param>
/// <param name="Timestamp">Provider timestamp as received; null when absent.</param>
public sealed record WhatsAppInboundMessage(
    string MessageId,
    string? SenderPhone,
    string? SenderDisplayName,
    string MessageType,
    string? Text,
    string? ButtonPayload,
    string? Timestamp)
{
    /// <summary>
    /// The contact card of a shared contact message; null for any other message, and always null from a
    /// provider that cannot ask for the user's contact.
    /// </summary>
    public WhatsAppSharedContact? SharedContact { get; init; }

    /// <summary>
    /// Business-scoped user ID (BSUID) of the sender without the provider's address prefix, of the form
    /// <c>US.13491208655302741918</c> — the identity of the sender in the channel. Null when the provider did
    /// not send it; the adapter refuses such a message, since it cannot tell who sent it.
    /// </summary>
    public string? SenderUserId { get; init; }

    /// <summary>
    /// WhatsApp username of the sender without a leading <c>@</c>; null when the user has not turned one on or
    /// the provider does not pass it. A hint for the relying party, never the identity of the sender.
    /// </summary>
    public string? SenderUsername { get; init; }
}
