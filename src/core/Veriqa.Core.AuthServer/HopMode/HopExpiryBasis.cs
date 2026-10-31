// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Which expiries the check of a presented hop token judges (SPEC-025 HOP-149): the method of the route
/// decides it, the rest of the check is the same for every method.
/// </summary>
internal enum HopExpiryBasis
{
    /// <summary>
    /// The own expiry of the token, then that of the transaction — the presentation of the URL
    /// (<c>GET</c>): a token past its own lifetime no longer leads into the channel.
    /// </summary>
    TokenAndTransaction,

    /// <summary>
    /// The expiry of the transaction alone — a client signal of the launch page (<c>POST</c>, HOP-190):
    /// the page was opened while the token was valid, and a tap made after its own lifetime is still an
    /// observation of a live transaction.
    /// </summary>
    TransactionOnly
}
