// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Constants;

/// <summary>
/// Twilio provider constants: the REST resources of the Messages API, the form fields of an outbound message
/// and of an inbound webhook, the webhook signature header and the <c>twilio/quick-reply</c> limits.
/// </summary>
internal static class TwilioConstants
{
    /// <summary>
    /// Twilio HTTP client name.
    /// </summary>
    public const string HttpClientName = "whatsapp-twilio";

    /// <summary>
    /// Twilio REST API HTTP client timeout in seconds.
    /// </summary>
    public const int ApiTimeoutSeconds = 30;

    /// <summary>
    /// Path of the accounts collection of the Twilio REST API (version <c>2010-04-01</c>); the account
    /// identifier follows it.
    /// </summary>
    public const string AccountsPath = "/2010-04-01/Accounts/";

    /// <summary>
    /// Suffix of the account resource after the account identifier.
    /// </summary>
    public const string AccountResourceSuffix = ".json";

    /// <summary>
    /// Suffix of the messages resource of an account after the account identifier.
    /// </summary>
    public const string MessagesResourceSuffix = "/Messages.json";

    /// <summary>
    /// Basic authentication scheme (<c>AccountSid:AuthToken</c>).
    /// </summary>
    public const string BasicScheme = "Basic";

    /// <summary>
    /// Separator of the user name and the password of Basic authentication.
    /// </summary>
    public const char BasicCredentialsSeparator = ':';

    /// <summary>
    /// Prefix Twilio puts before a WhatsApp address — a phone number or a business-scoped user ID.
    /// </summary>
    public const string WhatsAppAddressPrefix = "whatsapp:";

    /// <summary>
    /// Form field of the sender address (outbound and inbound).
    /// </summary>
    public const string FromField = "From";

    /// <summary>
    /// Form field of the recipient address of an outbound message.
    /// </summary>
    public const string ToField = "To";

    /// <summary>
    /// Form field of the text body (outbound and inbound).
    /// </summary>
    public const string BodyField = "Body";

    /// <summary>
    /// Form field of the content template of an outbound message.
    /// </summary>
    public const string ContentSidField = "ContentSid";

    /// <summary>
    /// Form field of the JSON object of the content template variables.
    /// </summary>
    public const string ContentVariablesField = "ContentVariables";

    /// <summary>
    /// Kind of an outbound plain text message, for the log.
    /// </summary>
    public const string OutboundKindText = "text";

    /// <summary>
    /// Kind of an outbound quick-reply prompt, for the log.
    /// </summary>
    public const string OutboundKindQuickReply = "quick-reply";

    /// <summary>
    /// Inbound webhook field of the message identifier.
    /// </summary>
    public const string MessageSidField = "MessageSid";

    /// <summary>
    /// Inbound webhook field of the sender's WhatsApp profile name.
    /// </summary>
    public const string ProfileNameField = "ProfileName";

    /// <summary>
    /// Inbound webhook field of the sender's business-scoped user ID, with the <c>whatsapp:</c> prefix.
    /// </summary>
    public const string ExternalUserIdField = "ExternalUserId";

    /// <summary>
    /// Inbound webhook field of the sender's WhatsApp username.
    /// </summary>
    public const string UsernameField = "Username";

    /// <summary>
    /// Inbound webhook field of the id of a pressed quick-reply button.
    /// </summary>
    public const string ButtonPayloadField = "ButtonPayload";

    /// <summary>
    /// Header of the inbound webhook signature.
    /// </summary>
    public const string SignatureHeader = "X-Twilio-Signature";

    /// <summary>
    /// Size of an HMAC-SHA1 signature in bytes.
    /// </summary>
    public const int SignatureSizeBytes = 20;

    /// <summary>
    /// Property of the account status in the account resource.
    /// </summary>
    public const string AccountStatusProperty = "status";

    /// <summary>
    /// Status of an account that can send messages.
    /// </summary>
    public const string AccountStatusActive = "active";

    /// <summary>
    /// Property of the Twilio error code in an error response.
    /// </summary>
    public const string ErrorCodeProperty = "code";

    /// <summary>
    /// <c>twilio/quick-reply</c> variable of the prompt body.
    /// </summary>
    public const string QuickReplyBodyVariable = "1";

    /// <summary>
    /// <c>twilio/quick-reply</c> variable of the confirm button title.
    /// </summary>
    public const string QuickReplyConfirmTitleVariable = "2";

    /// <summary>
    /// <c>twilio/quick-reply</c> variable of the confirm button id.
    /// </summary>
    public const string QuickReplyConfirmIdVariable = "3";

    /// <summary>
    /// <c>twilio/quick-reply</c> variable of the decline button title.
    /// </summary>
    public const string QuickReplyDeclineTitleVariable = "4";

    /// <summary>
    /// <c>twilio/quick-reply</c> variable of the decline button id.
    /// </summary>
    public const string QuickReplyDeclineIdVariable = "5";

    /// <summary>
    /// Maximum length of the <c>twilio/quick-reply</c> body, in characters.
    /// </summary>
    public const int QuickReplyBodyMaxLength = 1024;

    /// <summary>
    /// Maximum length of a <c>twilio/quick-reply</c> button title, in characters.
    /// </summary>
    public const int QuickReplyButtonTitleMaxLength = 20;
}
