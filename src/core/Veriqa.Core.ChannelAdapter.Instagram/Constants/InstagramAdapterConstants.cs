// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Instagram.Constants;

/// <summary>
/// Instagram adapter constants: the string and numeric literals of the adapter itself. The wordings
/// of a message it shows the user are not among them: those belong to the message and are declared
/// with it (<see cref="Veriqa.Core.TransactionEngine.MessageTemplates.MessageTemplateNaturalKeys"/>).
/// </summary>
internal static class InstagramAdapterConstants
{
    /// <summary>
    /// Named <see cref="System.Net.Http.IHttpClientFactory"/> client of the Instagram API calls.
    /// </summary>
    public const string HttpClientName = "instagram-graph-api";

    /// <summary>
    /// Timeout (seconds) of an Instagram API call — an explicit bound instead of the default 100s,
    /// so a stuck request on the confirm/decline/expiry path fails fast rather than hanging.
    /// </summary>
    public const int GraphApiTimeoutSeconds = 30;

    /// <summary>
    /// Prefix a Graph API version starts with (<c>v26.0</c>).
    /// </summary>
    public const string GraphApiVersionPrefix = "v";

    /// <summary>
    /// The at-sign a username is commonly written with; it is not part of the username itself and
    /// has no place in an <c>ig.me</c> link.
    /// </summary>
    public const char UsernamePrefix = '@';

    /// <summary>
    /// Adapter version stored in identity snapshots, in semver format.
    /// </summary>
    public const string AdapterVersion = "1.0.0";

    /// <summary>
    /// Confirm button text — a Natural Key resolved into the recipient's language before the send.
    /// </summary>
    public const string ConfirmButtonText = "Confirm ✅";

    /// <summary>
    /// Decline button text — a Natural Key resolved into the recipient's language before the send.
    /// </summary>
    public const string DeclineButtonText = "Decline ❌";

    /// <summary>
    /// Base URL of an <c>ig.me</c> link that opens the Direct thread with an account.
    /// </summary>
    public const string DeepLinkBaseUrl = "https://ig.me/m/";

    /// <summary>
    /// Query parameter of an <c>ig.me</c> link whose value Instagram hands back in the referral event.
    /// </summary>
    public const string DeepLinkReferralQueryKey = "ref";

    /// <summary>
    /// Authorization scheme of an Instagram API call.
    /// </summary>
    public const string BearerScheme = "Bearer";

    /// <summary>
    /// Resource segment of the Send API under the sending account
    /// (<c>/{version}/{account-id}/messages</c>).
    /// </summary>
    public const string MessagesResourceSegment = "messages";

    /// <summary>
    /// Query parameter of a Graph API read naming the fields to return.
    /// </summary>
    public const string FieldsQueryKey = "fields";

    /// <summary>
    /// Fields of the user profile read on a sign-in step: the name, the username and the picture
    /// of the person behind the Instagram-scoped ID.
    /// </summary>
    public const string ProfileFields = "name,username,profile_pic";

    /// <summary>
    /// Attachment type of a templated message.
    /// </summary>
    public const string AttachmentTypeTemplate = "template";

    /// <summary>
    /// Template type of a text with buttons under it.
    /// </summary>
    public const string TemplateTypeButton = "button";

    /// <summary>
    /// Button type whose press is delivered back as a postback event carrying the button payload.
    /// </summary>
    public const string ButtonTypePostback = "postback";

    /// <summary>
    /// Maximum length (characters) of the text of a button template, as Meta documents it.
    /// </summary>
    public const int ButtonTemplateTextLimit = 640;

    /// <summary>
    /// Maximum size (bytes of UTF-8) of the text of a text message: "Message text must be UTF-8 and be
    /// a 1000 bytes or less", as the Messaging API of the Instagram API with Instagram Login documents
    /// it. The limit counts bytes, not characters, so a text of characters outside ASCII fits fewer
    /// of them.
    /// </summary>
    public const int TextMessageByteLimit = 1000;

    /// <summary>
    /// Mark a text cut to a platform limit ends with.
    /// </summary>
    public const string TruncationMark = "\u2026";
}
