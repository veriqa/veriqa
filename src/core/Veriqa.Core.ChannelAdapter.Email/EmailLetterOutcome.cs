// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// What a letter of a sign-in or of a confirmation came to once it passed the gate of a letter (SPEC-016 EM-205,
/// EM-206) — decided once, whichever way the letter was answered: by its link, on the page the link opens, or by
/// its code, on the page the address was typed into. Each way only says it in its own form: the page of the link
/// as HTML, the entry of the code as JSON. What a decision writes — the attachment of the mailbox, the spending
/// of the letter, the mark the question of a confirmation asks for — is written by the decision, not by the form.
/// </summary>
internal abstract record EmailLetterOutcome
{
    /// <summary>
    /// Only the outcomes below.
    /// </summary>
    private EmailLetterOutcome()
    {
    }

    /// <summary>
    /// The letter was refused; nothing of it was written, and a letter of a sign-in stays unspent — unless a
    /// parallel request spent it first.
    /// </summary>
    /// <param name="StatusCode">The status the page of the link answers the refusal with.</param>
    /// <param name="LetterUsed">The letter was spent by a parallel request, and the refusal is that of a used
    /// link; otherwise it is the error of the page, with no detail of the refusal.</param>
    internal sealed record Refused(int StatusCode, bool LetterUsed = false) : EmailLetterOutcome;

    /// <summary>
    /// The mailbox is attached to the sign-in and the letter is spent; the sign-in window asks the question.
    /// </summary>
    internal sealed record SignInContinued : EmailLetterOutcome;

    /// <summary>
    /// The letter completed the sign-in and is spent.
    /// </summary>
    /// <param name="ReceiptText">The receipt of the outcome, resolved and localized by the core.</param>
    internal sealed record Completed(string ReceiptText) : EmailLetterOutcome;

    /// <summary>
    /// The fast link confirmed the sign-in into the claims completion while its form waits on this device: the
    /// letter is not spent, and the page offers one "Continue" button that posts it (SPEC-041 N78).
    /// </summary>
    internal sealed record ClaimFormContinue : EmailLetterOutcome;

    /// <summary>
    /// The sign-in waits for missing claims with the form on the device that confirmed it, which is the device
    /// of this request; the letter is spent, and the form is opened here.
    /// </summary>
    /// <param name="TransactionId">The transaction in the claims completion.</param>
    internal sealed record ClaimFormOnThisDevice(TransactionId TransactionId) : EmailLetterOutcome;

    /// <summary>
    /// The sign-in is confirmed into the claims completion and the letter is spent; the user goes on on the
    /// device the sign-in was started on.
    /// </summary>
    internal sealed record ContinueOnStartDevice : EmailLetterOutcome;

    /// <summary>
    /// The confirmation is handed over to the core page that asks its subject, with the answer bound to this
    /// browser, which holds the mark the page asks for.
    /// </summary>
    /// <param name="TransactionId">The confirmation transaction.</param>
    internal sealed record HandedOverToQuestion(TransactionId TransactionId) : EmailLetterOutcome;
}
