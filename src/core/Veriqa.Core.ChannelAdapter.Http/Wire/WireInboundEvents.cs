// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;

namespace Veriqa.Core.ChannelAdapter.Http.Wire;

/// <summary>
/// The fields every inbound event envelope carries (SPEC-042 C39). Read first and alone: the major
/// version decides whether the rest may be read at all, and <c>event</c> decides which fields the
/// rest is.
/// </summary>
internal sealed class WireEventHead
{
    /// <summary>
    /// Wire version of the adapter (SPEC-042 C40).
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Type of the event — a value of <see cref="HttpChannelWire.Events"/>; an unknown value is read
    /// as an unrelated event (SPEC-042 L42).
    /// </summary>
    public required string Event { get; init; }

    /// <summary>
    /// Version of the adapter (semver) — the adapter version of the identity snapshot.
    /// </summary>
    public required string AdapterVersion { get; init; }
}

/// <summary>
/// Fields of <c>auth_start</c> (SPEC-042 C39).
/// </summary>
internal sealed class WireAuthStartEvent
{
    /// <summary>
    /// Identifier of the transaction.
    /// </summary>
    public required string TransactionId { get; init; }

    /// <summary>
    /// Identity of the user in the channel.
    /// </summary>
    public required WireIdentity Identity { get; init; }
}

/// <summary>
/// Fields of <c>auth_start_by_code</c> (SPEC-042 C39): the start of a channel that declares the start by
/// a code. The event names no transaction — the core finds it by the code.
/// </summary>
internal sealed class WireAuthStartByCodeEvent
{
    /// <summary>
    /// The code as the user sent it, without the wrapper of the message template. Neither normalized
    /// nor checked for length on the wire: both belong to the core (SPEC-003 CA-218).
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Identity of the user in the channel.
    /// </summary>
    public required WireIdentity Identity { get; init; }
}

/// <summary>
/// Fields of <c>auth_confirm</c> and <c>auth_decline</c> (SPEC-042 C39).
/// </summary>
internal sealed class WireAuthAnswerEvent
{
    /// <summary>
    /// Identifier of the transaction.
    /// </summary>
    public required string TransactionId { get; init; }

    /// <summary>
    /// Identity of the user in the channel.
    /// </summary>
    public required WireIdentity Identity { get; init; }

    /// <summary>
    /// Opaque token of the interaction, handed back with the outcome; <c>null</c> — none.
    /// </summary>
    public string? InboundToken { get; init; }
}

/// <summary>
/// Fields of <c>phone_shared</c> (SPEC-042 C39).
/// </summary>
internal sealed class WirePhoneSharedEvent
{
    /// <summary>
    /// The shared phone number in E.164 with the leading plus: the plus and 1 to 15 digits, the first not
    /// zero, no separators or whitespace. A value in any other spelling is not repaired — it leaves the
    /// event unprocessed (SPEC-042 E52).
    /// </summary>
    public required string PhoneNumber { get; init; }

    /// <summary>
    /// Identity of the user in the channel.
    /// </summary>
    public required WireIdentity Identity { get; init; }
}

/// <summary>
/// Fields of <c>phone_step_action</c> (SPEC-042 C39): the user chose the secondary action of the phone
/// step through a control that carries the transaction.
/// </summary>
internal sealed class WirePhoneStepActionEvent
{
    /// <summary>
    /// Identifier of the transaction.
    /// </summary>
    public required string TransactionId { get; init; }

    /// <summary>
    /// The chosen action — a value of <see cref="WireValues.PhoneStepActions"/>; an unknown value makes
    /// the event unrelated (SPEC-042 L42).
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Identity of the user in the channel.
    /// </summary>
    public required WireIdentity Identity { get; init; }
}

/// <summary>
/// Fields of <c>menu_item_pressed</c> (SPEC-042 C39): the user pressed a callback item of the
/// post-sign-in menu. The event names no transaction — the press is handed to the host.
/// </summary>
internal sealed class WireMenuItemPressedEvent
{
    /// <summary>
    /// Value of the pressed item, as <c>send-message</c> carried it in the <c>menu_item_value</c> of the
    /// button. An empty value and one longer than <see cref="HttpChannelWire.MaxMenuItemValueLength"/>
    /// make the event unrelated.
    /// </summary>
    public required string MenuItemValue { get; init; }

    /// <summary>
    /// Identity of the user who pressed the item.
    /// </summary>
    public required WireIdentity Identity { get; init; }
}

/// <summary>
/// Fields of <c>unaddressed</c> (SPEC-042 C39).
/// </summary>
internal sealed class WireUnaddressedEvent
{
    /// <summary>
    /// Identity of the sender in the channel.
    /// </summary>
    public required WireIdentity Identity { get; init; }

    /// <summary>
    /// Text of the message; <c>null</c> — the event carries none. A control that carries no payload
    /// reaches the core as the text of its caption, which is how such a channel answers the secondary
    /// action of the phone step.
    /// </summary>
    public string? Text { get; init; }
}

/// <summary>
/// Identity of the user in the channel as the adapter reports it (SPEC-042 C39) — and the shape in
/// which the core states the account a question is asked of (C34). The wrapper carries an inbound one
/// as it is — the operator trusts the external adapter of the registration (SPEC-042 R19) — except
/// for the vendor claims, which it drops, and for a proof of the phone number above the one the
/// registration declares, which it lowers.
/// </summary>
internal sealed class WireIdentity
{
    /// <summary>
    /// Identifier of the user in the channel.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// Display name of the user.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Whether the source is a bot.
    /// </summary>
    public required bool IsBot { get; init; }

    /// <summary>
    /// Delivery address when it differs from the identifier (SPEC-043 C41). Part of the wire, read and
    /// not carried further: as shipped today, the identity snapshot of the channel contract has no
    /// field for a delivery address, so an outbound call addresses the identifier, and the account a
    /// prompt states (C34) always has <c>null</c> here.
    /// </summary>
    public string? DeliveryAddress { get; init; }

    /// <summary>
    /// First name of the user.
    /// </summary>
    public string? FirstName { get; init; }

    /// <summary>
    /// Last name of the user.
    /// </summary>
    public string? LastName { get; init; }

    /// <summary>
    /// Username of the user in the channel.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// E-mail address of the user.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Whether the channel verified that the user controls the e-mail address; <c>null</c> — no statement.
    /// </summary>
    public bool? EmailVerified { get; init; }

    /// <summary>
    /// Phone number of the user.
    /// </summary>
    public string? PhoneNumber { get; init; }

    /// <summary>
    /// Whether the channel proved that the phone number belongs to the user; <c>null</c> — no statement.
    /// <c>true</c> is taken only from a registration that declares the number proven (SPEC-042 C39).
    /// </summary>
    public bool? PhoneNumberVerified { get; init; }

    /// <summary>
    /// Avatar of the user.
    /// </summary>
    public string? AvatarUrl { get; init; }

    /// <summary>
    /// Language of the user.
    /// </summary>
    public string? Locale { get; init; }

    /// <summary>
    /// Arbitrary metadata of the channel — a JSON object (CA-005).
    /// </summary>
    public JsonElement? RawMetadata { get; init; }

    /// <summary>
    /// Additional claims of the adapter, name → value.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AdditionalClaims { get; init; }
}
