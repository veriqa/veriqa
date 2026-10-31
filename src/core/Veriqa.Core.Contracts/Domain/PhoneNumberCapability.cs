// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// The fact a channel declares about the phone number of its user (SPEC-041 C36): how it obtains the
/// number and whether it proves that the number belongs to the sender.
/// </summary>
public sealed record PhoneNumberCapability
{
    /// <summary>
    /// The channel gives no phone number — the value of every channel that declares nothing.
    /// </summary>
    public static PhoneNumberCapability None { get; } = new();

    /// <summary>
    /// How the channel obtains the number. <see cref="PhoneNumberAcquisition.None"/> — it gives none.
    /// </summary>
    public PhoneNumberAcquisition Acquisition { get; init; }

    /// <summary>
    /// Whether the channel proves that the number belongs to the sender. Meaningful only when
    /// <see cref="Acquisition"/> is not <see cref="PhoneNumberAcquisition.None"/>.
    /// </summary>
    public PhoneNumberProof Proof { get; init; }
}
