// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Messenger.Constants;

/// <summary>
/// Messenger adapter constants: the string and numeric literals of the adapter itself. The wordings
/// of a message it shows the user are not among them: those belong to the message and are declared
/// with it (<see cref="Veriqa.Core.TransactionEngine.MessageTemplates.MessageTemplateNaturalKeys"/>).
/// </summary>
internal static class MessengerAdapterConstants
{
    /// <summary>
    /// Named <see cref="System.Net.Http.IHttpClientFactory"/> client of the Graph API calls.
    /// </summary>
    public const string HttpClientName = "messenger-graph-api";

    /// <summary>
    /// Timeout (seconds) of a Graph API call — an explicit bound instead of the default 100s,
    /// so a stuck request on the confirm/decline/expiry path fails fast rather than hanging.
    /// </summary>
    public const int GraphApiTimeoutSeconds = 30;

    /// <summary>
    /// Prefix a Graph API version starts with (<c>v26.0</c>).
    /// </summary>
    public const string GraphApiVersionPrefix = "v";

    /// <summary>
    /// The at-sign a Page username is commonly written with; it is not part of the username itself
    /// and has no place in an <c>m.me</c> link.
    /// </summary>
    public const char PageNamePrefix = '@';

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
    /// Base URL of an <c>m.me</c> link that opens the Messenger thread with a Page.
    /// </summary>
    public const string DeepLinkBaseUrl = "https://m.me/";

    /// <summary>
    /// Query parameter of an <c>m.me</c> link whose value Messenger hands back in the referral event.
    /// </summary>
    public const string DeepLinkReferralQueryKey = "ref";

    /// <summary>
    /// Authorization scheme of a Graph API call.
    /// </summary>
    public const string BearerScheme = "Bearer";

    /// <summary>
    /// Resource segment of the Send API under the sending Page
    /// (<c>/{version}/{page-id}/messages</c>).
    /// </summary>
    public const string MessagesResourceSegment = "messages";

    /// <summary>
    /// Messaging type of a message sent in answer to what the user did in the thread — the only
    /// kind this adapter sends.
    /// </summary>
    public const string MessagingTypeResponse = "RESPONSE";

    /// <summary>
    /// Query parameter of a Graph API read naming the fields to return.
    /// </summary>
    public const string FieldsQueryKey = "fields";

    /// <summary>
    /// Fields of the user profile read on a sign-in step: the name, the picture and the locale of
    /// the person behind the page-scoped ID. The locale takes a permission of its own, and a read
    /// the platform refused is repeated with <see cref="ProfileFieldsWithoutLocale"/>.
    /// </summary>
    public const string ProfileFields = "first_name,last_name,profile_pic,locale";

    /// <summary>
    /// Fields of the user profile read when the read with the locale was refused.
    /// </summary>
    public const string ProfileFieldsWithoutLocale = "first_name,last_name,profile_pic";

    /// <summary>
    /// How long (minutes) the profiles of a tenant are read without the locale after the platform
    /// refused the read with it and granted the one without it.
    /// </summary>
    public const int LocaleRefusalHoldMinutes = 60;

    /// <summary>
    /// Separator of the language and the region in a locale as the Graph API writes it (<c>en_US</c>).
    /// </summary>
    public const char GraphLocaleSeparator = '_';

    /// <summary>
    /// Separator of the language and the region in a BCP 47 tag (<c>en-US</c>).
    /// </summary>
    public const char Bcp47LocaleSeparator = '-';

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
    /// Maximum length (characters) of the title of a URL or a postback button, as Meta documents it
    /// for the Messenger Platform (Buttons — URL button, postback button); a longer title is cut.
    /// </summary>
    public const int ButtonTitleLimit = 20;

    /// <summary>
    /// Maximum length (characters) of the payload of a postback button, as Meta documents it for the
    /// Messenger Platform (Buttons — postback button); a button with a longer payload is not drawn.
    /// </summary>
    public const int PostbackPayloadLimit = 1000;

    /// <summary>
    /// Mark a text cut to a platform limit ends with.
    /// </summary>
    public const string TruncationMark = "…";
}
