// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Veriqa.Core.ChannelAdapter.Meta;

/// <summary>
/// Body of a webhook delivery of the Messenger Platform family (Messenger, Instagram): the entries
/// Meta batched into one request. The envelope is the same for every channel of the family; what a
/// channel makes of an event is its own business.
/// </summary>
internal sealed class MetaWebhookPayload
{
    /// <summary>
    /// Entries of the delivery, one per page or account the events belong to.
    /// </summary>
    [JsonPropertyName("entry")]
    public IReadOnlyList<MetaWebhookEntry>? Entry { get; init; }
}

/// <summary>
/// One entry of a webhook delivery.
/// </summary>
internal sealed class MetaWebhookEntry
{
    /// <summary>
    /// Messaging events of the entry.
    /// </summary>
    [JsonPropertyName("messaging")]
    public IReadOnlyList<MetaMessagingEvent>? Messaging { get; init; }
}

/// <summary>
/// One messaging event. Exactly one of the content members is present, and it names the kind of
/// the event: a message, a pressed button, an opened link with a referral or a reaction.
/// </summary>
internal sealed class MetaMessagingEvent
{
    /// <summary>
    /// The party the event comes from: the user, or the page or account itself for an echo.
    /// </summary>
    [JsonPropertyName("sender")]
    public MetaMessagingParty? Sender { get; init; }

    /// <summary>
    /// A message in the thread.
    /// </summary>
    [JsonPropertyName("message")]
    public MetaInboundMessage? Message { get; init; }

    /// <summary>
    /// A pressed button of a template.
    /// </summary>
    [JsonPropertyName("postback")]
    public MetaPostback? Postback { get; init; }

    /// <summary>
    /// A link with a referral opened into an existing thread.
    /// </summary>
    [JsonPropertyName("referral")]
    public MetaReferral? Referral { get; init; }

    /// <summary>
    /// A reaction put on, or taken off, a message.
    /// </summary>
    [JsonPropertyName("reaction")]
    public MetaReaction? Reaction { get; init; }
}

/// <summary>
/// A party of a messaging event.
/// </summary>
internal sealed class MetaMessagingParty
{
    /// <summary>
    /// Scoped ID of the party: the identifier of a user is scoped to the page or account they wrote to.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

/// <summary>
/// A message of a messaging event.
/// </summary>
internal sealed class MetaInboundMessage
{
    /// <summary>
    /// Text of the message.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>
    /// True for a message the page or account itself sent — the echo of an outbound message.
    /// </summary>
    [JsonPropertyName("is_echo")]
    public bool IsEcho { get; init; }

    /// <summary>
    /// The quick reply the message was sent by; absent for a message the user typed.
    /// </summary>
    [JsonPropertyName("quick_reply")]
    public MetaQuickReply? QuickReply { get; init; }

    /// <summary>
    /// Referral of the link the thread was opened by, when the message is the first one of that
    /// thread.
    /// </summary>
    [JsonPropertyName("referral")]
    public MetaReferral? Referral { get; init; }
}

/// <summary>
/// A pressed quick reply button.
/// </summary>
internal sealed class MetaQuickReply
{
    /// <summary>
    /// Payload of the button: the one it was sent with, or — for the button that shares the phone
    /// number — the number of the user's profile.
    /// </summary>
    [JsonPropertyName("payload")]
    public string? Payload { get; init; }
}

/// <summary>
/// A pressed button of a template.
/// </summary>
internal sealed class MetaPostback
{
    /// <summary>
    /// Identifier of the press as the platform names it; a repeated delivery of the event carries
    /// the same one.
    /// </summary>
    [JsonPropertyName("mid")]
    public string? Mid { get; init; }

    /// <summary>
    /// Payload the button was sent with.
    /// </summary>
    [JsonPropertyName("payload")]
    public string? Payload { get; init; }

    /// <summary>
    /// Referral of the link the thread was opened by, when the press is the first thing the user
    /// did in that thread.
    /// </summary>
    [JsonPropertyName("referral")]
    public MetaReferral? Referral { get; init; }
}

/// <summary>
/// Referral of a link that opens a thread (<c>m.me</c>, <c>ig.me</c>).
/// </summary>
internal sealed class MetaReferral
{
    /// <summary>
    /// Value of the <c>ref</c> parameter of the link.
    /// </summary>
    [JsonPropertyName("ref")]
    public string? Ref { get; init; }
}

/// <summary>
/// A reaction to a message. Its presence alone tells the event apart; no member of it is read.
/// </summary>
internal sealed class MetaReaction;
