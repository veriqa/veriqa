// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// What a read of the held address of a transaction found (<see cref="IClaimCompletionEmail.GetHeldAddressAsync"/>).
/// </summary>
/// <param name="Known">Whether the store answered; false — it could not be reached, and an address may be held.</param>
/// <param name="Held">The held address; null — none is held, or it is not known.</param>
internal readonly record struct ClaimEmailHeldRead(bool Known, ClaimEmailHeldAddress? Held)
{
    /// <summary>
    /// The store could not be reached: whether an address is held is not known.
    /// </summary>
    public static ClaimEmailHeldRead Unknown { get; } = new(Known: false, Held: null);

    /// <summary>
    /// The store answered that no address is held, or there is no mail side to hold one.
    /// </summary>
    public static ClaimEmailHeldRead None { get; } = new(Known: true, Held: null);
}
