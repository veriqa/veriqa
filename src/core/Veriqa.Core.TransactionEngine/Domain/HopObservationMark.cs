// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// One-time observation marks a hop link leaves on its transaction (SPEC-025 HOP-071a, HOP-190).
/// Each mark is a moment written once, by the first writer, and never overwritten.
/// </summary>
public enum HopObservationMark
{
    /// <summary>
    /// First human resolve of the hop link (<see cref="Transaction.HopFirstResolvedAt"/>).
    /// </summary>
    FirstResolve,

    /// <summary>
    /// The interstitial page was shown (<see cref="Transaction.HopInterstitialOpenedAt"/>).
    /// </summary>
    InterstitialOpened,

    /// <summary>
    /// The browser started the transition into the channel, by itself or by a tap
    /// (<see cref="Transaction.HopChannelLaunchedAt"/>).
    /// </summary>
    ChannelLaunched,

    /// <summary>
    /// A human resolve of the hop link found the first resolve already marked and came from a browser
    /// without a valid browser mark (<see cref="Transaction.HopRepeatResolvedAt"/>).
    /// </summary>
    RepeatResolve
}
