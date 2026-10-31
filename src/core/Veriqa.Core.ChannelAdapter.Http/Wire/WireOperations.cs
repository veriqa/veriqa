// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Veriqa.Core.ChannelAdapter.Http.Wire;

/// <summary>
/// Payload of <c>send-prompt</c> (SPEC-042 C34).
/// </summary>
internal sealed class WireSendPromptPayload
{
    /// <summary>
    /// Identifier of the transaction the prompt asks about.
    /// </summary>
    public required string TransactionId { get; init; }

    /// <summary>
    /// Delivery address of the recipient in the channel.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// Structured context of the confirmation; the adapter may render from it on its own.
    /// </summary>
    public required WirePrompt Prompt { get; init; }

    /// <summary>
    /// The prompt as the core rendered it (SPEC-042 L46).
    /// </summary>
    public required WireRenderedPrompt Rendered { get; init; }
}

/// <summary>
/// Structured context of the confirmation in the fullness of the confirmation context of the core
/// (SPEC-042 C34), one derived type per <c>variant</c>. Raw IP address and User-Agent are not part of
/// any variant (ICC-040).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "variant")]
[JsonDerivedType(typeof(WireDetailedPrompt), WireValues.PromptVariants.Detailed)]
[JsonDerivedType(typeof(WireSubjectPrompt), WireValues.PromptVariants.Subject)]
[JsonDerivedType(typeof(WireSuppressedPrompt), WireValues.PromptVariants.Suppressed)]
internal abstract class WirePrompt
{
    /// <summary>
    /// Owners of the transaction.
    /// </summary>
    public required WireOwnership Ownership { get; init; }

    /// <summary>
    /// Language of the recipient; <c>null</c> — unknown.
    /// </summary>
    public string? RecipientLocale { get; init; }

    /// <summary>
    /// Time zone of the recipient; <c>null</c> — unknown.
    /// </summary>
    public string? RecipientTimeZone { get; init; }

    /// <summary>
    /// Brand name the levels of the transaction state, as configured — a Natural Key; <c>null</c> — no
    /// level states one. Every variant carries it, as every variant of the context does.
    /// </summary>
    public string? BrandName { get; init; }
}

/// <summary>
/// The <c>detailed</c> variant: the details of the initiator of the sign-in.
/// </summary>
internal sealed class WireDetailedPrompt : WirePrompt
{
    /// <summary>
    /// Name of the client application that initiated the sign-in.
    /// </summary>
    public required string ClientApplicationName { get; init; }

    /// <summary>
    /// Browser of the initiator; <c>null</c> — unknown.
    /// </summary>
    public string? Browser { get; init; }

    /// <summary>
    /// Operating system of the initiator; <c>null</c> — unknown.
    /// </summary>
    public string? OsPlatform { get; init; }

    /// <summary>
    /// Device type of the initiator; <c>null</c> — unknown.
    /// </summary>
    public string? DeviceType { get; init; }

    /// <summary>
    /// Country of the initiator; <c>null</c> — unknown.
    /// </summary>
    public string? GeoCountry { get; init; }

    /// <summary>
    /// City of the initiator; <c>null</c> — unknown.
    /// </summary>
    public string? GeoCity { get; init; }

    /// <summary>
    /// Moment the sign-in was initiated, in UTC.
    /// </summary>
    public required DateTimeOffset InitiatedAt { get; init; }

    /// <summary>
    /// Moment the transaction was created, in UTC — the time of the request the shipped question shows;
    /// <c>null</c> — the context states none.
    /// </summary>
    public DateTimeOffset? RequestedAt { get; init; }

    /// <summary>
    /// Whether the initiation looks anomalous.
    /// </summary>
    public required bool Anomaly { get; init; }

    /// <summary>
    /// Natural Key of the reason of the anomaly; <c>null</c> — a flag without a reason.
    /// </summary>
    public string? AnomalyReasonKey { get; init; }

    /// <summary>
    /// Channel snapshot of the account the question is asked of, in the shape the adapter posts an
    /// identity in its events; <c>null</c> — no account is known. Carried whole: the masks of the
    /// identity slots are applied where the slot values are assembled, as for an adapter in the core.
    /// </summary>
    public WireIdentity? ChannelIdentity { get; init; }

    /// <summary>
    /// Name of the channel the question is asked in, in the language of the text; <c>null</c> — the
    /// channel is not known.
    /// </summary>
    public string? ChannelName { get; init; }

    /// <summary>
    /// Name under which the user sees the chat of the channel; <c>null</c> — the channel states none.
    /// </summary>
    public string? BotName { get; init; }
}

/// <summary>
/// The <c>subject</c> variant: the subject text of a confirmation transaction.
/// </summary>
internal sealed class WireSubjectPrompt : WirePrompt
{
    /// <summary>
    /// Text of the subject, shown as it is.
    /// </summary>
    public required string PromptText { get; init; }
}

/// <summary>
/// The <c>suppressed</c> variant: no details.
/// </summary>
internal sealed class WireSuppressedPrompt : WirePrompt
{
    /// <summary>
    /// Why the details are absent — a value of <see cref="WireValues.SuppressionReasons"/>; a diagnostic
    /// that does not change what is shown.
    /// </summary>
    public required string Reason { get; init; }
}

