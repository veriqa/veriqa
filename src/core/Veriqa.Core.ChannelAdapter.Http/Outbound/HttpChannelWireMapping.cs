// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Wire;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Outbound;

/// <summary>
/// The explicit mapping of the types of the channel contract onto the wire and back (SPEC-042 R10,
/// C34–C36): every field and every enumeration value is named here, so a renamed member of the core
/// cannot move the wire. Raw IP address and User-Agent of the initiator have no place on the wire and
/// are never read (ICC-040); neither has any credential of the platform (SPEC-042 R11).
/// </summary>
internal static class HttpChannelWireMapping
{
    /// <summary>
    /// Message of a variant of the confirmation context the wire has no variant for.
    /// </summary>
    private const string UnknownPromptVariantMessage = "The confirmation context is of a variant the wire contract does not carry.";

    /// <summary>
    /// Message of a secondary action of the phone step the wire has no value for.
    /// </summary>
    private const string UnknownPhoneStepActionMessage = "The secondary action of the phone step is one the wire contract does not carry.";

    /// <summary>
    /// The secondary actions of the phone step with their wire values — the one table both directions
    /// of the mapping read.
    /// </summary>
    private static readonly (PhoneStepAction Action, string WireValue)[] PhoneStepActionValues =
    [
        (PhoneStepAction.Skip, WireValues.PhoneStepActions.Skip),
        (PhoneStepAction.CancelSignIn, WireValues.PhoneStepActions.CancelSignIn)
    ];

    /// <summary>
    /// The structured context of a confirmation (SPEC-042 C34), one wire variant per variant of the
    /// context.
    /// </summary>
    /// <param name="context">Confirmation context.</param>
    /// <returns>The prompt of the wire.</returns>
    /// <exception cref="UnreachableException">A variant of the context the wire does not carry.</exception>
    public static WirePrompt Prompt(ConfirmationPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var ownership = Ownership(context.Ownership);

        return context switch
        {
            DetailedConfirmationPromptContext details => new WireDetailedPrompt
            {
                Ownership = ownership,
                RecipientLocale = details.RecipientLocale,
                RecipientTimeZone = details.RecipientTimeZone,
                BrandName = details.BrandName,
                ClientApplicationName = details.ClientApplicationName,
                Browser = details.Browser,
                OsPlatform = details.OsPlatform,
                DeviceType = details.DeviceType,
                GeoCountry = details.GeoCountry,
                GeoCity = details.GeoCity,
                InitiatedAt = details.InitiatedAt.ToUniversalTime(),
                RequestedAt = details.RequestedAt?.ToUniversalTime(),
                Anomaly = details.AnomalyFlag,
                AnomalyReasonKey = details.AnomalyReasonKey,
                ChannelIdentity = Identity(details.ChannelIdentity),
                ChannelName = details.ChannelName,
                BotName = details.BotName
            },
            SubjectConfirmationPromptContext subject => new WireSubjectPrompt
            {
                Ownership = ownership,
                RecipientLocale = subject.RecipientLocale,
                RecipientTimeZone = subject.RecipientTimeZone,
                BrandName = subject.BrandName,
                PromptText = subject.PromptText
            },
            SuppressedConfirmationPromptContext suppressed => new WireSuppressedPrompt
            {
                Ownership = ownership,
                RecipientLocale = suppressed.RecipientLocale,
                RecipientTimeZone = suppressed.RecipientTimeZone,
                BrandName = suppressed.BrandName,
                Reason = SuppressionReason(suppressed.Reason)
            },
            _ => throw new UnreachableException(UnknownPromptVariantMessage)
        };
    }

    /// <summary>
    /// The channel snapshot of an account in the shape the adapter posts it in its events (SPEC-042
    /// C39), so one shape describes the account both ways; <c>null</c> — none. The delivery address is
    /// not part of the snapshot, so it is not stated.
    /// </summary>
    /// <param name="snapshot">Channel snapshot of the account.</param>
    /// <returns>The wire identity.</returns>
    private static WireIdentity? Identity(ChannelIdentitySnapshot? snapshot) =>
        snapshot is null
            ? null
            : new WireIdentity
            {
                ChannelUserId = snapshot.ChannelUserId,
                DisplayName = snapshot.DisplayName,
                IsBot = snapshot.IsBot,
                FirstName = snapshot.FirstName,
                LastName = snapshot.LastName,
                Username = snapshot.Username,
                Email = snapshot.Email,
                EmailVerified = snapshot.EmailVerified,
                PhoneNumber = snapshot.PhoneNumber,
                PhoneNumberVerified = snapshot.PhoneNumberVerified,
                AvatarUrl = snapshot.AvatarUrl,
                Locale = snapshot.Locale,
                RawMetadata = snapshot.RawMetadata,
                AdditionalClaims = snapshot.AdditionalClaims
            };

