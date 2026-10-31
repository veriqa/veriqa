// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Constants;

/// <summary>
/// Well-known outcomes of a transaction as shown to the user (SPEC-003 §6.2): the four terminal
/// ones and the one non-terminal state of a claims completion. <c>Confirmed</c>/<c>Declined</c> mean
/// the user's will carried through to a terminal state; any branch where the core shows the user an
/// error is <c>Failed</c>; <c>AwaitingClaims</c> is not an end of the transaction at all.
/// </summary>
public static class TransactionOutcomeCodes
{
    /// <summary>
    /// The user confirmed and the transaction completed.
    /// </summary>
    public const string Confirmed = "confirmed";

    /// <summary>
    /// The user declined and the decline was recorded.
    /// </summary>
    public const string Declined = "declined";

    /// <summary>
    /// The transaction expired by TTL.
    /// </summary>
    public const string Expired = "expired";

    /// <summary>
    /// The transaction ended in an error shown to the user.
    /// </summary>
    public const string Failed = "failed";

    /// <summary>
    /// The user confirmed, and the transaction is not over: it waits for the claims the sign-in
    /// still lacks. The only code here that is not terminal.
    /// </summary>
    public const string AwaitingClaims = "awaiting_claims";
}