/// <summary>
/// Owners of the transaction of a prompt (SPEC-042 C34). Every field may be <c>null</c>.
/// </summary>
internal sealed class WireOwnership
{
    /// <summary>
    /// Tenant of the transaction.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Application of the transaction.
    /// </summary>
    public string? ApplicationId { get; init; }

    /// <summary>
    /// Selector of the UI configuration of the transaction.
    /// </summary>
    public string? UiConfigSelector { get; init; }

    /// <summary>
    /// Scopes the relying party requested.
    /// </summary>
    public IReadOnlyList<string>? RequestedScopes { get; init; }

    /// <summary>
    /// ACR values the relying party requested.
    /// </summary>
    public IReadOnlyList<string>? AcrValues { get; init; }

    /// <summary>
    /// User the transaction is bound to.
    /// </summary>
    public string? UserId { get; init; }
}

/// <summary>
/// The prompt as the core rendered it (SPEC-042 C34, L46): plain text, escaping for the markup of the
/// platform is the adapter's.
/// </summary>
internal sealed class WireRenderedPrompt
{
    /// <summary>
    /// Text of the question.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Caption of the confirm button.
    /// </summary>
    public required string ConfirmLabel { get; init; }

    /// <summary>
    /// Caption of the decline button.
    /// </summary>
    public required string DeclineLabel { get; init; }

    /// <summary>
    /// Language the text is rendered in.
    /// </summary>
    public required string Locale { get; init; }
}

/// <summary>
/// Result of <c>send-prompt</c> and <c>send-message</c> (SPEC-042 C34, C35).
/// </summary>
internal sealed class WireMessageResult
{
    /// <summary>
    /// The sent message; <c>null</c> — the channel has no addressable reference for it (CA-182).
    /// </summary>
    public WireMessageRef? Message { get; init; }
}

/// <summary>
/// Coordinates of a message in the channel (SPEC-042 C34, C36).
/// </summary>
internal sealed class WireMessageRef
{
    /// <summary>
    /// Conversation of the message.
    /// </summary>
    public required string ChatId { get; init; }

    /// <summary>
    /// Message within the conversation.
    /// </summary>
    public required string MessageId { get; init; }
}

/// <summary>
/// Payload of <c>send-message</c> (SPEC-042 C35).
/// </summary>
internal sealed class WireSendMessagePayload
{
    /// <summary>
    /// Delivery address of the recipient in the channel.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// Text of the message.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Kind of the content — always one of the kinds the registration declares.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Content of the request for the phone number: present exactly when <see cref="Kind"/> is the
    /// phone-number request, <c>null</c> for every other kind (SPEC-042 C35).
    /// </summary>
    public WirePhoneNumberRequest? PhoneNumberRequest { get; init; }

    /// <summary>
    /// Content of the link to the claim completion form: present exactly when <see cref="Kind"/> is the
    /// link to the form, <c>null</c> for every other kind (SPEC-042 C35).
    /// </summary>
    public WireClaimFormLink? ClaimFormLink { get; init; }

    /// <summary>
    /// Buttons of an action message: present exactly when <see cref="Kind"/> is the action buttons,
    /// <c>null</c> for every other kind (SPEC-042 C35).
    /// </summary>
    public WireActionButtons? ActionButtons { get; init; }
}

/// <summary>
/// Content of the link to the claim completion form of <c>send-message</c> (SPEC-042 C35) in the
/// fullness of the content the core hands a built-in channel: one button under the text that opens the
/// form. The address carries the form token — the right to act on the form — so it is a secret the
/// adapter never writes to a log.
/// </summary>
internal sealed class WireClaimFormLink
{
    /// <summary>
    /// Absolute https URL of the form page, the form token included.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Caption of the button that opens the form, localized by the core.
    /// </summary>
    public required string ButtonLabel { get; init; }
}

/// <summary>
/// Buttons of an action message of <c>send-message</c> (SPEC-042 C35) in the fullness of the content
/// the core hands a built-in channel, drawn under the text.
/// </summary>
internal sealed class WireActionButtons
{
    /// <summary>
    /// Buttons of the message, never empty; the order of the list is the order they are displayed in.
    /// </summary>
    public required IReadOnlyList<WireActionButton> Buttons { get; init; }
}

/// <summary>
/// One button of an action message (SPEC-042 C35): a link the platform opens, or an item of the menu
/// whose press the adapter posts back as a <c>menu_item_pressed</c> event. Exactly one of
/// <see cref="Url"/> and <see cref="MenuItemValue"/> is not <c>null</c>.
/// </summary>
internal sealed class WireActionButton
{
    /// <summary>
    /// Caption of the button, localized by the core.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Address a link button opens; <c>null</c> for an item of the menu. The address may carry a
    /// single-use handle of the transaction, so it is a secret the adapter never writes to a log.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Value of an item of the menu — what a <c>menu_item_pressed</c> event answering the press carries
    /// back; <c>null</c> for a link button. The ownership marker of the core's own payloads is not part
    /// of it: the adapter builds the payload of the platform button itself (SPEC-042 R3).
    /// </summary>
    public string? MenuItemValue { get; init; }
}

