// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// Where the link the core asks a channel for is going to be used
/// (<c>IChannelAdapter.GetDeepLinkAsync</c>). The core names the purpose at every call; an adapter may
/// shape the link by it — through the message template of the purpose, for instance — or ignore it.
/// </summary>
/// <remarks>
/// The purpose does not change the channel the link leads to and does not tell sign-in apart from
/// confirmation: both kinds of transaction ask for the same purposes at the same places.
/// </remarks>
public enum DeepLinkPurpose
{
    /// <summary>
    /// A button on a page of the core, or the link handed to the relying party. The default.
    /// </summary>
    Button = 0,

    /// <summary>
    /// The target the hop resolve endpoint leads to after a hop QR code is scanned. A scan the core
    /// answers with a page of buttons (unified QR, Email that accepts an entered address under the
    /// Interstitial redirect style) gives those buttons <see cref="Button"/>.
    /// </summary>
    HopQr = 1,

    /// <summary>
    /// The content of a QR picture that carries the link itself, with no hop link in between.
    /// </summary>
    DirectQr = 2
}
