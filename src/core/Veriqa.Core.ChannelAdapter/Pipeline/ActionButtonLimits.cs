// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// What a platform lets an adapter draw as the buttons of a message: whether link buttons exist there
/// at all and the limits the platform names. A limit the platform does not name stays null and is not
/// checked beforehand — the platform is the judge of it, and its refusal fails the send (SPEC-003
/// §4.9.3). Lengths are counted in UTF-16 code units (<see cref="string.Length"/>).
/// </summary>
internal sealed record ActionButtonLimits
{
    /// <summary>
    /// Whether the form the adapter draws the buttons in has link buttons. False — every link button
    /// is left out with a warning.
    /// </summary>
    public bool DrawsLinks { get; init; } = true;

    /// <summary>
    /// Largest number of buttons the form holds; the first ones in the order of the list are kept.
    /// Null — the platform names no limit.
    /// </summary>
    public int? MaxButtons { get; init; }

    /// <summary>
    /// Largest length of a button label; a longer label is cut. Null — the platform names no limit.
    /// </summary>
    public int? MaxLabelLength { get; init; }

    /// <summary>
    /// Largest length of the payload of a callback button, the ownership marker included; a button
    /// with a longer payload is left out. Null — the platform names no limit.
    /// </summary>
    public int? MaxCallbackDataLength { get; init; }

    /// <summary>
    /// Largest length of the address of a link button; a button with a longer address is left out.
    /// Null — the platform names no limit.
    /// </summary>
    public int? MaxUrlLength { get; init; }
}
