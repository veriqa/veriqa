// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// Result of processing an inbound channel event (SPEC-003 §6.3).
/// The intent is the type: every intent brings its own mandatory set, so "which field is valid for
/// which intent" is no longer a rule each consumer has to remember.
/// </summary>
/// <remarks>
/// The hierarchy is closed (a private protected constructor) and carries its type through the
/// standard <c>System.Text.Json</c> polymorphism, with the discriminator named <c>intent</c> —
/// the replay perimeter of the sandbox deserializes this shape from disk and from a request body.
/// A consumer must have an "unknown result type" branch (warning plus no-op) so that a further intent
/// does not break the runtime.
/// <para>
/// That further intent is why the type is marked experimental: the roadmap items that add one —
/// unlinking a channel, a chain of channels — are still ahead, so this
/// hierarchy may gain a variant in a minor version instead of waiting for a major one.
/// </para>
/// </remarks>
[Experimental(VeriqaDiagnosticIds.UnsettledChannelSpiShape)]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "intent")]
[JsonDerivedType(typeof(ChannelAuthStartResult), ChannelInboundIntents.AuthStart)]
[JsonDerivedType(typeof(ChannelAuthStartByCodeResult), ChannelInboundIntents.AuthStartByCode)]
[JsonDerivedType(typeof(ChannelAuthConfirmResult), ChannelInboundIntents.AuthConfirm)]
[JsonDerivedType(typeof(ChannelAuthDeclineResult), ChannelInboundIntents.AuthDecline)]
[JsonDerivedType(typeof(ChannelPhoneSharedResult), ChannelInboundIntents.PhoneShared)]
[JsonDerivedType(typeof(ChannelPhoneStepActionResult), ChannelInboundIntents.PhoneStepAction)]
[JsonDerivedType(typeof(ChannelMenuItemPressedResult), ChannelInboundIntents.MenuItemPressed)]
[JsonDerivedType(typeof(ChannelUnrelatedResult), ChannelInboundIntents.Unrelated)]
[JsonDerivedType(typeof(ChannelUnaddressedResult), ChannelInboundIntents.Unaddressed)]
public abstract record ChannelInboundResult
{
    /// <summary>
    /// Closes the hierarchy to this assembly.
    /// </summary>
    private protected ChannelInboundResult()
    {
    }

    /// <summary>
    /// Original platform event, kept for logging.
    /// </summary>
    public object? RawEvent { get; init; }
}

/// <summary>
/// The user initiated the start of authentication (deep link followed, prefilled message sent).
/// </summary>
/// <remarks>
/// This is the start of a channel whose link carries the transaction — a channel that declared no
/// <see cref="ChannelCapabilities.CodeStart"/>, or one that declared it with
/// <see cref="ChannelCodeStart.LinkCarriesTransaction"/> <c>true</c> and got the transaction from the
/// link. A start by the code shown on the sign-in page is <see cref="ChannelAuthStartByCodeResult"/>.
/// </remarks>
public sealed record ChannelAuthStartResult : ChannelInboundResult
{
    /// <summary>
    /// Transaction identifier the event belongs to, already parsed by the adapter: the raw string
    /// lives in the channel event, so the channel is where it is read out (SPEC-003 §6.3). An event
    /// whose identifier does not parse is not ours — the adapter answers with
    /// <see cref="ChannelUnrelatedResult"/> instead of constructing this intent.
    /// </summary>
    public required TransactionId TransactionId { get; init; }

    /// <summary>
    /// Snapshot of the user's identity in the channel.
    /// </summary>
    public required ChannelIdentitySnapshot Identity { get; init; }
}

/// <summary>
/// The user initiated the start of authentication by sending the code shown on the sign-in page — the
/// start of a channel that declared <see cref="ChannelCapabilities.CodeStart"/>. The core finds the
/// transaction by the code and carries on exactly as after <see cref="ChannelAuthStartResult"/>.
/// </summary>
/// <remarks>
/// A message with a code carries none of the markers of <see cref="CallbackDataPrefixes"/>, so the
/// default <see cref="Veriqa.Core.ChannelAdapter.Abstractions.IChannelAdapter.OwnsInboundEvent"/>
/// does not claim it. A channel that needs such an event claimed overrides that member.
/// </remarks>
public sealed record ChannelAuthStartByCodeResult : ChannelInboundResult
{
    /// <summary>
    /// The code as the user sent it, without the command wrapper of
    /// <see cref="ChannelCodeStart.MessageTemplate"/>. The adapter neither normalizes it nor checks
    /// its length: both belong to the core, and a value that names no transaction is answered there.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Snapshot of the user's identity in the channel.
    /// </summary>
    public required ChannelIdentitySnapshot Identity { get; init; }
}

/// <summary>
/// The user confirmed authentication.
/// </summary>
public sealed record ChannelAuthConfirmResult : ChannelInboundResult
{
    /// <summary>
    /// Transaction identifier the event belongs to, already parsed by the adapter: the raw string
    /// lives in the channel event, so the channel is where it is read out (SPEC-003 §6.3). An event
    /// whose identifier does not parse is not ours — the adapter answers with
    /// <see cref="ChannelUnrelatedResult"/> instead of constructing this intent.
    /// </summary>
    public required TransactionId TransactionId { get; init; }

    /// <summary>
    /// Snapshot of the user's identity in the channel.
    /// </summary>
    public required ChannelIdentitySnapshot Identity { get; init; }

    /// <summary>
    /// Opaque token of the interaction, handed back to the adapter with the outcome notice.
    /// Absent when the event carries no interactive context (polling, a channel without buttons).
    /// </summary>
    public ChannelInboundToken? InboundToken { get; init; }
}

