// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Veriqa.Core.ChannelAdapter.Messenger;

/// <summary>
/// Answer of the Send API to a delivered message.
/// </summary>
internal sealed class MessengerSendResponse
{
    /// <summary>
    /// Page-scoped ID of the recipient.
    /// </summary>
    [JsonPropertyName("recipient_id")]
    public string? RecipientId { get; init; }

    /// <summary>
    /// Identifier of the delivered message.
    /// </summary>
    [JsonPropertyName("message_id")]
    public string? MessageId { get; init; }
}

/// <summary>
/// Profile of a Messenger user, as the Page that received their message may read it.
/// </summary>
internal sealed class MessengerUserProfile
{
    /// <summary>
    /// First name of the user.
    /// </summary>
    [JsonPropertyName("first_name")]
    public string? FirstName { get; init; }

    /// <summary>
    /// Last name of the user.
    /// </summary>
    [JsonPropertyName("last_name")]
    public string? LastName { get; init; }

    /// <summary>
    /// Address of the profile picture.
    /// </summary>
    [JsonPropertyName("profile_pic")]
    public string? ProfilePic { get; init; }

    /// <summary>
    /// Locale of the user as the Graph API writes it (<c>en_US</c>).
    /// </summary>
    [JsonPropertyName("locale")]
    public string? Locale { get; init; }
}
