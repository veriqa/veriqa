// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// What the email block of the web step of the claims completion shows (SPEC-041 L108, C96): the way of
/// obtaining the email the block is on, the other ways the user may switch to, how far the mails of that way
/// have got and the error of the last action — or, once the email is recorded, the recorded address.
/// </summary>
/// <remarks>
/// Built by the page that knows the transaction; the block itself reads nothing but this model. The error is
/// already localized; every other text the block localizes itself.
/// </remarks>
internal sealed record ClaimCompletionEmailBlockView
{
    /// <summary>
    /// Language the texts of the block are served in — the content language of the page.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Path the actions of the block are posted to, without the path base.
    /// </summary>
    public required string ActionPath { get; init; }

    /// <summary>
    /// Whether the email is required; an optional email is skipped by "Continue" of the form with nothing
    /// typed into the block (L108).
    /// </summary>
    public required bool Required { get; init; }

    /// <summary>
    /// The way the block is on: the one the user chose while it is still available, otherwise the main one;
    /// null — no way is available on this surface. <see cref="EmailCompletionMethod.Code"/> and
    /// <see cref="EmailCompletionMethod.MagicLink"/> both stand for the one way "confirm via email"; which
    /// of them names it carries no meaning for the block — <see cref="MailContent"/> does.
    /// </summary>
    public EmailCompletionMethod? Current { get; init; }

    /// <summary>
    /// The other available ways, in the order of the effective list (C96); the way "confirm via email" is
    /// among them once at most.
    /// </summary>
    public required IReadOnlyList<EmailCompletionMethod> Alternatives { get; init; }

    /// <summary>
    /// What the mail of the way "confirm via email" carries — a code, a link or both; null — the way is
    /// not available on this surface.
    /// </summary>
    public ClaimEmailMailContent? MailContent { get; init; }

    /// <summary>
    /// How far the mails of the transaction have got; null — nothing is known of them (no mail side, or its
    /// store could not be reached), and the block shows the first step of its way.
    /// </summary>
    public ClaimEmailState? State { get; init; }

    /// <summary>
    /// The <c>mailto:</c> link of the message the user sends for <see cref="EmailCompletionMethod.Push"/>;
    /// null — there is none to offer.
    /// </summary>
    public string? PushMailto { get; init; }

    /// <summary>
    /// Whether no available way can obtain the email any more (E61) — the block then offers only to continue,
    /// and that action ends the email.
    /// </summary>
    public required bool Exhausted { get; init; }

    /// <summary>
    /// Whether the way "confirm via email" shows its address step even though a mail is live — the answer to
    /// <see cref="ClaimCompletionFormFields.EmailOperations.Address"/>, to a code that ran out of attempts and
    /// to a refused send of a typed address. A mark of this answer only, kept nowhere: the page shown afresh
    /// shows the live mail again.
    /// </summary>
    public bool AddressStep { get; init; }

    /// <summary>
    /// The address the answered request typed into the block, shown in the input of the address step in
    /// place of the address of the last mail — the answer to "Continue" with an address not yet confirmed
    /// and to a refused send or save, so what was typed is not lost. A mark of this answer only, kept
    /// nowhere; null — the input shows the address of the last mail.
    /// </summary>
    public string? EnteredAddress { get; init; }

    /// <summary>
    /// The address the block has obtained — held until the form is finished, or already recorded by the web
    /// step; null — the email is still to be obtained. Such a block shows the address and, when it may be
    /// changed, the link that changes it.
    /// </summary>
    public string? RecordedAddress { get; init; }

    /// <summary>
    /// Whether <see cref="RecordedAddress"/> was confirmed; false — saved without verification.
    /// </summary>
    public bool RecordedVerified { get; init; }

    /// <summary>
    /// Whether <see cref="RecordedAddress"/> may be changed — it is held, not yet recorded, and some available
    /// way can still obtain the email, so asking for it again cannot leave the user with nothing.
    /// </summary>
    public bool RecordedChangeable { get; init; }

    /// <summary>
    /// Localized error of the block; null — none.
    /// </summary>
    public string? Error { get; init; }
}
