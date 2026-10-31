// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// The mail side of the email completion (SPEC-041 C96, L51): what the block of the web step asks of the
/// mail infrastructure of the Email adapter. The auth server and the Email adapter do not reference each
/// other, so the port lives here, where both see it; the Email adapter registers the one implementation,
/// a singleton, when it is added and enabled. Without a registration nothing can be sent or received
/// (<see cref="ClaimEmailCapabilities.None"/>), and only the <c>Unverified</c> method is available.
/// </summary>
/// <remarks>
/// Every operation works in the scope of the tenant of the transaction. No operation throws for a store
/// that cannot be reached: it answers with the outcome the member names for that case.
/// </remarks>
internal interface IClaimCompletionEmail
{
    /// <summary>
    /// What the mail infrastructure of a tenant can do for the email completion.
    /// </summary>
    /// <param name="tenantId">Tenant; null — the implicit tenant of a self-hosted deployment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The capabilities of the tenant.</returns>
    ValueTask<ClaimEmailCapabilities> GetCapabilitiesAsync(string? tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// The normalized form of an address the user entered; only a bare address is taken (see
    /// <see cref="ClaimEmailAddress.TakeBare"/>).
    /// </summary>
    /// <param name="raw">Address as entered.</param>
    /// <returns>The normalized address; null — the text is not a bare address.</returns>
    string? NormalizeAddress(string raw);

    /// <summary>
    /// Sends ONE completion mail — with a code, with a link or with both — within the limits of SPEC-041
    /// E61 and not sooner after the previous completion mail of the transaction than the minimal interval
    /// between mails the mail side is configured with. A mail with both is one mail and one reservation;
    /// its code and its link are live together and expire together.
    /// </summary>
    /// <param name="transaction">Transaction in the claims completion.</param>
    /// <param name="content">What the mail carries.</param>
    /// <param name="normalizedAddress">
    /// Recipient, normalized by <see cref="NormalizeAddress"/>; null — a repeat to the address of the last
    /// completion mail.
    /// </param>
    /// <param name="language">Language of the mail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Outcome of the send.</returns>
    Task<ClaimEmailSendOutcome> SendAsync(
        Transaction transaction,
        ClaimEmailMailContent content,
        string? normalizedAddress,
        string language,
        CancellationToken cancellationToken);

    /// <summary>
    /// Checks a code the user entered against the live code of the transaction, spending an attempt.
    /// </summary>
    /// <param name="transaction">Transaction in the claims completion.</param>
    /// <param name="code">Code as entered, six digits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the check.</returns>
    Task<ClaimEmailCodeCheck> CheckCodeAsync(Transaction transaction, string code, CancellationToken cancellationToken);

    /// <summary>
    /// State of the email completion of a transaction.
    /// </summary>
    /// <param name="transaction">Transaction in the claims completion.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The state; null — it is not known, the store could not be reached.</returns>
    Task<ClaimEmailState?> GetStateAsync(Transaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// Records the method the user chose for the transaction.
    /// </summary>
    /// <param name="transaction">Transaction in the claims completion.</param>
    /// <param name="method">Chosen method.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SelectMethodAsync(Transaction transaction, EmailCompletionMethod method, CancellationToken cancellationToken);

    /// <summary>
    /// Holds an address the email completion obtained until the form of the web step is finished, replacing
    /// the one held before; null lets the held address go — the user changes it.
    /// </summary>
    /// <param name="transaction">Transaction in the claims completion.</param>
    /// <param name="held">The address to hold; null — none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — written; <c>false</c> — the store could not be reached.</returns>
    Task<bool> HoldAddressAsync(Transaction transaction, ClaimEmailHeldAddress? held, CancellationToken cancellationToken);

    /// <summary>
    /// The address held for a transaction until the form of its web step is finished — read on its own, so
    /// nothing else the mail side keeps (the limits, the counters) can hide it.
    /// </summary>
    /// <param name="transaction">Transaction in the claims completion.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The held address, none, or <see cref="ClaimEmailHeldRead.Unknown"/> when the store could not
    /// be reached.</returns>
    Task<ClaimEmailHeldRead> GetHeldAddressAsync(Transaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// The <c>mailto:</c> link of the message the user sends for the <c>Push</c> method.
    /// </summary>
    /// <param name="transaction">Transaction in the claims completion.</param>
    /// <param name="language">Language of the subject and body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link; null — Push is not available to the tenant, or the correlation store failed.</returns>
    Task<string?> GetPushMailtoAsync(Transaction transaction, string language, CancellationToken cancellationToken);
}
