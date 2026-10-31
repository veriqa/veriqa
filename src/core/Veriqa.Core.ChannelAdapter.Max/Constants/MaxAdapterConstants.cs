// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.ChannelAdapter.Max.Constants;

/// <summary>
/// MAX adapter constants (SPEC-003 §11).
/// Contains the string and numeric literals of the adapter itself. The wordings of a message it
/// shows the user are not among them: those belong to the message and are declared with it
/// (<see cref="Veriqa.Core.TransactionEngine.MessageTemplates.MessageTemplateNaturalKeys"/>).
/// </summary>
internal static class MaxAdapterConstants
{
    /// <summary>
    /// Adapter version in semver format.
    /// </summary>
    public const string AdapterVersion = "1.0.0";

    /// <summary>
    /// HTTP header of the MAX Bot API webhook secret token.
    /// </summary>
    public const string WebhookSecretHeader = "X-Max-Bot-Api-Secret";

    /// <summary>
    /// Webhook path relative to the base URL.
    /// </summary>
    public const string WebhookPath = "/api/channels/max/webhook";

    /// <summary>
    /// Deep link parameter separator.
    /// </summary>
    public const string DeepLinkStartParam = "?start=";

    /// <summary>
    /// Deep link prefix for authentication (matches <see cref="CallbackDataPrefixes.Auth"/>).
    /// </summary>
    public const string DeepLinkPrefix = CallbackDataPrefixes.Auth;

    /// <summary>
    /// The at-sign a bot username is shown to the user with; it is not part of the username itself
    /// and has no place in a deep link.
    /// </summary>
    public const char BotUsernamePrefix = '@';

    /// <summary>
    /// Confirm button text.
    /// </summary>
    public const string ConfirmButtonText = "Confirm ✅";

    /// <summary>
    /// Decline button text.
    /// </summary>
    public const string DeclineButtonText = "Decline ❌";

    /// <summary>
    /// Largest address of a <c>link</c> button, in characters. Named by the platform: the official MAX
    /// Bot API documentation, section "Keyboard" (button of type <c>link</c>).
    /// </summary>
    public const int LinkButtonUrlMaxLength = 2048;

    /// <summary>
    /// Maximum raw metadata size in bytes (CA-005). Alias of the contract-wide
    /// <see cref="ChannelAdapterLimits.MaxRawMetadataSize"/> — the limit is identical for every channel,
    /// so this class does not carry its own literal.
    /// </summary>
    public const int MaxRawMetadataSize = ChannelAdapterLimits.MaxRawMetadataSize;

    /// <summary>
    /// Base delay between retried API calls on errors (in milliseconds).
    /// </summary>
    public const int RetryBaseDelayMs = 1000;

    /// <summary>
    /// Timeout of the <c>GET /updates</c> long-poll request (in seconds) — how long the platform holds the
    /// request open when it has no update to give.
    /// </summary>
    public const int LongPollTimeoutSeconds = 30;

    /// <summary>
    /// Maximum number of updates per one <c>GET /updates</c> long-poll request.
    /// </summary>
    public const int LongPollUpdatesLimit = 100;

    /// <summary>
    /// Long-poll API path (<c>GET /updates</c>). Requested as a raw call of the platform client rather than
    /// through its typed method, whose model drops the signature of a shared contact.
    /// </summary>
    public const string UpdatesPath = "/updates";

    /// <summary>
    /// Query parameter of <c>GET /updates</c>: the largest number of updates in the answer.
    /// </summary>
    public const string UpdatesLimitQueryParam = "limit";

    /// <summary>
    /// Query parameter of <c>GET /updates</c>: the long-poll timeout in seconds.
    /// </summary>
    public const string UpdatesTimeoutQueryParam = "timeout";

    /// <summary>
    /// Query parameter of <c>GET /updates</c>: the position of the next expected update, as the previous
    /// answer named it.
    /// </summary>
    public const string UpdatesMarkerQueryParam = "marker";

    /// <summary>
    /// Field of the answer of <c>GET /updates</c> holding the array of updates.
    /// </summary>
    public const string UpdatesResponseUpdatesField = "updates";

    /// <summary>
    /// Field of the answer of <c>GET /updates</c> holding the position of the next expected update.
    /// </summary>
    public const string UpdatesResponseMarkerField = "marker";

    /// <summary>
    /// Message edit/send API path (PUT /messages), resolved against the installation's address
    /// (<see cref="Veriqa.Core.ChannelAdapter.Max.Configuration.MaxOptions.ApiBaseUrl"/>). Used for the
    /// direct keyboard-clearing edit (attachments: []) that the SDK message builder cannot express.
    /// </summary>
    public const string EditMessagePath = "/messages";

    /// <summary>
    /// Query parameter carrying the id of the message being edited.
    /// </summary>
    public const string MessageIdQueryParam = "message_id";

    /// <summary>
    /// Authorization header carrying the raw bot token (MAX auth is a header, not a query parameter).
    /// </summary>
    public const string AuthorizationHeaderName = "Authorization";

    /// <summary>
    /// Media type of the MAX Bot API request body.
    /// </summary>
    public const string JsonMediaType = "application/json";

    /// <summary>
    /// Named <see cref="System.Net.Http.IHttpClientFactory"/> client for the direct MAX Bot API edit call.
    /// </summary>
    public const string HttpClientName = "max-bot-api";

    /// <summary>
    /// Timeout (seconds) of the direct MAX Bot API edit call — an explicit bound instead of the default
    /// 100s, so a stuck request on the confirm/decline/expiry path fails fast rather than hanging.
    /// </summary>
    public const int EditMessageTimeoutSeconds = 30;

