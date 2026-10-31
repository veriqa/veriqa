// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// The five hop observations of a transaction as one value — the unit a store that keeps them apart
/// from the transaction record reads, writes and lays back over the record.
/// </summary>
/// <param name="ScannerContextSnapshot">Context of the scanning device (see <see cref="Transaction.ScannerContextSnapshot"/>).</param>
/// <param name="FirstResolvedAt">First human resolve (see <see cref="Transaction.HopFirstResolvedAt"/>).</param>
/// <param name="InterstitialOpenedAt">Interstitial page shown (see <see cref="Transaction.HopInterstitialOpenedAt"/>).</param>
/// <param name="ChannelLaunchedAt">
/// Start of the transition into the channel (see <see cref="Transaction.HopChannelLaunchedAt"/>).
/// </param>
/// <param name="RepeatResolvedAt">Repeat human resolve from another browser (see <see cref="Transaction.HopRepeatResolvedAt"/>).</param>
internal readonly record struct HopObservations(
    ScannerContextSnapshot? ScannerContextSnapshot,
    DateTimeOffset? FirstResolvedAt,
    DateTimeOffset? InterstitialOpenedAt,
    DateTimeOffset? ChannelLaunchedAt,
    DateTimeOffset? RepeatResolvedAt);
