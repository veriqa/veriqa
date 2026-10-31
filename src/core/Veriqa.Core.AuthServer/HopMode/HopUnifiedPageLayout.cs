// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// How a page that shows the unified hop QR code lays out its channels, as <see cref="HopUnifiedMode"/> answers
/// it from one resolution of the issuance settings of the act.
/// </summary>
/// <param name="WithChannelQrCodes">
/// Whether the page keeps the per-channel QR codes as well — a tab with the unified QR code followed by a tab
/// per channel (SPEC-025 HOP-182).
/// </param>
/// <param name="AnyChannelTabShowsChannelButtons">
/// Whether the "Any channel" tab of such a page shows the buttons of the channels beside the unified QR code
/// (SPEC-025 HOP-187); it means nothing when <paramref name="WithChannelQrCodes"/> is <c>false</c>.
/// </param>
internal sealed record HopUnifiedPageLayout(bool WithChannelQrCodes, bool AnyChannelTabShowsChannelButtons);
