// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.CollectedClaims;

/// <summary>
/// Whether a value from the collected claims store still holds for a transaction (SPEC-041 L58): the rules a
/// value is accepted by live with the catalog of the claims and the configuration of the ways the email is
/// obtained, and both may have changed since the value was collected, or differ for the application of this
/// sign-in. A value the rules refuse is asked for again, as if it had never been kept.
/// </summary>
/// <remarks>
/// Internal seam: the planner of the claims completion asks it, the auth server — the owner of the catalog
/// and of the rules of the form — implements it. Without a registration no stored value is accepted.
/// </remarks>
internal interface IStoredClaimValueRules
{
    /// <summary>
    /// Whether a stored value is valid for the transaction by the rules in force now.
    /// </summary>
    /// <param name="transaction">Transaction being confirmed; its ownership context is the one the rules are
    /// resolved in.</param>
    /// <param name="stored">Stored record carrying a value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true — the value is handed out by this transaction instead of being asked for.</returns>
    ValueTask<bool> IsAcceptableAsync(
        Transaction transaction,
        ClaimCompletionResult stored,
        CancellationToken cancellationToken);
}
