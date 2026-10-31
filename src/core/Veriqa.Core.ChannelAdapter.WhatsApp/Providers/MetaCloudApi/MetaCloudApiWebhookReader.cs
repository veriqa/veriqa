// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.WhatsApp.Abstractions;
using Veriqa.Core.ChannelAdapter.WhatsApp.Constants;

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Providers.MetaCloudApi;

/// <summary>
/// Reader of the Meta Cloud API webhook body (<see cref="WhatsAppWebhookPayload"/>) into the neutral
/// inbound messages the channel logic of the adapter works on. The Meta wire format lives here and
/// nowhere else: the objects and fields it filters on, where a pressed button carries its payload, and how
/// the sender's profile name and username are found in the <c>contacts</c> list.
/// </summary>
internal static class MetaCloudApiWebhookReader
{
    /// <summary>
    /// WhatsApp webhook payload serialization options.
    /// </summary>
    private static readonly JsonSerializerOptions WebhookJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads the webhook body into neutral messages.
    /// </summary>
    /// <param name="body">Raw request body.</param>
    /// <param name="logger">Logger of the provider.</param>
    /// <returns>
    /// The messages of the <c>messages</c> changes in the order they arrived, with the parsed payload as the raw
    /// event; no messages (and no raw event) for a body that is not JSON of this shape.
    /// </returns>
    public static WhatsAppInboundEvent Read(ReadOnlySpan<byte> body, ILogger logger)
    {
        // The method deserializes the body, keeps only a WhatsApp Business Account object, and flattens the
        // messages of every "messages" change in document order. Nothing here decides what a message means —
        // that is the adapter's channel logic; this only says what the message is in Meta's format.
        WhatsAppWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WhatsAppWebhookPayload>(body, WebhookJsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize the WhatsApp webhook payload.");
            return new WhatsAppInboundEvent([], null);
        }

        if (payload is null)
        {
            return new WhatsAppInboundEvent([], null);
        }

        if (!string.Equals(
                payload.ObjectType,
                MetaCloudApiConstants.WebhookObjectType,
                StringComparison.Ordinal))
        {
            return new WhatsAppInboundEvent([], payload);
        }

        var messages = new List<WhatsAppInboundMessage>();

        foreach (var entry in payload.Entries ?? [])
        {
            foreach (var change in entry?.Changes ?? [])
            {
                if (change is null)
                {
                    continue;
                }

                if (!string.Equals(
                        change.Field,
                        MetaCloudApiConstants.WebhookMessagesField,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (change.Value?.Messages is null || change.Value.Messages.Count is 0)
                {
                    continue;
                }

                foreach (var message in change.Value.Messages)
                {
                    // A null element of the array is JSON the reader must not throw on; it carries nothing.
                    if (message is null)
                    {
                        continue;
                    }

                    messages.Add(ToInboundMessage(message, change.Value.Contacts));
                }
            }
        }

        return new WhatsAppInboundEvent(messages.AsReadOnly(), payload);
    }

    /// <summary>
    /// Maps one Meta webhook message to its neutral form.
    /// </summary>
    /// <param name="message">Inbound webhook message.</param>
    /// <param name="contacts">Contacts of the same change, for the lookup of the sender's profile.</param>
    /// <returns>The neutral message.</returns>
    private static WhatsAppInboundMessage ToInboundMessage(
        WhatsAppWebhookMessage message,
        IReadOnlyList<WhatsAppWebhookContact>? contacts)
    {
        // The method keeps Meta's own type as is and decides text-or-button by it: a "text" message
        // (case-insensitive) carries its body and never a button; any other type carries the payload of a
        // pressed button when it has one (interactive button_reply or list_reply, or template button). A "contacts" message
        // carries the card the user shared. The sender is its business-scoped user ID (from_user_id), which
        // every message carries; the number (from) is there only while the user does not hide it.
        var messageType = message.Type ?? string.Empty;
        var isText = string.Equals(
            messageType,
            MetaCloudApiConstants.InboundTypeText,
            StringComparison.OrdinalIgnoreCase);
        var senderProfile = FindSenderProfile(contacts, message.FromUserId);

        return new WhatsAppInboundMessage(
            MessageId: message.Id ?? string.Empty,
            SenderPhone: message.From,
            SenderDisplayName: TrimOrNull(senderProfile?.Name),
            MessageType: messageType,
            Text: isText ? message.Text?.Body : null,
            ButtonPayload: isText ? null : TryExtractCallbackPayload(message),
            Timestamp: message.Timestamp)
        {
            SenderUserId = message.FromUserId,
            SenderUsername = TrimOrNull(senderProfile?.Username),
            SharedContact = string.Equals(
                    messageType,
                    MetaCloudApiConstants.InboundTypeContacts,
                    StringComparison.OrdinalIgnoreCase)
                ? TryReadSharedContact(message)
                : null
        };
    }

    /// <summary>
    /// Reads the first card of a shared contact message (SPEC-041 L45, L46): its number — the written one,
    /// else the WhatsApp identifier — and whether it was shared by the request contact info button
    /// (<c>origin = contact_request</c>), which makes it the number of the sender's own account.
    /// </summary>
    /// <param name="message">Inbound contacts message.</param>
    /// <returns>The card, or null when the message carries none.</returns>
    private static WhatsAppSharedContact? TryReadSharedContact(WhatsAppWebhookMessage message)
    {
        var contact = message.Contacts?.FirstOrDefault(card => card is not null);
        if (contact is null)
        {
            return null;
        }

        var phone = contact.Phones?.FirstOrDefault(entry => entry is not null);
        var phoneNumber = string.IsNullOrWhiteSpace(phone?.Phone) ? phone?.WaId : phone.Phone;

        return new WhatsAppSharedContact(
            PhoneNumber: phoneNumber,
            SharedByRequest: string.Equals(
                contact.Origin,
                MetaCloudApiConstants.ContactRequestOrigin,
                StringComparison.Ordinal),
            Origin: contact.Origin);
    }

    /// <summary>
    /// Extracts the callback payload from an interactive or button message.
    /// </summary>
    /// <param name="message">Inbound webhook message.</param>
    /// <returns>The button payload, or null.</returns>
    private static string? TryExtractCallbackPayload(WhatsAppWebhookMessage message)
    {
        // The method extracts the payload from the various WhatsApp button message formats: a reply
        // button, a row of a list message (the post-sign-in menu of more than three items), a template
        // button.
        if (message.Interactive?.ButtonReply?.Id is not null)
        {
            return message.Interactive.ButtonReply.Id;
        }

        if (message.Interactive?.ListReply?.Id is not null)
        {
            return message.Interactive.ListReply.Id;
        }

        if (message.Button?.Payload is not null)
        {
            return message.Button.Payload;
        }

        return null;
    }

    /// <summary>
    /// Looks up the profile of the sender in the contact list: the contact whose <c>user_id</c> is the
    /// business-scoped user ID of the message. The number (<c>wa_id</c>) is not a key — a user who hides it
    /// behind a username has none.
    /// </summary>
    /// <param name="contacts">Contacts from the webhook.</param>
    /// <param name="senderUserId">Business-scoped user ID of the sender as received.</param>
    /// <returns>The profile of the sender, or null when no contact matches.</returns>
    private static WhatsAppWebhookContactProfile? FindSenderProfile(
        IReadOnlyList<WhatsAppWebhookContact>? contacts,
        string? senderUserId)
    {
        if (contacts is null || string.IsNullOrWhiteSpace(senderUserId))
        {
            return null;
        }

        // A null element of the array is JSON the reader must not throw on; it names nobody.
        return contacts.FirstOrDefault(contact =>
            contact is not null && string.Equals(contact.UserId, senderUserId, StringComparison.Ordinal))?.Profile;
    }

    /// <summary>
    /// Trims a profile value.
    /// </summary>
    /// <param name="value">Value as received.</param>
    /// <returns>The trimmed value, or null when it is absent or blank.</returns>
    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
