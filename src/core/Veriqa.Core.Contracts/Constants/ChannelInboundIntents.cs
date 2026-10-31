// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Constants;

/// <summary>
/// Intent discriminators of the inbound result hierarchy (SPEC-003 §6.3).
/// The same codes are the JSON type discriminator of <c>ChannelInboundResult</c>, so a replay
/// fixture written against the wire form and the type map can never drift apart.
/// </summary>
public static class ChannelInboundIntents
{
    /// <summary>
    /// The user initiated the start of authentication.
    /// </summary>
    public const string AuthStart = "AuthStart";

    /// <summary>
    /// The user initiated the start of authentication by sending the code shown on the sign-in page.
    /// </summary>
    public const string AuthStartByCode = "AuthStartByCode";

    /// <summary>
    /// The user confirmed authentication.
    /// </summary>
    public const string AuthConfirm = "AuthConfirm";

    /// <summary>
    /// The user declined authentication.
    /// </summary>
    public const string AuthDecline = "AuthDecline";

    /// <summary>
    /// The user shared a phone number.
    /// </summary>
    public const string PhoneShared = "PhoneShared";

    /// <summary>
    /// The user pressed the secondary action of the phone step (skip the number, cancel the sign-in).
    /// </summary>
    public const string PhoneStepAction = "PhoneStepAction";

    /// <summary>
    /// The user pressed a callback item of the post-sign-in menu.
    /// </summary>
    public const string MenuItemPressed = "MenuItemPressed";

    /// <summary>
    /// The event is unrelated to the authentication process.
    /// </summary>
    public const string Unrelated = "Unrelated";

    /// <summary>
    /// A direct message to the bot that names no transaction, from a sender the adapter vetted.
    /// </summary>
    public const string Unaddressed = "Unaddressed";
}
