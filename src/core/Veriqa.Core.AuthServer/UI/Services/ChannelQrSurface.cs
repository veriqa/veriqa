// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.UI.Services;

/// <summary>
/// Where the QR code of a channel is shown. The two projections of a channel QR code differ in how long
/// a hop token carried by it lives (SPEC-025 HOP-149, HOP-153); a list shown without QR codes of its own
/// carries none at all.
/// </summary>
public enum ChannelQrSurface
{
    /// <summary>
    /// A page the core renders — the sign-in page and the channel choice page of a confirmation. A hop
    /// token lives its own configured lifetime, never longer than the transaction.
    /// </summary>
    CorePage,

    /// <summary>
    /// The answer of a server-to-server creation of a confirmation, shown on a screen of the integrator.
    /// A hop token lives as long as the transaction.
    /// </summary>
    CreationAnswer,

    /// <summary>
    /// Nowhere: the channels are shown as buttons only, without a QR code of their own — the page the
    /// phone that scanned the unified hop QR code gets (SPEC-025 HOP-145, HOP-146). No hop token is issued
    /// and no picture is drawn; <see cref="ChannelDisplayInfo.QrContent"/> and
    /// <see cref="ChannelDisplayInfo.QrCodeBase64"/> are empty.
    /// </summary>
    NotShown
}
