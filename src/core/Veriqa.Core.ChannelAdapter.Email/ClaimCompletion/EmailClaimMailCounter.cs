// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.Contracts;
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
    /// Separator of the parts of the scope of a recipient counter: the tenant and the address or the domain.
    /// </summary>
    private const string RecipientScopeSeparator = ":";

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

    /// <summary>
    /// The counter of the mails to one recipient address within the tenant, across its transactions: the
    /// address in the comparison form of its normalization. The one place the rule is spelled: the sign-in
    /// mail and the mails of the claims completion each keep such a counter under a kind of their own.
    /// </summary>
    /// <param name="kind">Kind of the counter — what mails it counts.</param>
    /// <param name="tenantId">Tenant of the transaction; null — none.</param>
    /// <param name="address">Recipient address.</param>
    /// <param name="normalization">Normalization the address is compared by.</param>
    /// <param name="limit">Mails the counter allows within its window.</param>
    /// <returns>The counter.</returns>
    public static EmailClaimMailCounter OfRecipientAddress(
        string kind,
        string? tenantId,
        string address,
        EmailNormalizationOptions? normalization,
        int limit) =>
        new(
            EmailClaimSecrets.CounterKey(
                kind,
                string.Join(RecipientScopeSeparator, tenantId ?? string.Empty, EmailAddressHelper.ToComparisonForm(address, normalization))),
            limit,
            EmailClaimMailWindows.Address);

    /// <summary>
    /// The counter of the mails to one recipient domain within the tenant, across its transactions, under the
    /// same rule as <see cref="OfRecipientAddress"/>. The domain is counted in one form whatever way it is
    /// written: without the trailing dot, an internationalized name as its A-label, in lower case.
    /// </summary>
    /// <param name="kind">Kind of the counter — what mails it counts.</param>
    /// <param name="tenantId">Tenant of the transaction; null — none.</param>
    /// <param name="address">Recipient address.</param>
    /// <param name="limit">Mails the counter allows within its window; zero or less switches the limit off.</param>
    /// <returns>The counter; null — the limit is off or the address has no domain.</returns>
    public static EmailClaimMailCounter? OfRecipientDomain(string kind, string? tenantId, string address, int limit) =>
        limit > 0 && EmailChannelAdapter.ExtractDomain(address) is { } domain
            ? new EmailClaimMailCounter(
                EmailClaimSecrets.CounterKey(kind, string.Join(RecipientScopeSeparator, tenantId ?? string.Empty, EmailDomain.ToCanonical(domain))),
                limit,
                EmailClaimMailWindows.Domain)
            : null;
}
