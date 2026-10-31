// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Veriqa.Core.ChannelAdapter.Viber;

/// <summary>
/// Envelope of an event the platform delivers to the webhook of the bot. Only the members a
/// sign-in reads are declared; the rest of the body is skipped.
/// </summary>
internal sealed class ViberWebhookPayload
{
    /// <summary>
    /// Name of the event.
    /// </summary>
    [JsonPropertyName("event")]
    public string? Event { get; init; }

    /// <summary>
    /// Value of the <c>context</c> parameter of the deep link the conversation was opened by —
    /// a member of the conversation-started event.
    /// </summary>
    [JsonPropertyName("context")]
    public string? Context { get; init; }

    /// <summary>
    /// User who opened the conversation — a member of the conversation-started event.
    /// </summary>
    [JsonPropertyName("user")]
    public ViberUser? User { get; init; }

    /// <summary>
    /// Token of the message, unique per message; a repeated delivery of the event carries the same
    /// one — a member of the message event.
    /// </summary>
    [JsonPropertyName("message_token")]
    public long? MessageToken { get; init; }

    /// <summary>
    /// User who sent the message — a member of the message event.
    /// </summary>
    [JsonPropertyName("sender")]
    public ViberUser? Sender { get; init; }

    /// <summary>
    /// The message — a member of the message event.
    /// </summary>
    [JsonPropertyName("message")]
    public ViberInboundMessage? Message { get; init; }
}

/// <summary>
/// A Viber user as an event names them.
/// </summary>
internal sealed class ViberUser
{
    /// <summary>
    /// Identifier of the user, unique per bot.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>
    /// Name of the user.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Address of the avatar of the user.
    /// </summary>
    [JsonPropertyName("avatar")]
    public string? Avatar { get; init; }

    /// <summary>
    /// Language of the device of the user (ISO 639-1).
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }
}

/// <summary>
/// A message a user sent to the bot.
/// </summary>
internal sealed class ViberInboundMessage
{
    /// <summary>
    /// Type of the message.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Text of a text message; a pressed reply button arrives as its action body here.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>
    /// Address a media message carries; for a message of type <c>url</c> — the link the user sent.
    /// </summary>
    [JsonPropertyName("media")]
    public string? Media { get; init; }

    /// <summary>
    /// Contact of a contact message.
    /// </summary>
    [JsonPropertyName("contact")]
    public ViberContact? Contact { get; init; }
}

/// <summary>
/// Contact carried by a contact message.
/// </summary>
internal sealed class ViberContact
{
    /// <summary>
    /// Phone number of the contact, in the spelling of the platform.
    /// </summary>
    [JsonPropertyName("phone_number")]
    public string? PhoneNumber { get; init; }
}

/// <summary>
/// Answer of the Bot API to a call.
/// </summary>
internal sealed class ViberApiResponse
{
    /// <summary>
    /// Status of the call; null when the answer carries none.
    /// </summary>
    [JsonPropertyName("status")]
    public int? Status { get; init; }

    /// <summary>
    /// Token of the sent message — a member of the answer to an outbound message.
    /// </summary>
    [JsonPropertyName("message_token")]
    public long? MessageToken { get; init; }
}

/// <summary>
/// Body of an outbound text message. The member names are stated one by one: the platform reads
/// them case-sensitively, and a naming policy of a serializer must not get a say in them.
/// </summary>
internal sealed class ViberOutboundMessage
{
    /// <summary>
    /// Identifier of the user the message is addressed to.
    /// </summary>
    [JsonPropertyName("receiver")]
    public required string Receiver { get; init; }

    /// <summary>
    /// Minimal API version a client has to support for this message; null leaves the platform default.
    /// </summary>
    [JsonPropertyName("min_api_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinApiVersion { get; init; }

    /// <summary>
    /// Sender the message is shown from.
    /// </summary>
    [JsonPropertyName("sender")]
    public required ViberOutboundSender Sender { get; init; }

    /// <summary>
    /// Type of the message.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Text of the message.
    /// </summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>
    /// Keyboard shown under the message; null sends none, which also takes down a keyboard shown
    /// by an earlier message.
    /// </summary>
    [JsonPropertyName("keyboard")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ViberKeyboard? Keyboard { get; init; }
}

/// <summary>
/// Sender of an outbound message.
/// </summary>
internal sealed class ViberOutboundSender
{
    /// <summary>
    /// Name the message is shown from.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>
/// Custom keyboard of an outbound message. Its member names are capitalized on the wire.
/// </summary>
internal sealed class ViberKeyboard
{
    /// <summary>
    /// Type of the keyboard.
    /// </summary>
    [JsonPropertyName("Type")]
    public required string Type { get; init; }

    /// <summary>
    /// Buttons of the keyboard.
    /// </summary>
    [JsonPropertyName("Buttons")]
    public required IReadOnlyList<ViberKeyboardButton> Buttons { get; init; }
}

/// <summary>
/// Button of a custom keyboard. Its member names are capitalized on the wire.
/// </summary>
internal sealed class ViberKeyboardButton
{
    /// <summary>
    /// What a press of the button does.
    /// </summary>
    [JsonPropertyName("ActionType")]
    public required string ActionType { get; init; }

    /// <summary>
    /// Value the action works with; for a reply button — the text sent to the bot, for an open-url
    /// button — the address it opens.
    /// </summary>
    [JsonPropertyName("ActionBody")]
    public required string ActionBody { get; init; }

    /// <summary>
    /// Caption of the button.
    /// </summary>
    [JsonPropertyName("Text")]
    public required string Text { get; init; }

    /// <summary>
    /// Whether the press stays out of the conversation: true — the client shows nothing in the chat
    /// for it. The platform honours it on clients of version 6.7 and later; an older client shows the
    /// press anyway. Null leaves the member out, which is the platform default (false).
    /// </summary>
    [JsonPropertyName("Silent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Silent { get; init; }
}

/// <summary>
/// Body of the call that sets or removes the webhook of the bot.
/// </summary>
internal sealed class ViberSetWebhookRequest
{
    /// <summary>
    /// Address the platform delivers events to; empty removes the webhook.
    /// </summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    /// <summary>
    /// Events the webhook subscribes to besides the ones the platform always delivers; null leaves
    /// the member out.
    /// </summary>
    [JsonPropertyName("event_types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EventTypes { get; init; }
}