    /// <summary>
    /// The coordinates of a message on the wire; <c>null</c> — none.
    /// </summary>
    /// <param name="message">Coordinates of the message in the channel.</param>
    /// <returns>The wire coordinates.</returns>
    public static WireMessageRef? MessageRef(ChannelMessageRef? message) =>
        message is null
            ? null
            : new WireMessageRef { ChatId = message.ChatId, MessageId = message.MessageId };

    /// <summary>
    /// The coordinates of a message read from the wire; <c>null</c> — the channel has no addressable
    /// reference for it (CA-182).
    /// </summary>
    /// <param name="message">Wire coordinates.</param>
    /// <returns>Coordinates of the message in the channel.</returns>
    public static ChannelMessageRef? MessageRef(WireMessageRef? message) =>
        message is null
            ? null
            : new ChannelMessageRef { ChatId = message.ChatId, MessageId = message.MessageId };

    /// <summary>
    /// The outcome of a transaction on the wire (SPEC-042 C36) — a terminal one, or the non-terminal
    /// state of a claims completion. A value this mapping does not know is
    /// one the core added after this wire revision; it goes as the core spells it, and the adapter shows
    /// it as a terminal outcome by its status text (SPEC-042 L42).
    /// </summary>
    /// <param name="outcome">Outcome of the transaction.</param>
    /// <returns>The wire value.</returns>
    public static string Outcome(TransactionOutcome outcome) => outcome.Value switch
    {
        TransactionOutcomeCodes.Confirmed => WireValues.Outcomes.Confirmed,
        TransactionOutcomeCodes.Declined => WireValues.Outcomes.Declined,
        TransactionOutcomeCodes.Expired => WireValues.Outcomes.Expired,
        TransactionOutcomeCodes.Failed => WireValues.Outcomes.Failed,
        TransactionOutcomeCodes.AwaitingClaims => WireValues.Outcomes.AwaitingClaims,
        var added => added
    };

    /// <summary>
    /// How the outcome should appear, on the wire (SPEC-042 C36). A value this mapping does not know is
    /// sent as a message of its own — how the adapter reads an unknown value anyway (SPEC-042 L42).
    /// </summary>
    /// <param name="displayIntent">Display intent of the notice.</param>
    /// <returns>The wire value.</returns>
    public static string DisplayIntent(OutcomeNoticeDisplayIntent displayIntent) => displayIntent switch
    {
        OutcomeNoticeDisplayIntent.ReplacePrompt => WireValues.DisplayIntents.ReplacePrompt,
        _ => WireValues.DisplayIntents.NewMessage
    };

    /// <summary>
    /// Where the link is going to be used, on the wire (SPEC-042 C37). A value the contract does not
    /// define is a programming error of the caller and is thrown before anything is serialized.
    /// </summary>
    /// <param name="purpose">Purpose of the link.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The purpose is not a defined value.</exception>
    public static string LinkPurpose(DeepLinkPurpose purpose) => purpose switch
    {
        DeepLinkPurpose.Button => WireValues.LinkPurposes.Button,
        DeepLinkPurpose.HopQr => WireValues.LinkPurposes.HopQr,
        DeepLinkPurpose.DirectQr => WireValues.LinkPurposes.DirectQr,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown deep link purpose.")
    };

    /// <summary>
    /// The content of a request for the phone number on the wire (SPEC-042 C35), every member of the
    /// content of the core.
    /// </summary>
    /// <param name="request">Content of the request.</param>
    /// <returns>The wire content.</returns>
    /// <exception cref="UnreachableException">A secondary action the wire does not carry.</exception>
    public static WirePhoneNumberRequest PhoneNumberRequest(PhoneNumberRequestContent request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new WirePhoneNumberRequest
        {
            TransactionId = request.TransactionId.ToString(),
            ShareButtonLabel = request.ShareButtonLabel,
            SecondaryAction = PhoneStepActionValue(request.SecondaryAction),
            SecondaryButtonLabel = request.SecondaryButtonLabel
        };
    }

    /// <summary>
    /// The content of a link to the claim completion form on the wire (SPEC-042 C35), every member of
    /// the content of the core.
    /// </summary>
    /// <param name="link">Content of the link.</param>
    /// <returns>The wire content.</returns>
    public static WireClaimFormLink ClaimFormLink(ClaimFormLinkContent link)
    {
        ArgumentNullException.ThrowIfNull(link);

        return new WireClaimFormLink { Url = link.Url, ButtonLabel = link.ButtonLabel };
    }

