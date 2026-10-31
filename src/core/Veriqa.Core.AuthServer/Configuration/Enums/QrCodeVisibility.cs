// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Configuration.Enums;

/// <summary>
/// Visibility of the QR code on the sign-in page (SPEC-012 §9, presentational group SPEC-003 §17.3).
/// The value is a request to the page: only the browser knows the device, so the page itself decides
/// what to show on it (SPEC-007 UI-020). The one server-side consequence is the layout of
/// <see cref="Never"/>, which does not depend on the device.
/// </summary>
public enum QrCodeVisibility
{
    /// <summary>
    /// The QR code is shown on every device, and several channels share the page the same way
    /// everywhere: behind tabs, or all at once under <see cref="ChannelLayout.Grid"/>.
    /// </summary>
    Always,

    /// <summary>
    /// The QR code is shown everywhere except a phone, where scanning one's own screen is
    /// impossible: there the channels are laid out as buttons, one per channel, with no tabs. A
    /// tablet counts as a desktop here: the QR is scanned by ANOTHER device's camera, so a doubtful
    /// device keeps the QR rather than losing it. The default — the behavior of a deployment that
    /// configures nothing (SPEC-025 HOP-147).
    /// </summary>
    DesktopOnly,

    /// <summary>
    /// The QR code is never shown — only the channel buttons, on any device (SPEC-025 HOP-146).
    /// Applies unconditionally, before any device check, and the page lays the buttons out without
    /// a script.
    /// </summary>
    Never
}
