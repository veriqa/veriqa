// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.WebProviders.State;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Pages;

/// <summary>
/// The page of one session as it is read for a request: the session the mark of the browser names,
/// its transaction and what the page shows.
/// </summary>
/// <param name="Session">The session.</param>
/// <param name="Transaction">The transaction as the store holds it; <c>null</c> — it is gone.</param>
/// <param name="View">What the page shows.</param>
internal sealed record WebProviderSessionPage(
    WebProviderPageSession Session,
    Transaction? Transaction,
    WebProviderPageView View)
{
    /// <summary>
    /// The address of the claim form the page sends the browser to instead of showing itself: the current
    /// message is the link to the form, and the transaction still waits for the claims. In a chat the link
    /// is the way from the chat to the form; on this page a button that only opens the form is a step
    /// with nothing to decide. The form sends the browser back here once the transaction no longer waits
    /// for the claims, so the two never send it to each other.
    /// </summary>
    /// <returns>The address; <c>null</c> — the page shows itself.</returns>
    public string? FormToOpen() =>
        View.Feed is [{ ClaimFormLink: { } link }]
        && WebProviderFeedRules.IsDrawable(link)
        && Transaction is { } transaction
        && transaction.IsCompletingClaims()
            ? link.Url
            : null;
}
