// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The two judgements of the re-issue of a hop QR code on a page (SPEC-025 HOP-152, HOP-155), shared by the
/// hub that issues the replacement and the resolve endpoint that observes a superseded token, so that the
/// two cannot disagree about the same token.
/// </summary>
internal static class HopReissueSchedule
{
    /// <summary>
    /// The moment after which a token is due for replacement: the share
    /// <see cref="HopModeConstants.ReissueFraction"/> of its own lifetime past its moment of issue.
    /// </summary>
    /// <param name="issuedAt">Moment of issue of the token.</param>
    /// <param name="expiresAt">Own expiry of the token.</param>
    /// <returns>The moment the token is due for replacement.</returns>
    public static DateTimeOffset DueAt(DateTimeOffset issuedAt, DateTimeOffset expiresAt) =>
        issuedAt + ((expiresAt - issuedAt) * HopModeConstants.ReissueFraction);

    /// <summary>
    /// Whether a token expires before its transaction — the only kind of token a page replaces. A token
    /// issued with the expiry of the transaction is not one: the codec carries the expiry in whole seconds,
    /// so such a token reads as expiring earlier by less than <see cref="HopModeConstants.ExpiryTolerance"/>.
    /// </summary>
    /// <param name="tokenExpiresAt">Own expiry of the token.</param>
    /// <param name="transactionExpiresAt">Expiry of the transaction the token names.</param>
    /// <returns>True — the token expires before the transaction.</returns>
    public static bool ExpiresBeforeTransaction(DateTimeOffset tokenExpiresAt, DateTimeOffset transactionExpiresAt) =>
        tokenExpiresAt < transactionExpiresAt - HopModeConstants.ExpiryTolerance;
}
