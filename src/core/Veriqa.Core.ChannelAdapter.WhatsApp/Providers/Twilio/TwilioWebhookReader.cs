// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

using Veriqa.Core.ChannelAdapter.WhatsApp.Abstractions;
using Veriqa.Core.ChannelAdapter.WhatsApp.Constants;

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Providers.Twilio;

/// <summary>
/// Reader of the Twilio inbound message webhook: an <c>application/x-www-form-urlencoded</c> body with one
/// message per request. The Twilio wire format lives here and nowhere else: which fields carry the message, the
/// sender and a pressed button, and the <c>whatsapp:</c> prefix of the addresses. The sender is its
/// business-scoped user ID (<c>ExternalUserId</c>); <c>From</c> is its number only while the user does not hide
/// it — without a number Twilio puts the business-scoped user ID into <c>From</c> as well.
/// </summary>
internal static class TwilioWebhookReader
{
    /// <summary>
    /// Reads the form body of a webhook. Used by the signature check as well, since Twilio signs the decoded
    /// parameters of the form, not the raw body.
    /// </summary>
    /// <param name="body">Raw request body.</param>
    /// <param name="form">The decoded form fields; null when the body is not a readable form.</param>
    /// <returns>Whether the body was read.</returns>
    public static bool TryReadForm(
        ReadOnlySpan<byte> body,
        [NotNullWhen(true)] out IReadOnlyDictionary<string, StringValues>? form)
    {
        // The method decodes the body as UTF-8 and parses it with the framework form reader, whose limits
        // (field count, key and value length) turn an oversized body into a refusal instead of an allocation.
        try
        {
            using var reader = new FormReader(Encoding.UTF8.GetString(body));
            form = new ReadOnlyDictionary<string, StringValues>(reader.ReadForm());
            return true;
        }
        catch (InvalidDataException)
        {
            form = null;
            return false;
        }
    }

    /// <summary>
    /// Reads the webhook body into neutral messages.
    /// </summary>
    /// <param name="body">Raw request body.</param>
    /// <param name="logger">Logger of the provider.</param>
    /// <returns>
    /// One message with the parsed form as the raw event; no messages for a request that carries neither a
    /// pressed button nor a text (a status callback, a media message without a caption); no messages and no raw
    /// event for a body that is not a readable form.
    /// </returns>
    public static WhatsAppInboundEvent Read(ReadOnlySpan<byte> body, ILogger logger)
    {
        // The method classifies the one message of the request by its fields: a button payload makes it a button
        // press (the Body of a press is only the button's caption and is not carried), otherwise a non-empty Body
        // makes it a text. What the message means is the adapter's channel logic, not decided here.
        if (!TryReadForm(body, out var form))
        {
            logger.LogWarning("Failed to read the Twilio webhook form body.");
            return new WhatsAppInboundEvent([], null);
        }

        var buttonPayload = FieldOrNull(form, TwilioConstants.ButtonPayloadField);
        var text = buttonPayload is null ? FieldOrNull(form, TwilioConstants.BodyField) : null;

        if (buttonPayload is null && text is null)
        {
            return new WhatsAppInboundEvent([], form);
        }

        var senderUserId = StripWhatsAppPrefix(FieldOrNull(form, TwilioConstants.ExternalUserIdField));
        var from = StripWhatsAppPrefix(FieldOrNull(form, TwilioConstants.FromField));

        var message = new WhatsAppInboundMessage(
            MessageId: FieldOrNull(form, TwilioConstants.MessageSidField) ?? string.Empty,
            SenderPhone: string.Equals(from, senderUserId, StringComparison.Ordinal) ? null : from,
            SenderDisplayName: FieldOrNull(form, TwilioConstants.ProfileNameField),
            MessageType: buttonPayload is null ? WhatsAppInboundMessageTypes.Text : WhatsAppInboundMessageTypes.Button,
            Text: text,
            ButtonPayload: buttonPayload,
            Timestamp: null)
        {
            SenderUserId = senderUserId,
            SenderUsername = FieldOrNull(form, TwilioConstants.UsernameField)
        };

        return new WhatsAppInboundEvent([message], form);
    }

    /// <summary>
    /// Returns a form field's value, or null when the field is absent or blank.
    /// </summary>
    /// <param name="form">Decoded form fields.</param>
    /// <param name="field">Field name.</param>
    /// <returns>The first value of the field, or null.</returns>
    private static string? FieldOrNull(IReadOnlyDictionary<string, StringValues> form, string field)
    {
        if (!form.TryGetValue(field, out var values) || values.Count is 0)
        {
            return null;
        }

        var value = values[0];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Removes the <c>whatsapp:</c> address prefix Twilio puts before a WhatsApp address.
    /// </summary>
    /// <param name="address">Address as received.</param>
    /// <returns>The address without the prefix; the value as is when it has none; null for null.</returns>
    private static string? StripWhatsAppPrefix(string? address)
    {
        if (address is null)
        {
            return null;
        }

        return address.StartsWith(TwilioConstants.WhatsAppAddressPrefix, StringComparison.OrdinalIgnoreCase)
            ? address[TwilioConstants.WhatsAppAddressPrefix.Length..]
            : address;
    }
}
