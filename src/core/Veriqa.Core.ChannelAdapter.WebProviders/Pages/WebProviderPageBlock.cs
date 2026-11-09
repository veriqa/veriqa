// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.WebProviders.Pages;

/// <summary>
/// Main block of the page of a web provider channel (<see cref="WebProviderPageView.Of"/>).
/// </summary>
internal enum WebProviderPageBlock
{
    /// <summary>
    /// The status text of the transaction, as the core rendered it.
    /// </summary>
    Outcome,

    /// <summary>
    /// The transaction ended in the core without an outcome shown here: a neutral line.
    /// </summary>
    Ended,

    /// <summary>
    /// The current message of the feed has the last word, and the page adds no text of its own: the
    /// receipt of a sign-in confirmed without a question, or the actions of a message (the menu or the
    /// button after a sign-in) that take the place of a stored outcome.
    /// </summary>
    Closed,

    /// <summary>
    /// The question to the account of the session, with its two answers.
    /// </summary>
    Question,

    /// <summary>
    /// The page waits for the question, a message or the outcome.
    /// </summary>
    Waiting
}
