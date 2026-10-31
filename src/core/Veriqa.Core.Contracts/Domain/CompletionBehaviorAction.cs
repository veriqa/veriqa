// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// What happens in the channel after a sign-in is completed (SPEC-012 §4.9). The deployment states
/// the action it asks for; a channel declares in its <see cref="ChannelCapabilities"/> which of them
/// it can render, and an action outside that set is replaced with the channel's own default.
/// </summary>
public enum CompletionBehaviorAction
{
    /// <summary>
    /// Nothing is done after the sign-in is completed — the value the product ships with.
    /// </summary>
    None = 0,

    /// <summary>
    /// The channel sends the user a welcome message.
    /// </summary>
    WelcomeMessage = 1,

    /// <summary>
    /// The channel sends the user a message with a menu — a list of buttons, each a link or a
    /// callback handed to the host. A channel that declares the action renders it; on any other
    /// channel the value is replaced with that channel's default.
    /// </summary>
    Menu = 2,

    /// <summary>
    /// The channel sends the user a message with one button that opens a link. A channel that
    /// declares the action renders it; on any other channel the value is replaced with that channel's
    /// default.
    /// </summary>
    Redirect = 3
}