    /// <summary>
    /// The buttons of an action message on the wire (SPEC-042 C35), in the order of the content. A
    /// button is exactly one of a link and an item of the menu; an item travels as its value — the
    /// callback payload without the ownership marker <see cref="CallbackDataPrefixes.MenuItem"/>, since
    /// the adapter builds the payload of the platform button itself (SPEC-042 R3).
    /// </summary>
    /// <param name="content">Buttons of the message.</param>
    /// <returns>
    /// The wire buttons; <c>null</c> — a button is malformed: it is both a link and a callback or
    /// neither, or its callback is not an item of the menu, or it is the marker alone that names no item,
    /// or the value of its item is longer than <see cref="HttpChannelWire.MaxMenuItemValueLength"/> — the
    /// press of such a button would come back as an event the wrapper reads as unrelated (SPEC-042 C39).
    /// </returns>
    public static WireActionButtons? ActionButtons(ActionButtonsContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var buttons = new List<WireActionButton>(content.Buttons.Count);

        foreach (var button in content.Buttons)
        {
            if (ActionButton(button) is not { } wireButton)
            {
                return null;
            }

            buttons.Add(wireButton);
        }

        return new WireActionButtons { Buttons = buttons };
    }

    /// <summary>
    /// The secondary action of the phone step read from the wire (SPEC-042 C39).
    /// </summary>
    /// <param name="value">Wire value.</param>
    /// <param name="action">The action, when the value is one this wire revision knows.</param>
    /// <returns>Whether the value is known; an unknown one is a value a newer revision added (SPEC-042 L42).</returns>
    public static bool TryPhoneStepAction(string value, out PhoneStepAction action)
    {
        foreach (var (known, wireValue) in PhoneStepActionValues)
        {
            if (string.Equals(value, wireValue, StringComparison.Ordinal))
            {
                action = known;
                return true;
            }
        }

        action = default;
        return false;
    }

    /// <summary>
    /// The secondary action of the phone step on the wire (SPEC-042 C35).
    /// </summary>
    /// <param name="action">The action.</param>
    /// <returns>The wire value.</returns>
    /// <exception cref="UnreachableException">An action the wire does not carry.</exception>
    private static string PhoneStepActionValue(PhoneStepAction action)
    {
        foreach (var (known, wireValue) in PhoneStepActionValues)
        {
            if (known == action)
            {
                return wireValue;
            }
        }

        throw new UnreachableException(UnknownPhoneStepActionMessage);
    }

    /// <summary>
    /// One button of an action message on the wire. A link and a callback are told apart as the
    /// built-in channels tell them: an empty value is no value.
    /// </summary>
    /// <param name="button">The button.</param>
    /// <returns>The wire button; <c>null</c> — the button is malformed.</returns>
    private static WireActionButton? ActionButton(ChannelActionButton button)
    {
        var hasUrl = !string.IsNullOrEmpty(button.Url);
        var hasCallbackData = !string.IsNullOrEmpty(button.CallbackData);

        if (hasUrl == hasCallbackData)
        {
            return null;
        }

        if (hasUrl)
        {
            return new WireActionButton { Label = button.Label, Url = button.Url, MenuItemValue = null };
        }

        var callbackData = button.CallbackData!;
        if (!callbackData.StartsWith(CallbackDataPrefixes.MenuItem, StringComparison.Ordinal))
        {
            return null;
        }

        var menuItemValue = callbackData[CallbackDataPrefixes.MenuItem.Length..];
        if (menuItemValue.Length is 0 or > HttpChannelWire.MaxMenuItemValueLength)
        {
            return null;
        }

        return new WireActionButton { Label = button.Label, Url = null, MenuItemValue = menuItemValue };
    }

    /// <summary>
    /// The owners of a transaction on the wire (SPEC-042 C34).
    /// </summary>
    /// <param name="ownership">Ownership levels of the transaction.</param>
    /// <returns>The wire owners.</returns>
    private static WireOwnership Ownership(ResolutionContext ownership) => new()
    {
        TenantId = ownership.TenantId,
        ApplicationId = ownership.ApplicationId,
        UiConfigSelector = ownership.UiConfigSelector,
        RequestedScopes = ownership.PerRequest?.RequestedScopes,
        AcrValues = ownership.PerRequest?.AcrValues,
        UserId = ownership.UserOverride?.UserId
    };

    /// <summary>
    /// Why the details of a prompt are absent, on the wire (SPEC-042 C34). A value this mapping does not
    /// know goes as the core spells it; the adapter reads it as a suppression without a reason
    /// (SPEC-042 L42).
    /// </summary>
    /// <param name="reason">Reason of the suppression.</param>
    /// <returns>The wire value.</returns>
    private static string SuppressionReason(ContextSuppressionReason reason) => reason.Value switch
    {
        ContextSuppressionReasons.InitiatorSnapshotUnavailable => WireValues.SuppressionReasons.InitiatorSnapshotUnavailable,
        ContextSuppressionReasons.DisplayDisabledByConfiguration => WireValues.SuppressionReasons.DisplayDisabledByConfiguration,
        ContextSuppressionReasons.ConfirmationTemplateUnavailable => WireValues.SuppressionReasons.ConfirmationTemplateUnavailable,
        var added => added
    };
}
