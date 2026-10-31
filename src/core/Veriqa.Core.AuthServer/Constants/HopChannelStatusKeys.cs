// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// Intermediate channel statuses of the hop family — the codes that tell the sign-in window how far
/// the user got on the way from the QR code into the channel (SPEC-025 HOP-161), and whether the link
/// was opened again elsewhere (SPEC-041 L93). They travel in the
/// same <c>TransactionChannelStatusChangedEvent</c> and the same hub method as the Email statuses of
/// <see cref="EmailChannelStatusKeys"/>, and the page keeps ONE dictionary of hint texts for both
/// families — which is why no name here repeats an Email code.
/// <para>
/// The statuses are informative: none of them ends, advances or completes the transaction
/// (SPEC-025 HOP-102). The values are wire-level codes, as stable as the event that carries them.
/// </para>
/// </summary>
internal static class HopChannelStatusKeys
{
    /// <summary>
    /// The hop link of the QR code was presented to the server — the code was scanned. The event may
    /// carry no channel type, and the page shows its hint without one.
    /// </summary>
    public const string Scanned = "scanned";

    /// <summary>
    /// The intermediate page of the hop was opened on the phone.
    /// </summary>
    public const string InterstitialOpened = "interstitial_opened";

    /// <summary>
    /// The browser of the phone started the transition into the channel — by the automatic launch of the
    /// intermediate page or by a tap on a button into the channel; the event names the channel being
    /// opened. It does not say the channel's app opened, and it is not taken back when the transition led
    /// nowhere.
    /// </summary>
    public const string ChannelLaunched = "channel_launched";

    /// <summary>
    /// The hop link was opened again by a person from a browser without the mark of the first opening —
    /// possibly on another device (SPEC-041 L93). A risk signal: it grants and denies nothing, and the page
    /// shows it as a warning, not on the status line. Published once, by the resolve that wrote the mark
    /// of the reopening.
    /// </summary>
    public const string Reopened = "reopened";

    /// <summary>
    /// Every code of the family, in the order the user meets them. The page tells a hop code from an
    /// Email one by this set, never by the channel type of the event.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Scanned, InterstitialOpened, ChannelLaunched, Reopened];
}
