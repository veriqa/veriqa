// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// One counter a claims-completion mail is reserved against (SPEC-041 E61).
/// </summary>
/// <param name="Key">
/// Opaque key of the counter; it names neither an address nor a channel account in clear text.
/// </param>
/// <param name="Limit">Mails the counter allows within its window.</param>
/// <param name="Window">
/// Length of the window, fixed from the first mail the counter counts; the counter is gone when it ends.
/// </param>
internal sealed record EmailClaimMailCounter(string Key, int Limit, TimeSpan Window)
{
    /// <summary>
    /// Mails a counter of the interval admits within one interval.
    /// </summary>
    private const int MailsPerResendInterval = 1;

    /// <summary>
    /// The counter that holds two mails of one transaction apart: one mail within a window that is the
    /// interval itself — counted by the mail that goes out and gone when the interval has passed. The one
    /// place the rule is spelled: the sign-in mail and the mails of the claims completion each keep such a
    /// counter under a kind of their own, so neither holds the other back, and both follow the same rule.
    /// </summary>
    /// <param name="kind">Kind of the counter — what mails it holds apart.</param>
    /// <param name="transactionId">The transaction whose mails are held apart.</param>
    /// <param name="interval">Interval between two mails; zero or less switches the interval off.</param>
    /// <returns>The counter; null — the interval is off.</returns>
    public static EmailClaimMailCounter? OfResendInterval(string kind, TransactionId transactionId, TimeSpan interval) =>
        interval > TimeSpan.Zero
            ? new EmailClaimMailCounter(EmailClaimSecrets.CounterKey(kind, transactionId.ToString()), MailsPerResendInterval, interval)
            : null;
}