    /// <summary>
    /// Separator of the parts packed into the opaque inbound token: the callback identifier and the
    /// identifier of the message the button belongs to. Neither part can contain it — MAX
    /// callback identifiers and message ids never carry it.
    /// </summary>
    /// <remarks>
    /// The layout is the adapter's own business: the core carries the token back untouched and never
    /// parses it. The message id is packed here on purpose — the callback identifier exists only
    /// inside the inbound event, and the message id must survive a process restart or a
    /// different instance, where an in-process prompt store would have nothing to offer (CA-142).
    /// The chat is deliberately not packed: a MAX edit is addressed by the message id alone, so a
    /// chat slot would hold a value nothing ever reads.
    /// </remarks>
    public const string InboundTokenSeparator = "|";

    /// <summary>
    /// Number of parts in a well-formed inbound token. Only the first one (the callback identifier)
    /// is always filled; the message-id slot is empty when the press carried no source message.
    /// </summary>
    public const int InboundTokenPartCount = 2;

    /// <summary>
    /// Notification sent when answering a callback (<c>POST /answers</c>). The MAX Bot API rejects an
    /// answer that carries neither <c>message</c> nor <c>notification</c> (<c>proto.payload</c>); an empty
    /// notification satisfies it and clears the button's loading indicator without showing any text.
    /// </summary>
    public const string SilentCallbackNotification = "";

    /// <summary>
    /// Bytes of the attachment type of a shared contact in a MAX update (<c>"type": "contact"</c> of an
    /// element of <c>message.body.attachments</c>) — what the ownership answer looks for to claim a contact
    /// event, which carries no Veriqa marker. A substring match, so a user's own text with this word in
    /// quotes also matches: the ownership contract allows such a false positive.
    /// </summary>
    public static ReadOnlySpan<byte> ContactAttachmentMarker => "\"contact\""u8;

    /// <summary>
    /// Wire value of the attachment type of a shared contact.
    /// </summary>
    public const string ContactAttachmentType = "contact";

    /// <summary>
    /// Fields of a <c>message_created</c> update that shares a contact: <c>message</c> → <c>sender</c>,
    /// <c>recipient</c> → <c>chat_type</c>, and <c>body</c> → <c>attachments</c>, then <c>type</c> and
    /// <c>payload</c> of each element, with <c>vcf_info</c> and the signature <c>hash</c> inside the
    /// payload of a contact (MAX Bot API, keyboard use case, read 2026-09-29); <c>user_locale</c> at the
    /// root. The shipped <c>Max.BotClient</c> model leaves the message of this update empty and does not
    /// carry <c>hash</c> at all, so such an update is read off the raw body.
    /// </summary>
    public const string UpdateMessageField = "message";

    /// <summary>
    /// Field of the locale of the update, at its root (see <see cref="UpdateMessageField"/>).
    /// </summary>
    public const string UpdateUserLocaleField = "user_locale";

    /// <summary>
    /// Field of the user who sent a MAX message (see <see cref="UpdateMessageField"/>).
    /// </summary>
    public const string MessageSenderField = "sender";

    /// <summary>
    /// Field of the recipient of a MAX message — the chat and, in a dialog, the bot, never the sender
    /// (see <see cref="UpdateMessageField"/>).
    /// </summary>
    public const string MessageRecipientField = "recipient";

    /// <summary>
    /// Field of the kind of the chat inside the recipient of a MAX message (see <see cref="UpdateMessageField"/>).
    /// </summary>
    public const string RecipientChatTypeField = "chat_type";

    /// <summary>
    /// Field of the message body inside a MAX message (see <see cref="UpdateMessageField"/>).
    /// </summary>
    public const string MessageBodyField = "body";

    /// <summary>
    /// Field of the message id inside a MAX message body (<c>message.body.mid</c>) — of a
    /// <c>message_callback</c> update, the message the pressed button belongs to.
    /// </summary>
    public const string MessageBodyIdField = "mid";

    /// <summary>
    /// Field of the attachments inside a MAX message body (see <see cref="UpdateMessageField"/>).
    /// </summary>
    public const string BodyAttachmentsField = "attachments";

    /// <summary>
    /// Field of the attachment type (see <see cref="UpdateMessageField"/>).
    /// </summary>
    public const string AttachmentTypeField = "type";

    /// <summary>
    /// Field of the attachment payload (see <see cref="UpdateMessageField"/>).
    /// </summary>
    public const string AttachmentPayloadField = "payload";

    /// <summary>
    /// Field of the vCard of a contact (see <see cref="UpdateMessageField"/>).
    /// </summary>
    public const string ContactVcfInfoField = "vcf_info";

    /// <summary>
    /// Field of the contact signature: HMAC-SHA256 of <c>vcf_info</c> keyed with the bot token
    /// (see <see cref="UpdateMessageField"/>).
    /// </summary>
    public const string ContactHashField = "hash";

    /// <summary>
    /// vCard property of a phone number.
    /// </summary>
    public const string VCardPhoneProperty = "TEL";

    /// <summary>
    /// URI scheme a vCard 4.0 phone value may be written with (<c>TEL;VALUE=uri:tel:+1...</c>).
    /// </summary>
    public const string VCardPhoneUriScheme = "tel:";

    /// <summary>
    /// A vCard line break as it reads when it arrives escaped once more than JSON unescapes — the
    /// vendor asks for such breaks to be restored before hashing.
    /// </summary>
    public const string EscapedVCardLineBreak = "\\r\\n";

    /// <summary>
    /// A vCard line break.
    /// </summary>
    public const string VCardLineBreak = "\r\n";

    /// <summary>
    /// A bare line feed some vCard writers break lines with.
    /// </summary>
    public const string VCardBareLineBreak = "\n";
}
