// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Veriqa.Core.ChannelAdapter.Instagram;

/// <summary>
/// Answer of the Send API to a delivered message.
/// </summary>
internal sealed class InstagramSendResponse
{
    /// <summary>
    /// Instagram-scoped ID of the recipient.
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
/// Profile of an Instagram user, as the account that received their message may read it.
/// </summary>
internal sealed class InstagramUserProfile
{
    /// <summary>
    /// Name of the user.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Username of the user.
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; init; }

    /// <summary>
    /// Address of the profile picture.
    /// </summary>
    [JsonPropertyName("profile_pic")]
    public string? ProfilePic { get; init; }
}
