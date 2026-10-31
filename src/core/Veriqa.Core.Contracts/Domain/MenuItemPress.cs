// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// A press of a callback item of the post-sign-in menu (SPEC-012 §4.9), as handed to the host's
/// <see cref="Veriqa.Core.ChannelAdapter.Abstractions.IMenuItemPressHandler"/>.
/// </summary>
/// <remarks>
/// The event names the user and the item, not a sign-in: the menu is shown after the transaction is
/// completed, and no transaction travels with the press.
/// </remarks>
public sealed record MenuItemPress
{
    /// <summary>
    /// Type of the channel the button was pressed in.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Identifier of the user within the channel.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// Tenant the adapter states for the user; null — the default implicit tenant.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Value the pressed button carried, without the ownership marker — what the operator configured
    /// for the menu item when the message was sent. Untrusted input: it is not checked against the
    /// items configured now, so a button of an old message delivers the value of an item that is no
    /// longer in the menu.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Identifier of the press as the channel named it — the key by which the host discards a repeated
    /// delivery of the same press; null — the channel named no identifier. It is unique within the
    /// channel (<see cref="ChannelType"/>) alone: a host serving several channels keys by both.
    /// </summary>
    public string? PressId { get; init; }
}
