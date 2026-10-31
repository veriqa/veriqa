// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Max.BotClient.Types;

using Veriqa.Core.ChannelAdapter.Max.Constants;

namespace Veriqa.Core.ChannelAdapter.Max;

/// <summary>
/// A message sharing a contact, in the terms the phone step judges it by (SPEC-041 L45): who sent it,
/// in which kind of chat, the vCard of the first contact attachment with its signature, and the locale of
/// the update. Read off the raw body of the update, which both update modes hand over — the webhook its
/// request body, the polling loop each update of its own <c>GET /updates</c>.
/// </summary>
/// <param name="Sender">The user who sent the message; null — the message names none that reads.</param>
/// <param name="RecipientChatType">Kind of the chat the message was sent in; null — unknown.</param>
/// <param name="VcfInfo">The vCard of the first contact attachment; null — none.</param>
/// <param name="Hash">Signature of that vCard; null — none arrived.</param>
/// <param name="UserLocale">Locale of the update (IETF BCP 47); null — unknown.</param>
internal sealed record MaxSharedContact(
    User? Sender,
    ChatType? RecipientChatType,
    string? VcfInfo,
    string? Hash,
    string? UserLocale)
{
    /// <summary>
    /// Reads a shared contact off the raw body of a <c>message_created</c> update. The shipped client
    /// library leaves the message of such an update empty, so nothing of it is taken from the model: the
    /// sender from <c>message.sender</c> (<c>message.recipient.user_id</c> is the bot), the kind of chat
    /// from <c>message.recipient.chat_type</c>, the vCard and its signature from the payload of the first
    /// <c>contact</c> attachment, the locale from the root <c>user_locale</c>. A field of the wrong kind or
    /// with an unreadable value counts as absent.
    /// </summary>
    /// <param name="body">Raw update body.</param>
    /// <param name="options">Serialization options of the client library, for its leaf types.</param>
    /// <param name="contact">The shared contact on success.</param>
    /// <returns>true — the message carries a contact attachment; false — it does not, or the body is not JSON.</returns>
    public static bool TryRead(
        ReadOnlyMemory<byte> body,
        JsonSerializerOptions options,
        [NotNullWhen(true)] out MaxSharedContact? contact)
    {
        contact = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object
                || !TryGetObject(root, MaxAdapterConstants.UpdateMessageField, out var message)
                || !TryGetObject(message, MaxAdapterConstants.MessageBodyField, out var messageBody)
                || !messageBody.TryGetProperty(MaxAdapterConstants.BodyAttachmentsField, out var attachments)
                || attachments.ValueKind is not JsonValueKind.Array
                || !TryFindFirstContactPayload(attachments, out var payload))
            {
                return false;
            }

            var sender = TryGetObject(message, MaxAdapterConstants.MessageSenderField, out var senderElement)
                ? ReadLeaf<User>(senderElement, options)
                : null;

            var chatType = TryGetObject(message, MaxAdapterConstants.MessageRecipientField, out var recipient)
                && recipient.TryGetProperty(MaxAdapterConstants.RecipientChatTypeField, out var chatTypeElement)
                && chatTypeElement.ValueKind is JsonValueKind.String
                    ? ReadLeaf<ChatType?>(chatTypeElement, options)
                    : null;

            string? vcfInfo = null;
            string? hash = null;
            if (payload is { } contactPayload)
            {
                vcfInfo = ReadString(contactPayload, MaxAdapterConstants.ContactVcfInfoField);
                hash = ReadString(contactPayload, MaxAdapterConstants.ContactHashField);
            }

            contact = new MaxSharedContact(
                sender,
                chatType,
                vcfInfo,
                hash,
                ReadString(root, MaxAdapterConstants.UpdateUserLocaleField));

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the first attachment of type <c>contact</c> — the one whose number is read and whose
    /// signature proves it, and no other.
    /// </summary>
    /// <param name="attachments">The attachments array of the message body.</param>
    /// <param name="payload">Payload of that attachment; null — it has none that is an object.</param>
    /// <returns>true — a contact attachment is present.</returns>
    private static bool TryFindFirstContactPayload(JsonElement attachments, out JsonElement? payload)
    {
        foreach (var attachment in attachments.EnumerateArray())
        {
            if (attachment.ValueKind is not JsonValueKind.Object
                || !attachment.TryGetProperty(MaxAdapterConstants.AttachmentTypeField, out var type)
                || type.ValueKind is not JsonValueKind.String
                || !type.ValueEquals(MaxAdapterConstants.ContactAttachmentType))
            {
                continue;
            }

            payload = TryGetObject(attachment, MaxAdapterConstants.AttachmentPayloadField, out var found)
                ? found
                : null;

            return true;
        }

        payload = null;
        return false;
    }

    /// <summary>
    /// A property of an object that is itself an object.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">Property name.</param>
    /// <param name="value">The property value on success.</param>
    /// <returns>true — the property is present and is an object.</returns>
    private static bool TryGetObject(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind is JsonValueKind.Object;

    /// <summary>
    /// A string property of an object.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The value; null — absent or not a string.</returns>
    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// A leaf type of the client library read from one element of the raw body; an element the library
    /// cannot read counts as absent rather than failing the whole update.
    /// </summary>
    /// <typeparam name="T">The library type.</typeparam>
    /// <param name="element">The element.</param>
    /// <param name="options">Serialization options of the client library.</param>
    /// <returns>The value; default — unreadable.</returns>
    private static T? ReadLeaf<T>(JsonElement element, JsonSerializerOptions options)
    {
        try
        {
            return element.Deserialize<T>(options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
