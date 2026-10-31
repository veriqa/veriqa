// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Enums;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// How hop tokens are issued for one act, as the canonical resolver answers the issuance keys of
/// SPEC-025 §11.1 for the caller's full resolution context.
/// </summary>
/// <param name="QrMode">
/// One hop QR code per channel, one for the transaction, or both — <see cref="HopQrMode.PerChannel"/>,
/// <see cref="HopQrMode.Unified"/>, <see cref="HopQrMode.UnifiedWithChannels"/> (SPEC-025 HOP-182). This is
/// the value of the key; whether the unified mode takes effect for a transaction, and whether its page keeps
/// the per-channel QR codes, is decided by <see cref="HopUnifiedMode"/> alone.
/// </param>
/// <param name="ShowChannelButtonsOnAnyChannelTab">
/// Whether the "Any channel" tab shows the buttons of the channels beside the unified QR code (SPEC-025 HOP-187).
/// This is the value of the key; it means something only on a page that keeps the per-channel QR codes, as
/// <see cref="HopUnifiedMode"/> decides.
/// </param>
/// <param name="TokenLifetime">
/// Own lifetime of a hop token (SPEC-025 HOP-151); null — the token lives as long as the transaction.
/// </param>
internal sealed record HopIssuanceSettings(
    HopQrMode QrMode,
    bool ShowChannelButtonsOnAnyChannelTab,
    TimeSpan? TokenLifetime);
