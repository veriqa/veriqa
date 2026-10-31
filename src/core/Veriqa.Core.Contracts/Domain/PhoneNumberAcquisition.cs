// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// How a channel obtains the phone number of its user (SPEC-041 C36). A channel may declare both ways
/// at once: it gets the number by itself while the platform sends it, and asks for it otherwise.
/// </summary>
[Flags]
public enum PhoneNumberAcquisition
{
    /// <summary>
    /// The channel gives no phone number — neither by itself nor on request.
    /// </summary>
    None = 0,

    /// <summary>
    /// The number is known to the channel without any action of the user: the platform delivers it
    /// with the user's messages.
    /// </summary>
    Automatic = 1,

    /// <summary>
    /// The channel can ask the user to share the number and receive it inside the channel.
    /// </summary>
    OnRequest = 2
}
