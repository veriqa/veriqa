// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.MessageTemplates;

/// <summary>
/// Identifiers of the message kinds (message kinds) governed by the typed placeholder schema
/// (SPEC-036 §4.3). Kinds are constants, never magic strings; a caller-slot configuration and the
/// resolver key both address a kind by these identifiers.
/// </summary>
public static class MessageKinds
{
    /// <summary>The sign-in confirmation prompt (messenger channels, SPEC-017 §7.2).</summary>
    public const string ConfirmationPrompt = "confirmation-prompt";

    /// <summary>
    /// The sign-in mail (Pull mode, SPEC-016 §4.3). ONE kind for the whole mail: its subject and its
    /// body are fields of the template variant, and the render mode (<c>html</c>/<c>plain</c>) is
    /// another — neither a part of a message nor a way of rendering it is a kind of its own.
    /// </summary>
    public const string SignInMail = "sign-in-mail";

    /// <summary>
    /// The sign-in mail that carries a CODE instead of the magic link (Pull mode, SPEC-016 §4): the code is
    /// entered on the page the address was typed into. A kind of its own rather than a variant of
    /// <see cref="SignInMail"/>: the minimal step of a ladder may lean only on the slots its kind guarantees,
    /// and this one guarantees the code and not the link. Worded for a confirmation at
    /// <c>ByType:confirmation</c>, as <see cref="SignInMail"/> is.
    /// </summary>
    public const string SignInCodeMail = "sign-in-code-mail";

    /// <summary>
    /// The sign-in mail that carries BOTH the code and the magic link (Pull mode, SPEC-016 §4): whichever the
    /// recipient uses first confirms the sign-in, and the other one no longer does. A kind of its own for the
    /// reason <see cref="SignInCodeMail"/> is one: it guarantees the code and the link alike.
    /// </summary>
    public const string SignInCodeLinkMail = "sign-in-code-link-mail";

    /// <summary>
    /// The mail of the claims completion that carries a CODE confirming the recipient address
    /// (SPEC-041 R12, L51). A kind of its own rather than a branch of the sign-in mail or of the link
    /// mail: the minimal step of a ladder may lean only on the slots its kind guarantees, and this one
    /// guarantees the code.
    /// </summary>
    public const string ClaimCodeMail = "claim-code-mail";

    /// <summary>
    /// The mail of the claims completion that carries a LINK to the page confirming the recipient
    /// address (SPEC-041 L50, L51); it guarantees the link.
    /// </summary>
    public const string ClaimLinkMail = "claim-link-mail";

    /// <summary>
    /// The mail of the claims completion that carries BOTH the code and the link confirming the
    /// recipient address — the mail of a deployment that offers the two ways at once, so the recipient
    /// takes whichever is at hand. A kind of its own for the reason <see cref="ClaimCodeMail"/> is one:
    /// it guarantees the code and the link alike, which neither of the two other kinds does.
    /// </summary>
    public const string ClaimCodeLinkMail = "claim-code-link-mail";

    /// <summary>
    /// The deep-link prefill message that carries the sign-in code or the correlation token
    /// (SPEC-003 §10.4, SPEC-016 §5.3). ONE kind for every channel that prefills a deep link: the
    /// channel is a value of the <c>channel</c> dimension of the template key, never a kind of its own.
    /// </summary>
    public const string DeeplinkPrefill = "deeplink-prefill";

    /// <summary>
    /// Terminal receipt of a CONFIRMED decision (SPEC-036 TPL-123). ONE kind for every wording of it:
    /// what the transaction was about, what is shown where and through which channel are values of the
    /// declared dimensions of the template key — never kinds of their own.
    /// </summary>
    public const string OutcomeReceiptConfirmed = "outcome-receipt-confirmed";

    /// <summary>Terminal receipt of a DECLINED decision (SPEC-036 TPL-123).</summary>
    public const string OutcomeReceiptDeclined = "outcome-receipt-declined";

    /// <summary>
    /// Terminal receipt of a transaction whose time is over — the TTL handler and a decision arriving
    /// for a transaction the store no longer has both end here (SPEC-036 TPL-123, SPEC-003 CA-192).
    /// </summary>
    public const string OutcomeReceiptExpired = "outcome-receipt-expired";

    /// <summary>
    /// Receipt of the one NON-terminal state of the group: the sign-in is confirmed and waits for the
    /// claims it still lacks (SPEC-036 TPL-123). It reports no end of the transaction — the terminal
    /// receipt of such a sign-in is a message of one of the three kinds above.
    /// </summary>
    public const string OutcomeReceiptAwaitingClaims = "outcome-receipt-awaiting-claims";

    /// <summary>
    /// The welcome message a channel receives after a completed sign-in when the post-completion
    /// action of the deployment asks for one (SPEC-012 §4.9, SPEC-036 §4.3). A message of its own, sent
    /// after the outcome receipt and never in place of it (SPEC-036 TPL-104); its slots are filled from
    /// the claims of the resolved identity (SPEC-036 TPL-053).
    /// </summary>
    public const string PostLoginWelcome = "post-login-welcome";

