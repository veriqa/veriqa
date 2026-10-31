// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.ClaimCompletion;

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// The check of an entered code against the code of a mail, shared by the code of the claims completion and the
/// code of the sign-in mail (SPEC-041 R12, E61): the code is compared with its fingerprint in constant time, and
/// one attempt is then spent by a compare-and-swap on what was read, so two concurrent entries cannot both spend
/// the same attempt; a match spends every attempt left.
/// </summary>
internal static class EmailCodeCheck
{
    /// <summary>
    /// How many times a check re-reads the code when a concurrent check changed it meanwhile.
    /// </summary>
    private const int MaxRounds = 10;

    /// <summary>
    /// Checks an entered code and spends its attempt. A code that is gone or that <paramref name="isAdmitted"/>
    /// refuses is no code; an expired or exhausted code is answered as such; in none of these is anything spent.
    /// </summary>
    /// <typeparam name="TCode">The kind of the stored code.</typeparam>
    /// <param name="transactionId">The transaction the code belongs to — part of its fingerprint.</param>
    /// <param name="code">The entered code, in the shape of a code.</param>
    /// <param name="readCode">Reads the stored code; null — there is none.</param>
    /// <param name="isAdmitted">A further condition on a live code, checked before the attempt is spent; null — none.</param>
    /// <param name="trySpend">Spends the attempt on the code that was read: whether it matched, and the result of the compare-and-swap.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger of the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome of the check.</returns>
    public static async Task<Outcome<TCode>> RunAsync<TCode>(
        string transactionId,
        string code,
        Func<CancellationToken, Task<TCode?>> readCode,
        Func<TCode, DateTimeOffset, CancellationToken, Task<bool>>? isAdmitted,
        Func<TCode, bool, CancellationToken, Task<bool>> trySpend,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
        where TCode : class, IEmailStoredCode
    {
        var attemptsSeen = 0;

        for (var round = 0; round < MaxRounds; round++)
        {
            if (await readCode(cancellationToken) is not { } live)
            {
                return new Outcome<TCode>(ClaimEmailCodeStatus.NoCode, Code: null, AttemptsLeft: 0);
            }

            var now = timeProvider.GetUtcNow();

            if (now >= live.ExpiresAt)
            {
                return new Outcome<TCode>(ClaimEmailCodeStatus.Expired, live, live.AttemptsLeft);
            }

            if (live.AttemptsLeft <= 0)
            {
                return new Outcome<TCode>(ClaimEmailCodeStatus.Exhausted, live, AttemptsLeft: 0);
            }

            if (isAdmitted is not null && !await isAdmitted(live, now, cancellationToken))
            {
                return new Outcome<TCode>(ClaimEmailCodeStatus.NoCode, Code: null, AttemptsLeft: 0);
            }

            attemptsSeen = live.AttemptsLeft;
            var matched = EmailClaimSecrets.CodeMatches(live.Hash, transactionId, code);

            if (!await trySpend(live, matched, cancellationToken))
            {
                continue;
            }

            if (matched)
            {
                return new Outcome<TCode>(ClaimEmailCodeStatus.Matched, live, AttemptsLeft: 0);
            }

            var attemptsLeft = live.AttemptsLeft - 1;

            return attemptsLeft > 0
                ? new Outcome<TCode>(ClaimEmailCodeStatus.Mismatch, live, attemptsLeft)
                : new Outcome<TCode>(ClaimEmailCodeStatus.Exhausted, live, AttemptsLeft: 0);
        }

        // The code kept changing under this check: nothing was spent, and nothing is granted.
        logger.LogWarning(
            "A code of a mail could not be checked: it kept changing under the check. TransactionId: {TransactionId}",
            transactionId);

        return new Outcome<TCode>(ClaimEmailCodeStatus.Mismatch, Code: null, attemptsSeen);
    }

    /// <summary>
    /// What a check of a code came to.
    /// </summary>
    /// <typeparam name="TCode">The kind of the stored code.</typeparam>
    /// <param name="Status">The outcome of the check.</param>
    /// <param name="Code">The stored code the outcome was decided on; null when there was none to decide on.</param>
    /// <param name="AttemptsLeft">Attempts the code has left after the check.</param>
    public readonly record struct Outcome<TCode>(ClaimEmailCodeStatus Status, TCode? Code, int AttemptsLeft)
        where TCode : class, IEmailStoredCode;
}
