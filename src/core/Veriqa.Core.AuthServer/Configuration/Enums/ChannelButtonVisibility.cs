// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Configuration.Enums;

/// <summary>
/// Visibility of the channel button on the sign-in page — the button that takes the user into a
/// channel by its deep link ("Sign in with …", "Confirm in …", "Send confirmation email"). The counterpart of
/// <see cref="QrCodeVisibility"/>: the QR code is the way in from ANOTHER device, the button is the
/// way in on THIS one. The value is a request to the page, for the same reason the QR visibility is:
/// only the browser knows the device, so the page itself decides what to show on it.
/// </summary>
public enum ChannelButtonVisibility
{
    /// <summary>
    /// The button is shown on every device. The default — the behavior of a deployment that
    /// configures nothing.
    /// </summary>
    Always,

    /// <summary>
    /// The button is shown on a phone only. Every other device — a desktop, a television, a kiosk —
    /// is left with the QR code, which is scanned with a phone. A tablet counts as not a phone, as it
    /// does for <see cref="QrCodeVisibility.DesktopOnly"/>.
    /// </summary>
    MobileOnly,

    /// <summary>
    /// The button is never shown: the QR code is the only way into a channel, on any device.
    /// </summary>
    Never
}
