// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;

namespace Veriqa.Core.ChannelAdapter.WeChat.Constants;

/// <summary>
/// WeChat adapter constants: the string and numeric literals of the adapter itself.
/// </summary>
internal static class WeChatAdapterConstants
{
    /// <summary>
    /// Default base URL of the Official Account API.
    /// </summary>
    public const string DefaultApiBaseUrl = "https://api.weixin.qq.com";

    /// <summary>
    /// Named <see cref="System.Net.Http.IHttpClientFactory"/> client of the Official Account API calls.
    /// </summary>
    public const string HttpClientName = "wechat-official-account-api";

    /// <summary>
    /// Timeout (seconds) of an Official Account API call — an explicit bound instead of the default
    /// 100s, so a stuck request fails fast rather than hanging.
    /// </summary>
    public const int ApiTimeoutSeconds = 30;

    /// <summary>
    /// Query parameter of the webhook address check whose value the platform expects echoed back.
    /// </summary>
    public const string EchoQueryKey = "echostr";

    /// <summary>
    /// Adapter version recorded in the identity snapshot.
    /// </summary>
    public const string AdapterVersion = "1.0.0";

    /// <summary>
    /// Confirm option text — a Natural Key resolved into the recipient's language before the send.
    /// </summary>
    public const string ConfirmButtonText = "Confirm ✅";

    /// <summary>
    /// Decline option text — a Natural Key resolved into the recipient's language before the send.
    /// </summary>
    public const string DeclineButtonText = "Decline ❌";

    /// <summary>
    /// Path of the access-token request, relative to the API base URL: the stable-token call, whose
    /// repeated use within the lifetime of a token returns that same token instead of replacing it.
    /// </summary>
    public const string StableAccessTokenPath = "/cgi-bin/stable_token";

    /// <summary>
    /// Grant of the access-token request: the credentials of the account itself.
    /// </summary>
    public const string GrantTypeClientCredential = "client_credential";

    /// <summary>
    /// Query parameter every call of the account carries its access token in.
    /// </summary>
    public const string AccessTokenQueryKey = "access_token";

    /// <summary>
    /// Path of the QR code creation call, relative to the API base URL.
    /// </summary>
    public const string QrCodeCreatePath = "/cgi-bin/qrcode/create";

    /// <summary>
    /// Kind of the QR code a sign-in is entered by: a temporary one whose scene is a string.
    /// </summary>
    public const string QrCodeActionTemporaryStringScene = "QR_STR_SCENE";

    /// <summary>
    /// Interval (seconds) at which the QR codes remembered by a client are swept of the expired ones.
    /// </summary>
    public const int QrCodeSweepIntervalSeconds = 60;

    /// <summary>
    /// Path of the customer service message call, relative to the API base URL.
    /// </summary>
    public const string CustomerServiceMessagePath = "/cgi-bin/message/custom/send";

    /// <summary>
    /// Customer service message type: a plain text.
    /// </summary>
    public const string MessageTypeText = "text";

    /// <summary>
    /// Customer service message type: a text with a menu of options under it.
    /// </summary>
    public const string MessageTypeMenu = "msgmenu";

    /// <summary>
    /// Markup of a link inside the text of a customer service message: the address, then the caption
    /// the recipient sees. A text message is the one place the sendCustomMessage documentation names a
    /// link in; a menu message promises no markup, so a link is never put into one.
    /// </summary>
    public const string TextLinkMarkupFormat = "<a href=\"{0}\">{1}</a>";

    /// <summary>
    /// Separator of the lines of a message text: the text above the links, and one link per line.
    /// </summary>
    public const string TextLineBreak = "\n";

    /// <summary>
    /// Characters of a link caption that would break the markup around it, each with the full-width
    /// form of the same character it is shown as instead. A look-alike is used rather than an entity:
    /// the platform documents no decoding of entities in a text message, and an entity it showed as
    /// written would garble the caption.
    /// </summary>
    public static readonly FrozenDictionary<char, char> LinkCaptionMarkupLookAlikes = new Dictionary<char, char>
    {
        ['<'] = '＜',
        ['>'] = '＞',
        ['"'] = '＂',
        ['&'] = '＆'
    }.ToFrozenDictionary();

    /// <summary>
    /// Query parameter of an inbound request carrying its signature.
    /// </summary>
    public const string SignatureQueryKey = "signature";

    /// <summary>
    /// Query parameter of an inbound request carrying the moment it was signed (Unix seconds).
    /// </summary>
    public const string TimestampQueryKey = "timestamp";

    /// <summary>
    /// Query parameter of an inbound request carrying the random value of the signature.
    /// </summary>
    public const string NonceQueryKey = "nonce";

    /// <summary>
    /// Maximum distance (seconds) between the signing moment of an inbound request and the clock of
    /// this host. In plaintext mode the body is not signed, so an older request is refused as a replay.
    /// </summary>
    public const long MaxInboundAgeSeconds = 300;

    /// <summary>
    /// Type of an inbound message that reports an event of the account.
    /// </summary>
    public const string InboundTypeEvent = "event";

    /// <summary>
    /// Type of an inbound message that carries a text of the user, a pressed menu option included.
    /// </summary>
    public const string InboundTypeText = "text";

    /// <summary>
    /// Event of a user who followed the account, by scanning a QR code among other ways.
    /// </summary>
    public const string EventSubscribe = "subscribe";

    /// <summary>
    /// Event of a follower who scanned a QR code of the account.
    /// </summary>
    public const string EventScan = "SCAN";

    /// <summary>
    /// Prefix the platform puts before the scene of a QR code in the event of a user who followed
    /// the account by scanning it.
    /// </summary>
    public const string SubscribeScenePrefix = "qrscene_";

    /// <summary>
    /// Error code of a platform answer that carries no error.
    /// </summary>
    public const int ApiSuccessCode = 0;

    /// <summary>
    /// Margin (seconds) before the expiry of an access token at which it is requested anew, so a
    /// call never leaves with a token that expires on the way.
    /// </summary>
    public const int AccessTokenRenewalMarginSeconds = 300;

    /// <summary>
    /// Divisor of the lifetime the platform stated for an access token that bounds the renewal
    /// margin: the margin takes at most half of that lifetime.
    /// </summary>
    public const int AccessTokenRenewalMarginLifetimeDivisor = 2;

    /// <summary>
    /// Error codes with which the platform says the access token of a call is no longer accepted:
    /// invalid credential, invalid access token, expired access token.
    /// </summary>
    public static readonly FrozenSet<int> AccessTokenRejectedCodes = new[] { 40001, 40014, 42001 }.ToFrozenSet();
}
