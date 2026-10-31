// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;

using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// What every page answering a link of a letter is rendered from, read once per request: the transaction
/// the link names, as the store holds it, and the branding, the text overrides and the language that
/// transaction gives the page. What a letter is about does not run out with its transaction — the type,
/// the application and the language stay those of the record past its deadline too, while whether the
/// transaction can still be answered is read off the same record where it decides something
/// (<see cref="Transaction.HasRunOutOfTime"/>) and checked again by the write itself.
/// </summary>
/// <param name="Transaction">The transaction of the letter; null — none could be read.</param>
/// <param name="Branding">Branding of the page, resolved for that transaction.</param>
/// <param name="Texts">Overrides of the page's built-in texts, resolved for that transaction.</param>
/// <param name="Locale">Language of the page: the one the transaction states (<c>OidcContext.UiLocale</c>),
/// otherwise the one detected from Accept-Language; null — the base language.</param>
internal sealed record EmailLetterPage(
    Transaction? Transaction,
    CorePageBranding Branding,
    CorePageTexts Texts,
    string? Locale)
{
    /// <summary>
    /// Whether the letter is of a CONFIRMATION transaction — one this adapter asks no question of its own
    /// about (SPEC-039 E29), whose request leads to the core page.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Transaction))]
    public bool IsConfirmation =>
        Transaction is { } transaction
        && string.Equals(transaction.Type, TransactionTypes.Confirmation, StringComparison.Ordinal);
}
