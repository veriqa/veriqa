// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The unified hop QR code of a page, drawn: its picture, the own expiry of the token it carries, whether the
/// page keeps the per-channel QR codes beside it and whether its "Any channel" tab shows the channel buttons.
/// </summary>
/// <param name="QrCodeBase64">Base64 PNG of the QR code, without a data URI prefix.</param>
/// <param name="ExpiresAt">Own expiry of the unified hop token the QR code carries (SPEC-025 HOP-149).</param>
/// <param name="WithChannelQrCodes">
/// Whether the page keeps the per-channel QR codes as well — a tab with the unified QR code followed by a tab
/// per channel — as <see cref="HopUnifiedMode.ResolvePageLayoutAsync"/> answers it.
/// </param>
/// <param name="AnyChannelTabShowsChannelButtons">
/// Whether the "Any channel" tab shows the buttons of the channels beside the unified QR code (SPEC-025 HOP-187),
/// as <see cref="HopUnifiedMode.ResolvePageLayoutAsync"/> answers it; it means nothing when
/// <paramref name="WithChannelQrCodes"/> is <c>false</c>.
/// </param>
internal sealed record HopUnifiedQrImage(
    string QrCodeBase64,
    DateTimeOffset ExpiresAt,
    bool WithChannelQrCodes,
    bool AnyChannelTabShowsChannelButtons);
