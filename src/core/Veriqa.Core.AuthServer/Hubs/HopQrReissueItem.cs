// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Hubs;

/// <summary>
/// One entry of the answer of <see cref="AuthTransactionHub.ReissueHopQrAsync"/> — the replacement of one hop
/// QR code of a page (SPEC-025 HOP-152). An entry the hub leaves out of the answer is one the page stops
/// re-issuing: its QR code stays as it is.
/// </summary>
/// <param name="Entry">
/// The entry as the page asked for it: the channel type of a per-channel QR code, spelled as in the request,
/// or an empty string for the unified QR code.
/// </param>
/// <param name="Url">The new hop URL; empty when <paramref name="Failed"/>.</param>
/// <param name="QrCodeBase64">
/// PNG of the new QR code in Base64, without a data URI prefix — the form of
/// <see cref="Veriqa.Core.AuthServer.UI.Services.ChannelDisplayInfo.QrCodeBase64"/>; empty when
/// <paramref name="Failed"/>.
/// </param>
/// <param name="RemainingMs">
/// What is left of the own lifetime of the new token by the clock of the server, in milliseconds; never
/// negative, zero when <paramref name="Failed"/>.
/// </param>
/// <param name="ExpiresBeforeTransaction">
/// True — the new token expires before the transaction, so the page replaces it again; false — it lives as
/// long as the transaction, and the re-issue of this entry is over.
/// </param>
/// <param name="Failed">
/// True — the replacement of this entry could not be made this time; the page keeps its QR code and asks
/// again later.
/// </param>
public sealed record HopQrReissueItem(
    string Entry,
    string Url,
    string QrCodeBase64,
    long RemainingMs,
    bool ExpiresBeforeTransaction,
    bool Failed);
