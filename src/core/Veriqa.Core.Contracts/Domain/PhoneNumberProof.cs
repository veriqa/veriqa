// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// Whether a channel proves that the phone number it gives belongs to the user who sent it
/// (SPEC-041 C36).
/// </summary>
public enum PhoneNumberProof
{
    /// <summary>
    /// The channel does not prove that the number belongs to the sender.
    /// </summary>
    NotProven = 0,

    /// <summary>
    /// The number is proven to belong to the sender — the platform itself binds it to the account the
    /// message came from.
    /// </summary>
    Proven = 1
}
