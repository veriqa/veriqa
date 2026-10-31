// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Veriqa.Core.ChannelAdapter.WeChat;

/// <summary>
/// Answer of the Official Account API to an access-token request.
/// </summary>
internal sealed class WeChatAccessTokenResponse
{
    /// <summary>
    /// The access token; absent from an answer that carries an error.
    /// </summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    /// <summary>
    /// Lifetime of the access token, in seconds.
    /// </summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    /// <summary>
    /// Error code of the platform; absent (zero) from a successful answer.
    /// </summary>
    [JsonPropertyName("errcode")]
    public int ErrCode { get; init; }
}

/// <summary>
/// Answer of the Official Account API to a call made with an access token. A refusal arrives in the
/// body of a successful HTTP answer, as a non-zero error code.
/// </summary>
internal class WeChatApiResponse
{
    /// <summary>
    /// Error code of the platform; zero (or absent) for a call the platform carried out.
    /// </summary>
    [JsonPropertyName("errcode")]
    public int ErrCode { get; init; }
}

/// <summary>
/// Answer of the Official Account API to a QR code creation call.
/// </summary>
internal sealed class WeChatQrCodeResponse : WeChatApiResponse
{
    /// <summary>
    /// Content of the QR code: the address a scanner reads off it. Absent from an answer that
    /// carries an error.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}
