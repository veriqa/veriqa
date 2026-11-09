// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// The subject of a confirmation as a page of the contour shows it — the entry page of a confirmation
/// transaction and the hop launch page ask here, so the subject reads the same on every surface that
/// shows it (SPEC-039 R38, SPEC-025 HOP-069a). Whether a surface shows it at all is not decided here: that
/// is the set of subject-display surfaces (SPEC-012 §4.11), asked by the caller.
/// </summary>
internal static class ConfirmationSubjectText
{
    /// <summary>
    /// The modifier of the instruction primitive the subject line carries; the stylesheets of the sign-in
    /// window and of the service pages declare it under the same name.
    /// </summary>
    public const string ModifierClass = "veriqa-instruction--subject";

    /// <summary>
    /// Resolves the subject of the confirmation.
    /// </summary>
    /// <remarks>
    /// The wording is asked of the SAME factory the page that asks the question asks: one engine of
    /// substitution, one set of render protections, one place they have to be right (SPEC-039 R8). The one
    /// asked is the account of the snapshot of the transaction (<see cref="RecipientOf"/>) — none before
    /// the channel attached it — and the surface is a page of the core, so the assembly of the values
    /// decides whether the subject may name the account; a second entry point "give me the subject text"
    /// is the second implementation this must not create.
    /// <para>
    /// A subject that cannot be worded is NOT fatal and does not end the transaction: the pages that
    /// show it lead into the channel, the showing of the subject is an addition to that, and E28
    /// belongs to the surface that would otherwise ask a question with nothing in it.
    /// </para>
    /// </remarks>
    /// <param name="promptContextFactory">Factory of the confirmation context.</param>
    /// <param name="transaction">Transaction the page belongs to.</param>
    /// <param name="language">Language of the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subject as plain text, or null when it cannot be worded.</returns>
    public static async Task<string?> ResolveAsync(
        IConfirmationPromptContextFactory promptContextFactory,
        Transaction transaction,
        string language,
        CancellationToken cancellationToken)
    {
        var promptContext = await promptContextFactory.CreateAsync(
            transaction,
            RecipientOf(transaction),
            language,
            MessageSurfaces.CorePage,
            cancellationToken);

        return promptContext is SubjectConfirmationPromptContext subject ? subject.PromptText : null;
    }

    /// <summary>
    /// The one a question shown on a page of the core is asked of: the account of the channel snapshot of
    /// the transaction. A transaction no channel has attached a snapshot to yet names no channel and no
    /// account.
    /// </summary>
    /// <param name="transaction">Transaction the page belongs to.</param>
    /// <returns>The recipient of the question.</returns>
    public static ConfirmationPromptRecipient RecipientOf(Transaction transaction)
    {
        var snapshot = transaction.ChannelIdentitySnapshot;

        return new ConfirmationPromptRecipient(
            snapshot?.ChannelType ?? string.Empty,
            snapshot?.ChannelUserId ?? string.Empty,
            snapshot);
    }

    /// <summary>
    /// The line of the subject on a page — the text primitive of the card the instruction uses, with the
    /// modifier that sets it as the first sentence of the card (<see cref="ModifierClass"/>): the action
    /// the person is asked about reads larger than the instruction under it. The wording is HTML-encoded
    /// on output (it arrives plain from the engine that produced it).
    /// </summary>
    /// <remarks>
    /// The subject is worded in the language it was asked in, and the page's own texts fall back to the
    /// English base text as a whole when the locale files do not carry that language. The subject does
    /// not follow that fallback — it is the calling party's wording, not a text of the page — so when
    /// the two languages differ, the line states its own <c>lang</c> (WCAG 3.1.2) instead of being read
    /// out in the language of the page.
    /// </remarks>
    /// <param name="subjectText">The subject as plain text; null or empty — no line.</param>
    /// <param name="subjectLanguage">Language the subject was worded in.</param>
    /// <param name="pageLanguage">Language of the page's own texts (the <c>lang</c> of its root).</param>
    /// <returns>Markup of the line, or an empty string when there is no subject.</returns>
    public static string Markup(string? subjectText, string subjectLanguage, string pageLanguage)
    {
        if (string.IsNullOrEmpty(subjectText))
        {
            return string.Empty;
        }

        var languageAttribute = string.Equals(subjectLanguage, pageLanguage, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" lang=\"{WebUtility.HtmlEncode(subjectLanguage)}\"";

        return $"<p class=\"veriqa-instruction {ModifierClass}\"{languageAttribute}>{WebUtility.HtmlEncode(subjectText)}</p>";
    }
}
