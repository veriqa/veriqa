// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// Store of the email completion (SPEC-041 R12, L50, E61): per transaction — the chosen method, the last
/// code, the last link, the correlation token of <c>Push</c>, the address held until the form of the web
/// step is finished, and the code of the last sign-in mail
/// (SPEC-016 §4); across transactions — the mail counters, those of the sign-in mail among them. The in-process default serves one instance; a deployment of several replicas
/// uses the Redis one, so a code sent by one replica is checked by another.
/// </summary>
/// <remarks>
/// CONTRACT for every implementation: each operation below is ATOMIC on its own, the way
/// <c>IEmailActionTokenStore.MarkUsedAsync</c> is — out of concurrent calls exactly one wins a
/// reservation that has room for one mail, one spends the last attempt of a code, one uses a link.
/// A record lives until the transaction it belongs to expires.
/// </remarks>
internal interface IEmailClaimCompletionStore
{
    /// <summary>
    /// Reads the record of a transaction.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record; null — nothing was kept for the transaction.</returns>
    Task<EmailClaimCompletionRecord?> GetAsync(string transactionId, CancellationToken cancellationToken);

    /// <summary>
    /// Reserves one mail against every counter at once: either each counter counts it, or none does.
    /// </summary>
    /// <param name="counters">Counters the mail is counted by, in the order refusals are reported.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Null — reserved; otherwise the index of the first counter that has no room left.</returns>
    Task<int?> TryReserveMailAsync(IReadOnlyList<EmailClaimMailCounter> counters, CancellationToken cancellationToken);

    /// <summary>
    /// Reads how many mails counters have counted within their current windows.
    /// </summary>
    /// <param name="keys">Keys of the counters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counts, in the order of <paramref name="keys"/>; zero for a counter with no window.</returns>
    Task<IReadOnlyList<int>> ReadMailCountsAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken);

    /// <summary>
    /// Makes a code the live one of a transaction; the code before it is no longer accepted.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="code">The code that takes effect.</param>
    /// <param name="recordExpiresAt">Moment the transaction, and with it the record, expires.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetCodeAsync(
        string transactionId,
        EmailClaimCode code,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Spends one attempt of the live code — a compare-and-swap: it succeeds only while the live code is
    /// still the one that was read and still has the attempts that were read, so two concurrent entries
    /// cannot both spend the same attempt.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="codeHash">Fingerprint of the code that was read.</param>
    /// <param name="attemptsSeen">Attempts the code had when it was read.</param>
    /// <param name="matched">Whether the entry matched: a match spends every attempt left, the code is done.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the attempt was spent by this call; <c>false</c> — the code changed meanwhile.</returns>
    Task<bool> TrySpendCodeAttemptAsync(
        string transactionId,
        string codeHash,
        int attemptsSeen,
        bool matched,
        CancellationToken cancellationToken);

    /// <summary>
    /// Makes a code of a sign-in mail the live one of a transaction; the code of the sign-in mail before it is
    /// no longer accepted. The code of the claims completion is not touched.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="code">The code that takes effect.</param>
    /// <param name="recordExpiresAt">Moment the transaction, and with it the record, expires.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetSignInCodeAsync(
        string transactionId,
        EmailSignInCode code,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Spends one attempt of the live code of a sign-in mail — the compare-and-swap of
    /// <see cref="TrySpendCodeAttemptAsync"/>, over the code of the sign-in mail.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="codeHash">Fingerprint of the code that was read.</param>
    /// <param name="attemptsSeen">Attempts the code had when it was read.</param>
    /// <param name="matched">Whether the entry matched: a match spends every attempt left, the code is done.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the attempt was spent by this call; <c>false</c> — the code changed meanwhile.</returns>
    Task<bool> TrySpendSignInCodeAttemptAsync(
        string transactionId,
        string codeHash,
        int attemptsSeen,
        bool matched,
        CancellationToken cancellationToken);

    /// <summary>
    /// Makes a link the live one of a transaction; the token of the link before it is no longer accepted.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="link">The link that takes effect.</param>
    /// <param name="recordExpiresAt">Moment the transaction, and with it the record, expires.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetLinkAsync(
        string transactionId,
        EmailClaimLink link,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the live link a token belongs to, changing nothing — what the confirmation page shows before
    /// the user acts on it (SPEC-016 EM-010, EM-011).
    /// </summary>
    /// <param name="tokenHash">Fingerprint of the token (<see cref="EmailClaimSecrets.HashOfToken"/>).</param>
    /// <param name="now">Current moment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link; null — the token is not the live link of any transaction.</returns>
    Task<EmailClaimLinkUse?> FindLiveLinkAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Uses the live link a token belongs to — once: of concurrent uses exactly one gets the link.
    /// </summary>
    /// <param name="tokenHash">Fingerprint of the token (<see cref="EmailClaimSecrets.HashOfToken"/>).</param>
    /// <param name="now">Current moment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link this call used; null — the token is not a live link, or it was used meanwhile.</returns>
    Task<EmailClaimLinkUse?> TryUseLinkAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records the method the user chose for a transaction.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="method">Chosen method.</param>
    /// <param name="recordExpiresAt">Moment the transaction, and with it the record, expires.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetSelectedMethodAsync(
        string transactionId,
        EmailCompletionMethod method,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the correlation token of the <c>Push</c> method issued for a transaction.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="token">The token and its expiry.</param>
    /// <param name="recordExpiresAt">Moment the transaction, and with it the record, expires.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetPushTokenAsync(
        string transactionId,
        EmailClaimPushToken token,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Holds the address the email completion obtained for a transaction until the form of its web step is
    /// finished, replacing the one held before; null lets the held address go.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="held">The address to hold; null — none.</param>
    /// <param name="recordExpiresAt">Moment the transaction, and with it the record, expires.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetHeldAddressAsync(
        string transactionId,
        EmailClaimHeldAddress? held,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken);
}
