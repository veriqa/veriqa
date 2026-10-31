// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using Veriqa.Core.ChannelAdapter.Domain;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Host-side handler of a press on a callback item of the post-sign-in menu (SPEC-012 §4.9).
/// The host implements it and registers the implementation in its dependency injection container;
/// Veriqa ships none.
/// </summary>
/// <remarks>
/// It is called for every press of a callback menu item that reaches the core — from a built-in
/// adapter, or from the external adapter of an HTTP channel registration. The press is not answered to
/// the platform from here: a built-in adapter answers it itself before handing the press over, where the
/// platform waits for an answer; on the HTTP channel the external adapter answers the platform, and the
/// core answers the request of that adapter. There is nothing to return: what the press means is the
/// host's business. The event names no sign-in transaction. An exception thrown here — by the
/// constructor of the implementation or by the call — is logged and affects neither the user nor any
/// sign-in.
/// <para>
/// <b>The same press can arrive again.</b> A repeated delivery of the same event reaches the handler
/// again, with the same <see cref="MenuItemPress.PressId"/>: the core does not discard repeats. An
/// implementation whose work must not run twice keys it by that identifier together with
/// <see cref="MenuItemPress.ChannelType"/>: the identifier is unique within the channel alone.
/// </para>
/// <para>
/// <b>On the HTTP channel the identity is stated by the adapter.</b> Who pressed
/// (<see cref="MenuItemPress.ChannelUserId"/>) and the tenant of the press (the route of a
/// registration without an owner) are stated by the external adapter the operator trusts, not by the
/// platform (SPEC-042 R19, R60). A built-in adapter takes the user from the platform event.
/// </para>
/// <para>
/// <b>Return promptly.</b> The call is awaited by the processing of the inbound event: until it
/// returns, the platform is not told the event is taken (a webhook left unanswered is delivered
/// again, and the press arrives twice), and where events are read in turn the events of other users
/// wait behind it. Work that takes time belongs in a background queue of the host.
/// </para>
/// <para>
/// <b>The value is untrusted input.</b> It is what the pressed button carried, and a button outlives
/// the configuration that drew it: an item removed from the menu can still be pressed in an old
/// message, and the value is not checked against the items configured today. The implementation
/// checks the value — and what the user may do — itself.
/// </para>
/// <para>
/// Without a registered implementation callback items are not drawn at all; link items of the same
/// menu are unaffected.
/// </para>
/// </remarks>
public interface IMenuItemPressHandler
{
    /// <summary>
    /// Handles one press of a callback menu item.
    /// </summary>
    /// <param name="press">Who pressed, in which channel and tenant, and the value of the item.</param>
    /// <param name="cancellationToken">Cancellation token of the inbound event being processed.</param>
    /// <returns>A task that completes when the press is handled.</returns>
    Task HandleAsync(MenuItemPress press, CancellationToken cancellationToken = default);
}
