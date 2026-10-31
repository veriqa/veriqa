// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Configuration.Enums;

/// <summary>
/// How many hop QR codes a transaction shows (SPEC-025 HOP-182): one per channel, one for the
/// transaction, or both. Written in the configuration by the name of the member.
/// </summary>
public enum HopQrMode
{
    /// <summary>
    /// One QR code per channel. The default.
    /// </summary>
    PerChannel,

    /// <summary>
    /// One hop QR code for the whole transaction; the channel is chosen on the page it opens.
    /// </summary>
    Unified,

    /// <summary>
    /// The unified hop QR code of <see cref="Unified"/> together with the QR code of every channel: the page
    /// opens on a tab with the unified QR code and the buttons of all channels, followed by a tab per
    /// channel with its own QR code. Applies under the same condition as <see cref="Unified"/>.
    /// </summary>
    UnifiedWithChannels
}
