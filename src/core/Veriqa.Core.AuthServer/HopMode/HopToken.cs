// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// What a hop token carries — exactly these four fields, all inside the protected payload, so the bearer
/// can alter neither the lifetime nor the issue mark (SPEC-025 HOP-150). No target URL travels in the
/// token: the resolve endpoint re-derives it from the channel adapter (SPEC-025 HOP-040).
/// </summary>
/// <param name="TransactionId">Transaction the token leads to.</param>
/// <param name="ChannelType">
/// Channel type the token was issued for; never null. An empty string is the token of the unified QR
/// code, issued for the transaction rather than for one channel.
/// </param>
/// <param name="IssuedAt">Moment of issue, UTC, carried with a precision of one second.</param>
/// <param name="ExpiresAt">
/// Own expiry of the token (SPEC-025 HOP-149), UTC, carried with a precision of one second. The codec
/// does not judge it: whether the token has expired is decided by whoever presents it.
/// </param>
internal sealed record HopToken(
    TransactionId TransactionId,
    string ChannelType,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
