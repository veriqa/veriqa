// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// What a hop token is issued for: the transaction, the channel and the projection that shows its QR code.
/// </summary>
/// <param name="TransactionId">Transaction the token leads to.</param>
/// <param name="TransactionExpiresAt">Expiry of the transaction — the upper bound of the token's own expiry.</param>
/// <param name="ChannelType">
/// Channel the token is issued for; an empty string is the token of the unified QR code, issued for the
/// transaction rather than for one channel.
/// </param>
/// <param name="Resolution">Resolution context of the act, passed to the reading of the hop settings as is.</param>
/// <param name="Surface">Projection that shows the QR code; it decides the token's expiry (SPEC-025 HOP-153).</param>
internal sealed record HopIssueRequest(
    TransactionId TransactionId,
    DateTimeOffset TransactionExpiresAt,
    string ChannelType,
    ResolutionContext Resolution,
    ChannelQrSurface Surface);
