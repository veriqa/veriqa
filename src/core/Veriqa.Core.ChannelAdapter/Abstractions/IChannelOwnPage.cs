// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Optional capability of a channel adapter whose chat is a page of the core itself — the page of a web
/// provider channel. A page the core opens from such a chat (the claims completion of the device that
/// confirmed the sign-in) sends the browser back to that page when it is done instead of ending on a
/// receipt of its own: the chat is where the menu and the outcome are shown, and a messenger chat is
/// returned to by closing the browser over it, while a page that replaced the chat in its tab is not.
/// </summary>
internal interface IChannelOwnPage
{
    /// <summary>
    /// Address of the page of the channel, rooted and without the path base of the application.
    /// </summary>
    /// <returns>The rooted path.</returns>
    string PagePath();
}
