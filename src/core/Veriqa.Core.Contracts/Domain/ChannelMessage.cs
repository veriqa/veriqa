// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// An outbound message the core asks a channel to deliver (SPEC-003 §6.2).
/// Extensible by construction: a richer content kind is a new <see cref="ChannelMessageKind"/> plus
/// new properties with defaults — never a new interface member.
/// </summary>
public sealed record ChannelMessage
{
    /// <summary>
    /// Recipient identifier within the channel.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// Message text, already localized by the caller.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Content kind; plain text by default.
    /// </summary>
    public ChannelMessageKind Kind { get; init; } = ChannelMessageKind.PlainText;

    /// <summary>
    /// Content of a phone number request — set exactly when <see cref="Kind"/> is
    /// <see cref="ChannelMessageKind.PhoneNumberRequest"/>, and null for every other kind. The request
    /// text itself is <see cref="Text"/>.
    /// </summary>
    public PhoneNumberRequestContent? PhoneNumberRequest { get; init; }

    /// <summary>
    /// Content of a link to the claim completion form — set exactly when <see cref="Kind"/> is
    /// <see cref="ChannelMessageKind.ClaimFormLink"/>, and null for every other kind. The text shown
    /// above the button is <see cref="Text"/>.
    /// </summary>
    public ClaimFormLinkContent? ClaimFormLink { get; init; }

    /// <summary>
    /// Buttons of an action message — set exactly when <see cref="Kind"/> is
    /// <see cref="ChannelMessageKind.ActionButtons"/>, and null for every other kind. The text shown
    /// above the buttons is <see cref="Text"/>.
    /// </summary>
    public ActionButtonsContent? ActionButtons { get; init; }
}
