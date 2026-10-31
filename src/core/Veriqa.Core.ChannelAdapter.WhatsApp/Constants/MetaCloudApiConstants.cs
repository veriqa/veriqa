// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Constants;

/// <summary>
/// Meta Cloud API provider constants.
/// </summary>
internal static class MetaCloudApiConstants
{
    /// <summary>
    /// Graph API message-sending resource segment.
    /// </summary>
    public const string GraphMessagesResourceSegment = "messages";

    /// <summary>
    /// Meta Cloud API HTTP client name.
    /// </summary>
    public const string HttpClientName = "whatsapp-meta-cloud-api";

    /// <summary>
    /// Bearer authentication scheme.
    /// </summary>
    public const string BearerScheme = "Bearer";

    /// <summary>
    /// Content-type value for JSON.
    /// </summary>
    public const string ApplicationJsonContentType = "application/json";

    /// <summary>
    /// Graph API HTTP client timeout in seconds.
    /// </summary>
    public const int GraphApiTimeoutSeconds = 30;

    /// <summary>
    /// WhatsApp messaging product (used in the JSON payload).
    /// </summary>
    public const string MessagingProduct = "whatsapp";

    /// <summary>
    /// Outbound text message type.
    /// </summary>
    public const string OutboundTypeText = "text";

    /// <summary>
    /// Outbound interactive message type.
    /// </summary>
    public const string OutboundTypeInteractive = "interactive";

    /// <summary>
    /// Interactive message type with buttons.
    /// </summary>
    public const string InteractiveTypeButton = "button";

    /// <summary>
    /// Reply button type.
    /// </summary>
    public const string ReplyButtonType = "reply";

    /// <summary>
    /// Interactive message type — and the name of its only action — of a body with one button that opens
    /// a link.
    /// </summary>
    public const string InteractiveTypeCtaUrl = "cta_url";

    /// <summary>
    /// Interactive message type of a body with a button that opens a list of rows to pick from.
    /// </summary>
    public const string InteractiveTypeList = "list";

    /// <summary>
    /// Interactive message type — and the name of its only action — that asks the user to share the
    /// contact of their own account.
    /// </summary>
    public const string InteractiveTypeRequestContactInfo = "request_contact_info";

    /// <summary>
    /// Webhook signature header from Meta.
    /// </summary>
    public const string SignatureHeader = "X-Hub-Signature-256";

    /// <summary>
    /// Webhook signature prefix in the header.
    /// </summary>
    public const string SignaturePrefix = "sha256=";

    /// <summary>
    /// Query parameter key of the Hub Challenge mode.
    /// </summary>
    public const string HubModeQueryKey = "hub.mode";

    /// <summary>
    /// Query parameter key of the verify token in the Hub Challenge.
    /// </summary>
    public const string HubVerifyTokenQueryKey = "hub.verify_token";

    /// <summary>
    /// Query parameter key of the challenge in the Hub Challenge.
    /// </summary>
    public const string HubChallengeQueryKey = "hub.challenge";

    /// <summary>
    /// Expected value of the Hub Challenge mode.
    /// </summary>
    public const string HubSubscribeMode = "subscribe";

    /// <summary>
    /// WhatsApp Business webhook object type.
    /// </summary>
    public const string WebhookObjectType = "whatsapp_business_account";

    /// <summary>
    /// Value of the change field carrying messages.
    /// </summary>
    public const string WebhookMessagesField = "messages";

    /// <summary>
    /// Inbound text message type.
    /// </summary>
    public const string InboundTypeText = "text";

    /// <summary>
    /// Inbound message type of a shared contact card.
    /// </summary>
    public const string InboundTypeContacts = "contacts";

    /// <summary>
    /// <c>origin</c> of a contact the user shared by the request contact info button — the number of
    /// their own account (Meta, business-scoped user IDs). Any other value (<c>other</c>: a card sent by
    /// hand) or none is not proven to be the sender's.
    /// </summary>
    public const string ContactRequestOrigin = "contact_request";

    /// <summary>
    /// Bytes of the type of a shared contact message in a webhook (<c>"type":"contacts"</c>) — what the
    /// ownership answer looks for to claim a contact event, which carries no Veriqa marker. Not the bare
    /// <c>"contacts"</c> field name: every messages webhook carries a <c>contacts</c> list of the sender's
    /// profile. A substring match, so a false positive is possible and allowed by the ownership contract.
    /// </summary>
    public static ReadOnlySpan<byte> ContactsMessageMarker => "\"type\":\"contacts\""u8;
}