/// <summary>
/// The user declined authentication.
/// </summary>
public sealed record ChannelAuthDeclineResult : ChannelInboundResult
{
    /// <summary>
    /// Transaction identifier the event belongs to, already parsed by the adapter: the raw string
    /// lives in the channel event, so the channel is where it is read out (SPEC-003 §6.3). An event
    /// whose identifier does not parse is not ours — the adapter answers with
    /// <see cref="ChannelUnrelatedResult"/> instead of constructing this intent.
    /// </summary>
    public required TransactionId TransactionId { get; init; }

    /// <summary>
    /// Snapshot of the user's identity in the channel. Mandatory on this branch too: the outcome
    /// notice needs the recipient, and an optional snapshot would degrade into a silent skip.
    /// </summary>
    public required ChannelIdentitySnapshot Identity { get; init; }

    /// <summary>
    /// Opaque token of the interaction, handed back to the adapter with the outcome notice.
    /// </summary>
    public ChannelInboundToken? InboundToken { get; init; }
}

/// <summary>
/// The user shared a phone number (CA-004).
/// </summary>
public sealed record ChannelPhoneSharedResult : ChannelInboundResult
{
    /// <summary>
    /// Shared phone number in E.164 with the leading plus. Bringing it there is the adapter's duty, not
    /// the core's (SPEC-041 L47, SPEC-003 CA-009): a platform's own spelling does not cross this
    /// boundary. <see cref="Veriqa.Core.Contracts.E164PhoneNumber.TryNormalize(string, out string)"/>
    /// brings a number there. Empty — the shared value has no E.164 form; the core counts such a number
    /// as absent.
    /// </summary>
    public required string PhoneNumber { get; init; }

    /// <summary>
    /// Snapshot of the user's identity in the channel.
    /// </summary>
    public required ChannelIdentitySnapshot Identity { get; init; }

    /// <summary>
    /// True — the shared contact did not pass the channel's check that the number belongs to the sender
    /// (SPEC-041 L46): a card of somebody else's contact, a forwarded one, a signature that does not
    /// match. Such a number is never taken as the sender's; the event is still reported, because the
    /// step answers it by asking again.
    /// </summary>
    public bool OwnershipRejected { get; init; }
}

/// <summary>
/// The user pressed the secondary action of the phone step of claim completion (SPEC-041 L73) — a
/// button that carries the transaction in its payload.
/// </summary>
public sealed record ChannelPhoneStepActionResult : ChannelInboundResult
{
    /// <summary>
    /// Transaction the pressed button belongs to, already parsed by the adapter. An event whose
    /// identifier does not parse is not ours — the adapter answers with
    /// <see cref="ChannelUnrelatedResult"/> instead of constructing this intent.
    /// </summary>
    public required TransactionId TransactionId { get; init; }

    /// <summary>
    /// Snapshot of the user's identity in the channel.
    /// </summary>
    public required ChannelIdentitySnapshot Identity { get; init; }

    /// <summary>
    /// Action the pressed button stands for.
    /// </summary>
    public required PhoneStepAction Action { get; init; }
}

/// <summary>
/// The user pressed a callback item of the post-sign-in menu (SPEC-012 §4.9) — a button whose payload
/// starts with <see cref="CallbackDataPrefixes.MenuItem"/>. Where the platform waits for an answer to
/// the press (Telegram, MAX), the built-in adapter has already given it; a platform that waits for none
/// gets no answer (SPEC-003 §4.9.3). On the HTTP channel the external adapter answers the platform
/// itself. The core only hands the value over to the host.
/// </summary>
/// <remarks>
/// The intent names no transaction: the menu is shown after the sign-in is completed, and its buttons
/// outlive it.
/// </remarks>
public sealed record ChannelMenuItemPressedResult : ChannelInboundResult
{
    /// <summary>
    /// Snapshot of the identity of the user who pressed the button.
    /// </summary>
    public required ChannelIdentitySnapshot Identity { get; init; }

    /// <summary>
    /// Value of the pressed item — what follows the marker in the button payload. Never empty: a
    /// payload that carries the marker alone is not this intent, the adapter answers with
    /// <see cref="ChannelUnrelatedResult"/>.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Identifier of the press as the channel named it — the identifier the platform gave the callback
    /// or the message the press arrived as, or the delivery identifier of the external adapter on the
    /// HTTP channel; a repeated delivery of the same press carries the same one. Null — the adapter
    /// named no identifier.
    /// </summary>
    public string? PressId { get; init; }
}

/// <summary>
/// The event is unrelated to the authentication process.
/// </summary>
public sealed record ChannelUnrelatedResult : ChannelInboundResult;

/// <summary>
/// A direct message to the bot from a user the adapter already vetted, that names no transaction
/// (a bare /start, free text). Unlike <see cref="ChannelUnrelatedResult"/> it carries the sender, so
/// a reply is possible when the registration declares one.
/// </summary>
/// <remarks>
/// The vetting is the one an inbound message already goes through — a private chat, a sender that is
/// present and is not a bot. An event that fails it, one from a group chat, and a callback that is
/// none of ours stay <see cref="ChannelUnrelatedResult"/>: they carry no sender the core may answer
/// (SPEC-003 CA-193).
/// </remarks>
public sealed record ChannelUnaddressedResult : ChannelInboundResult
{
    /// <summary>
    /// Snapshot of the sender's identity in the channel. Mandatory: an answer without a recipient is
    /// no answer, and this intent exists precisely to carry one.
    /// </summary>
    public required ChannelIdentitySnapshot Identity { get; init; }

    /// <summary>
    /// Text of the message; null when the event carries none. A plain keyboard button arrives as the
    /// text of its label, which is how the secondary action of the phone step reaches the core from a
    /// channel whose buttons carry no payload (SPEC-041 L73).
    /// </summary>
    public string? Text { get; init; }
}
