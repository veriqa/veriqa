// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Configuration.Enums;

/// <summary>
/// Layout of the channels on a sign-in page that shows the QR code of each of them — how several
/// channels share the page. It is a question of ARRANGEMENT alone: which channels the page carries is
/// decided by <see cref="ChannelDisplayMode"/> and the channel set, and whether a QR code is shown at
/// all by <see cref="QrCodeVisibility"/>.
/// <para>
/// The setting acts where the page would otherwise show tabs: two channels or more, each with a QR
/// code of its own. A page of one channel, a page laid out as buttons (the QR code is hidden) and a
/// page with the unified QR code have nothing to arrange and are rendered the same under either value.
/// </para>
/// </summary>
public enum ChannelLayout
{
    /// <summary>
    /// One channel at a time: a tab strip over the panels, the panel of the selected tab on screen.
    /// The default — the behavior of a deployment that configures nothing.
    /// </summary>
    Tabs,

    /// <summary>
    /// Every channel at once: the panels stand side by side in a row that wraps, each under the name
    /// of its channel, and the card of the page grows to hold the row. Meant for a screen read from a
    /// distance — a television, a kiosk — where nobody switches tabs.
    /// </summary>
    Grid
}
