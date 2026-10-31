// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Viber.Constants;

/// <summary>
/// Viber adapter constants: the string and numeric literals of the adapter itself. The wordings
/// of a message it shows the user are not among them: those belong to the message and are declared
/// with it (<see cref="Veriqa.Core.TransactionEngine.MessageTemplates.MessageTemplateNaturalKeys"/>).
/// </summary>
internal static class ViberAdapterConstants
{
    /// <summary>
    /// Named <see cref="System.Net.Http.IHttpClientFactory"/> client of the Bot API calls.
    /// </summary>
    public const string HttpClientName = "viber-bot-api";

    /// <summary>
    /// Timeout (seconds) of a Bot API call — an explicit bound instead of the default 100s,
    /// so a stuck request on the confirm/decline/expiry path fails fast rather than hanging.
    /// </summary>
    public const int BotApiTimeoutSeconds = 30;

    /// <summary>
    /// Adapter version stored in identity snapshots, in semver format.
    /// </summary>
    public const string AdapterVersion = "1.0.0";

    /// <summary>
    /// Sender name the messages of the bot are signed with unless the installation sets its own.
    /// </summary>
    public const string DefaultSenderName = "Veriqa";

    /// <summary>
    /// Maximum length of the sender name of a message: the platform limit is 28 characters, as the
    /// Bot API documents it.
    /// </summary>
    /// <remarks>
    /// The checks compare the number of UTF-16 code units, which is a conservative estimate: a name
    /// with characters outside the BMP (an emoji, for example) can be rejected before it reaches
    /// the limit of the platform.
    /// </remarks>
    public const int SenderNameMaxLength = 28;

    /// <summary>
    /// Header of an inbound webhook carrying the HMAC-SHA256 of its body in hexadecimal form.
    /// </summary>
    public const string SignatureHeader = "X-Viber-Content-Signature";

    /// <summary>
    /// Size (bytes) of a SHA-256 signature.
    /// </summary>
    public const int SignatureSizeBytes = 32;

    /// <summary>
    /// Header of a Bot API call carrying the authentication token of the bot.
    /// </summary>
    public const string AuthTokenHeader = "X-Viber-Auth-Token";

    /// <summary>
    /// Bot API path of an outbound message, relative to the API base address.
    /// </summary>
    public const string SendMessagePath = "/pa/send_message";

    /// <summary>
    /// Bot API path that sets or removes the webhook of the bot, relative to the API base address.
    /// </summary>
    public const string SetWebhookPath = "/pa/set_webhook";

    /// <summary>
    /// Value of the <c>status</c> member of a Bot API answer that stands for success.
    /// </summary>
    public const int StatusOk = 0;

    /// <summary>
    /// Event of a user opening the conversation with the bot — by a deep link among other ways.
    /// The platform delivers it only to a webhook that subscribed to it.
    /// </summary>
    public const string EventConversationStarted = "conversation_started";

    /// <summary>
    /// Event of a message a user sent to the bot. The platform delivers it to every webhook.
    /// </summary>
    public const string EventMessage = "message";

    /// <summary>
    /// Type of a text message — inbound and outbound.
    /// </summary>
    public const string MessageTypeText = "text";

    /// <summary>
    /// Type of an inbound message that carries a contact.
    /// </summary>
    public const string MessageTypeContact = "contact";

    /// <summary>
    /// Type of an inbound message that carries a link in its <c>media</c> member.
    /// </summary>
    public const string MessageTypeUrl = "url";

    /// <summary>
    /// Type of a custom keyboard attached to a message.
    /// </summary>
    public const string KeyboardType = "keyboard";

    /// <summary>
    /// Action of a keyboard button whose press sends the action body to the bot as a text message.
    /// </summary>
    public const string ActionTypeReply = "reply";

    /// <summary>
    /// Action of a keyboard button whose press opens the address of its action body. The platform also
    /// sends the address to the bot as a reply of the user, the keyboard documentation says.
    /// </summary>
    public const string ActionTypeOpenUrl = "open-url";

    /// <summary>
    /// Largest length of the caption of a keyboard button: 250 characters, as the keyboard
    /// documentation of the Viber Bot API states it. The same documentation names no limit for the
    /// action body of a button nor for the number of buttons, so neither is checked beforehand.
    /// </summary>
    public const int KeyboardButtonTextMaxLength = 250;

    /// <summary>
    /// Action of a keyboard button whose press shares the phone number of the user with the bot
    /// as a contact message.
    /// </summary>
    public const string ActionTypeSharePhone = "share-phone";

    /// <summary>
    /// Action body of the share-phone button. The platform requires the member to carry some text
    /// and hands none of it back: the press arrives as a contact.
    /// </summary>
    public const string SharePhoneActionBody = "share-phone";

    /// <summary>
    /// Minimal API version a client has to support for the share-phone button, as the keyboard
    /// documentation of the platform states it. It travels in <c>min_api_version</c> of the message
    /// that carries the button.
    /// </summary>
    public const int SharePhoneMinApiVersion = 3;

    /// <summary>
    /// Scheme and host of the deep link that opens the conversation with a bot.
    /// </summary>
    public const string DeepLinkBase = "viber://pa";

    /// <summary>
    /// Query parameter of the deep link naming the bot.
    /// </summary>
    public const string DeepLinkChatUriQueryKey = "chatURI";

    /// <summary>
    /// Query parameter of the deep link whose value the platform hands back in the
    /// conversation-started event.
    /// </summary>
    public const string DeepLinkContextQueryKey = "context";

    /// <summary>
    /// Confirm button text — a Natural Key resolved into the recipient's language before the send.
    /// </summary>
    public const string ConfirmButtonText = "Confirm ✅";

    /// <summary>
    /// Decline button text — a Natural Key resolved into the recipient's language before the send.
    /// </summary>
    public const string DeclineButtonText = "Decline ❌";
}