    /// <summary>
    /// The text above the buttons of the menu a channel receives after a completed sign-in when the
    /// post-completion action of the deployment asks for one (SPEC-012 §4.9, SPEC-036 §4.3). A message
    /// of its own, like <see cref="PostLoginWelcome"/>, and never that message reused; its slots are
    /// filled from the claims of the resolved identity (SPEC-036 TPL-053).
    /// </summary>
    public const string PostLoginMenu = "post-login-menu";

    /// <summary>
    /// The text above the link button a channel receives after a completed sign-in when the
    /// post-completion action of the deployment is a redirect (SPEC-012 §4.9, SPEC-036 §4.3). A
    /// message of its own, like <see cref="PostLoginWelcome"/>, and never that message reused; its
    /// slots are filled from the claims of the resolved identity (SPEC-036 TPL-053).
    /// </summary>
    public const string PostLoginRedirect = "post-login-redirect";

    /// <summary>
    /// The request of the phone number the claims completion sends into the chat of the user
    /// when the number may be skipped (SPEC-041 L94). ONE kind for every channel that asks for the
    /// number: the channel is a value of the <c>channel</c> dimension of the template key, never a kind
    /// of its own. The labels of the buttons of the request are not part of the message.
    /// </summary>
    public const string PhoneNumberRequest = "phone-number-request";

    /// <summary>
    /// The request of the phone number the sign-in cannot finish without (SPEC-041 L94). A kind of its
    /// own rather than a wording of <see cref="PhoneNumberRequest"/>: what the user is told to do when
    /// the profile has no number differs between the two — skip the step there, add a number or sign in
    /// through another channel here — and one text cannot say both.
    /// </summary>
    public const string PhoneNumberRequestRequired = "phone-number-request-required";

    /// <summary>
    /// The message a sign-in sends into the chat of the sender when its question is asked on the core
    /// page (SPEC-012 §4.4.2): the sign-in has started and is confirmed on the sign-in page. ONE kind
    /// for every channel that sends messages: the channel is a value of the <c>channel</c> dimension of
    /// the template key, never a kind of its own. It reports no outcome — the receipt of the decision
    /// made on the page is a message of an outcome receipt kind.
    /// </summary>
    public const string WebConfirmationNotice = "web-confirmation-notice";

    /// <summary>
    /// The message that carries the link to the form of the claims completion into the chat of the user
    /// (SPEC-041). ONE kind for every channel that receives the link: the channel is a value of the
    /// <c>channel</c> dimension of the template key, never a kind of its own. The label of the link
    /// button is not part of the message.
    /// </summary>
    public const string ClaimFormLink = "claim-form-link";

    /// <summary>
    /// The line a page of the core shows once the user has moved to a channel of the page: what to do
    /// there to go on — the answer of the bot in the sign-in window and the line of the started
    /// transition on the launch page of a hop. ONE kind for every channel of the page: the channel is a
    /// value of the <c>channel</c> dimension of the template key, never a kind of its own; the shipped
    /// declarations word the kind for a chat and state the wording of Email at the step of that channel.
    /// </summary>
    public const string ChannelAnswerLine = "channel-answer-line";

    /// <summary>
    /// The status line a page of the core shows once the transition to a channel of the page has
    /// started. ONE kind for every channel of the page, for the reason <see cref="ChannelAnswerLine"/>
    /// is one.
    /// </summary>
    public const string ChannelLaunchedLine = "channel-launched-line";

    /// <summary>
    /// The status line a page of the core shows once the QR code of a channel of the page has been
    /// scanned. ONE kind for every channel of the page, for the reason <see cref="ChannelAnswerLine"/>
    /// is one.
    /// </summary>
    public const string QrScannedLine = "qr-scanned-line";

    /// <summary>
    /// The hint a page of the core shows once the phone is on its way into a channel of the page: what the
    /// phone may show on that way before the channel opens. ONE kind for every channel, for the reason
    /// <see cref="ChannelAnswerLine"/> is one: the channel is a value of the <c>channel</c> dimension of the
    /// template key. The kind has no ladder at its base, so a channel no level words a hint for has none —
    /// an ordinary outcome, not a failure; the shipped declarations word it for Messenger alone.
    /// </summary>
    public const string HopChannelHint = "hop-channel-hint";

    /// <summary>
    /// Whether the kind is one of the outcome receipts — the three terminal ones and the receipt of
    /// the claims completion state. The group is the one whose caller slots are filled from values
    /// accepted against ANOTHER contract, and are therefore never guaranteed (SPEC-036 TPL-123).
    /// </summary>
    /// <param name="kind">Message kind identifier.</param>
    /// <returns><c>true</c> — the kind is an outcome receipt.</returns>
    public static bool IsOutcomeReceipt(string kind) =>
        string.Equals(kind, OutcomeReceiptConfirmed, StringComparison.Ordinal)
        || string.Equals(kind, OutcomeReceiptDeclined, StringComparison.Ordinal)
        || string.Equals(kind, OutcomeReceiptExpired, StringComparison.Ordinal)
        || string.Equals(kind, OutcomeReceiptAwaitingClaims, StringComparison.Ordinal);
}