/// <summary>
/// Content of the request for the phone number of <c>send-message</c> (SPEC-042 C35) in the fullness
/// of the content the core hands a built-in channel: what the adapter needs to offer sharing the
/// number and the secondary action of the step. How the adapter shows the two is its own choice.
/// </summary>
internal sealed class WirePhoneNumberRequest
{
    /// <summary>
    /// Identifier of the transaction the phone step belongs to — what a <c>phone_step_action</c> event
    /// answering the request names.
    /// </summary>
    public required string TransactionId { get; init; }

    /// <summary>
    /// Caption of the button that shares the phone number, localized by the core.
    /// </summary>
    public required string ShareButtonLabel { get; init; }

    /// <summary>
    /// The secondary action of the step — a value of <see cref="WireValues.PhoneStepActions"/>.
    /// </summary>
    public required string SecondaryAction { get; init; }

    /// <summary>
    /// Caption of the button of the secondary action, localized by the core.
    /// </summary>
    public required string SecondaryButtonLabel { get; init; }
}

/// <summary>
/// Payload of <c>report-outcome</c> (SPEC-042 C36).
/// </summary>
internal sealed class WireReportOutcomePayload
{
    /// <summary>
    /// Identifier of the transaction.
    /// </summary>
    public required string TransactionId { get; init; }

    /// <summary>
    /// Outcome — a value of <see cref="WireValues.Outcomes"/>.
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>
    /// Delivery address of the recipient in the channel.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// Status text, localized by the core.
    /// </summary>
    public required string StatusText { get; init; }

    /// <summary>
    /// Opaque token of the interaction the adapter handed over; <c>null</c> — none (CA-184).
    /// </summary>
    public string? InboundToken { get; init; }

    /// <summary>
    /// The prompt of the transaction in this channel; <c>null</c> — none.
    /// </summary>
    public WireMessageRef? PromptMessage { get; init; }

    /// <summary>
    /// The message that asked the user for a phone number, when the step it belongs to is over;
    /// <c>null</c> — none. Stated with a terminal outcome only.
    /// </summary>
    public WireMessageRef? PhoneRequestMessage { get; init; }

    /// <summary>
    /// How the outcome should appear — a value of <see cref="WireValues.DisplayIntents"/>.
    /// </summary>
    public required string DisplayIntent { get; init; }
}

/// <summary>
/// Result of <c>report-outcome</c> (SPEC-042 C36).
/// </summary>
internal sealed class WireReportOutcomeResult
{
    /// <summary>
    /// <c>true</c> — the outcome is shown; <c>false</c> — the channel deliberately showed nothing.
    /// </summary>
    public required bool Shown { get; init; }
}

/// <summary>
/// Payload of <c>deep-link</c> (SPEC-042 C37).
/// </summary>
internal sealed class WireDeepLinkPayload
{
    /// <summary>
    /// Identifier of the transaction.
    /// </summary>
    public required string TransactionId { get; init; }

    /// <summary>
    /// Where the link is going to be used — a value of <see cref="WireValues.LinkPurposes"/>. The core
    /// always sends it; an adapter that does not know the field ignores it.
    /// </summary>
    public required string Purpose { get; init; }

    /// <summary>
    /// The text to prefill the link with, as the core rendered it from the <c>deeplink-prefill</c>
    /// template of the purpose of the link; <c>null</c> — the core has no text for this link. An
    /// adapter that does not know the field ignores it.
    /// </summary>
    public WireRenderedPrefill? Rendered { get; init; }
}

/// <summary>
/// The prefill text of a deep link as the core rendered it (SPEC-042 C37): plain text, shaped like the
/// <c>rendered</c> object of <c>send-prompt</c>. The core does not promise the sign-in code in it: the
/// code is there only where the template states its slot.
/// </summary>
internal sealed class WireRenderedPrefill
{
    /// <summary>
    /// Text of the message the link prefills.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Language the text is rendered in.
    /// </summary>
    public required string Locale { get; init; }
}

/// <summary>
/// Result of <c>deep-link</c> (SPEC-042 C37).
/// </summary>
internal sealed class WireDeepLinkResult
{
    /// <summary>
    /// The deep link; accepted only as an absolute URI of an admitted scheme (SPEC-042 L45).
    /// </summary>
    public required string Url { get; init; }
}

/// <summary>
/// Payload of <c>health</c> (SPEC-042 C38): an empty object.
/// </summary>
internal sealed class WireHealthPayload;

/// <summary>
/// Result of <c>health</c> (SPEC-042 C38). Response time and check moment are measured by the wrapper.
/// </summary>
internal sealed class WireHealthResult
{
    /// <summary>
    /// Whether the adapter is healthy.
    /// </summary>
    public required bool Healthy { get; init; }

    /// <summary>
    /// Description of the state; <c>null</c> — none.
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// Version of the adapter (semver).
    /// </summary>
    public required string AdapterVersion { get; init; }

    /// <summary>
    /// Highest wire version the adapter supports.
    /// </summary>
    public required string WireVersion { get; init; }
}
