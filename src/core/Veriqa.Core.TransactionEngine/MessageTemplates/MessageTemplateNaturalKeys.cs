// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.MessageTemplates;

/// <summary>
/// The single source of the core message-template Natural Keys (English base text = localization key,
/// SPEC-036 §3.5, TASK-057 flip). Living in the engine, below the channel layer, this is the ONE place
/// the literals of a message are declared; the channel-layer and page-layer constants that still carry
/// such a text reference these instead of spelling it again (TPL-110/TPL-093). The prompt keys are the
/// steps of the shipped sign-in ladder, the time of the request among its slots (SPEC-036 TPL-138).
/// <para>
/// The mail keys live here rather than with the mail adapter because it is the CONTRACT of the sign-in
/// mail that names them: a localized-string slot of that contract stands for the translation of one of
/// these keys (SPEC-036 §4.1), so the phrases belong to the message, not to the channel that sends it.
/// The same holds for the wordings of the confirmation prompt.
/// </para>
/// <para>
/// The wordings of the terminal outcome RECEIPT are no longer among them: they became a message of
/// the mechanism (SPEC-036 TPL-123), so they live where every wording of a message lives — in the
/// declaration the product ships, addressed by the dimensions that tell the surfaces apart. What is
/// left here of that group is the ERROR of a channel, which is not an outcome of a transaction and
/// stays the channel's own text (SPEC-036 §1.3).
/// </para>
/// All keys are registered in every host locale file (i18n-locale-parity, TPL-040/TPL-044).
/// </summary>
public static class MessageTemplateNaturalKeys
{
    /// <summary>
    /// Full prompt: application + time of the request + browser + OS + region.
    /// <para>
    /// Every step of the ladder, and <see cref="PromptDefault"/> below it, ends with a paragraph of its
    /// own telling the user to decline a request they did not start (RFC 10027 §6.1.14, SPEC-017 §7.2).
    /// It is part of the shipped wording and not appended by the renderer, so a deployment that words
    /// the prompt itself decides whether its wording says it.
    /// </para>
    /// </summary>
    public const string PromptFull = "Confirm sign-in to \"{app}\" — request at {requested_at} from {browser} on {os}, {region}\n\nIf it was not you, decline the request.";

    /// <summary>Prompt without region: application + time of the request + browser + OS.</summary>
    public const string PromptWithoutRegion = "Confirm sign-in to \"{app}\" — request at {requested_at} from {browser} on {os}\n\nIf it was not you, decline the request.";

    /// <summary>
    /// Prompt without initiator details: application + time of the request — the step a missing browser
    /// or OS degrades onto, since the time is the transaction's own and does not go with them.
    /// </summary>
    public const string PromptRequestTimeOnly = "Confirm sign-in to \"{app}\" — request at {requested_at}\n\nIf it was not you, decline the request.";

    /// <summary>Minimal prompt: application only.</summary>
    public const string PromptApplicationOnly = "Confirm sign-in to \"{app}\"\n\nIf it was not you, decline the request.";

    /// <summary>
    /// The prompt a channel shows when the transaction carries no initiator details at all — the floor
    /// below the ladder above, which every step of names at least the application (ICC-042).
    /// </summary>
    public const string PromptDefault = "Confirm sign-in\n\nIf it was not you, decline the request.";

    /// <summary>
    /// Generic anomaly note appended to the prompt when no specific reason is set (ICC-053). Specific
    /// reasons (unusual region/device) arrive as keys of the host's anomaly detector and are checked
    /// against the loaded locale files — keys are not created here without a producer (SPEC-017 §9).
    /// </summary>
    public const string PromptAnomalyGeneric = "⚠ This request looks unusual — confirm only if it was you";

    /// <summary>
    /// Terminal receipt of a failed sign-in on the prompt surface of a messenger. It names the next
    /// step: the failure leaves the user in front of a dead prompt, and the only move that gets him
    /// anywhere is the one the sign-in page offers.
    /// </summary>
    public const string OutcomeErrorInChannel = "Something went wrong ⚠️ Go back to the sign-in page and start again.";

    /// <summary>
    /// Terminal receipt of a failed sign-in for a channel that states no wording of its own — the same
    /// sentence without the emoji, which such a channel is not guaranteed to render.
    /// </summary>
    public const string OutcomeErrorNeutral = "Something went wrong. Go back to the sign-in page and start again.";

    /// <summary>
    /// Reply to a sign-in link the core cannot serve any more: the transaction it names is gone, its
    /// window has closed, or it no longer accepts the event. One sentence for all of those reasons on
    /// purpose — the sender is not told which one applies (SPEC-039 C15) — and it names the next
    /// step, since the link in hand will not work however many times it is followed.
    /// </summary>
    public const string StaleLinkReply =
        "This sign-in link has expired or is no longer valid. Return to the application and start the sign-in again.";

    /// <summary>
    /// Reply to a code the core found no transaction by: it was mistyped, its sign-in has ended, or it
    /// has been used already. One sentence for all of those reasons on purpose — the sender is not told
    /// which one applies, nor whether such a code ever existed — and it names the two ways on: the
    /// code of the sign-in page sent once more, and the page opened again for a new one. It speaks of
    /// no link: a channel started by a code may have none.
    /// </summary>
    public const string UnmatchedCodeReply =
        "This code did not work. Check the code shown on the sign-in page and send it again, "
        + "or open the sign-in page again to get a new one.";

    /// <summary>
    /// Reply of a channel to a pressed button that names no transaction — for a
    /// channel whose link does not always deliver the transaction it carries. It explains the extra
    /// step by the safety of the sign-in and names the two ways on: the code shown on the sign-in page
    /// and one more scan of its QR code. It does not say a sign-in has been started: the same reply
    /// goes to a user who opened the chat with no link at all.
    /// </summary>
    public const string SignInWithoutLinkReply =
        "To keep sign-ins secure, this chat answers only a sign-in started from the sign-in page. "
        + "Send the code shown on that page here, or scan its QR code again.";

    /// <summary>
    /// The same reply for a channel whose deployment ships with the spare start code switched off: it
    /// names only what the sign-in page shows without that block — one more scan of its QR code or one
    /// more press of its button — so the user is not sent looking for a code the page does not show.
    /// </summary>
    public const string SignInWithoutLinkScanAgainReply =
        "To keep sign-ins secure, this chat answers only a sign-in started from the sign-in page. "
        + "Scan the QR code on that page again, or tap its button once more.";

    /// <summary>
    /// Answer to a decision refused because the per-user attempt budget is spent. It deliberately does
    /// NOT advise starting over: a new attempt on a spent budget is refused as well, so the next step
    /// here is waiting out the window (one minute, SPEC-007 §6.1) and only then signing in again.
    /// </summary>
    public const string OutcomeTooManyAttempts = "Too many attempts. Wait a minute and start the sign-in again.";

    /// <summary>Subject of the sign-in mail — the one text of the mail that carries a slot and stays a key.</summary>
    public const string MailTitle = "Sign in to {app}";

    /// <summary>Label of the confirmation button of the sign-in mail.</summary>
    public const string MailButton = "Approve sign-in";

    /// <summary>Instruction above the QR image of the sign-in mail.</summary>
    public const string MailQrInstruction = "Or scan the QR code to finish signing in on another device:";

    /// <summary>Alternative text of the QR image of the sign-in mail.</summary>
    public const string MailQrAlt = "Sign-in QR code";

    /// <summary>Intro line of the sign-in mail.</summary>
    public const string MailTextIntro = "You requested sign-in via Veriqa.";

    /// <summary>Link hint of the plain-text part of the sign-in mail.</summary>
    public const string MailOpenLinkHint = "To confirm, open the following link:";

    /// <summary>Forwarding warning of the sign-in mail (SPEC-016 §9.3).</summary>
    public const string MailForwardWarning = "Do not forward this mail: the link grants sign-in access to your account.";

    /// <summary>Notice for a recipient who did not request the sign-in.</summary>
    public const string MailIgnoreNotice = "If you did not request sign-in, simply ignore this mail.";

    /// <summary>Subject of the confirmation mail — like the sign-in mail, it names the application.</summary>
    public const string ConfirmationMailSubject = "Confirmation for {app}";

    /// <summary>Heading of the confirmation mail.</summary>
    public const string ConfirmationMailTitle = "Confirmation required";

    /// <summary>Label of the button of the confirmation mail.</summary>
    public const string ConfirmationMailButton = "Continue to confirmation";

    /// <summary>Hint above the button of the confirmation mail.</summary>
    public const string ConfirmationMailButtonHint = "Press the button below to continue the confirmation.";

    /// <summary>Instruction above the QR image of the confirmation mail.</summary>
    public const string ConfirmationMailQrInstruction = "Or scan the QR code to continue the confirmation on another device:";

    /// <summary>Alternative text of the QR image of the confirmation mail.</summary>
    public const string ConfirmationMailQrAlt = "Confirmation QR code";

    /// <summary>Intro line of the confirmation mail.</summary>
    public const string ConfirmationMailTextIntro = "A confirmation was requested.";

    /// <summary>Forwarding warning of the confirmation mail (SPEC-016 §9.3).</summary>
    public const string ConfirmationMailForwardWarning =
        "Do not forward this mail: the link lets anyone continue the confirmation on your behalf.";

    /// <summary>Notice for a recipient who did not request the confirmation.</summary>
    public const string ConfirmationMailIgnoreNotice = "If you did not request this confirmation, simply ignore this mail.";

    /// <summary>Label of the direct link in the footer of the sign-in mail.</summary>
    public const string MailDirectLinkLabel = "Direct link:";

    /// <summary>
    /// Served "link sent" text of the Email pull form page. An ordinary localized string, not a message of the
    /// template mechanism: the <c>{email_masked}</c> token is a named position for the masked address in
    /// the sentence (was <c>"... sent to"</c> + concatenation, TPL-052), substituted by the page itself.
    /// </summary>
    public const string MailLinkSent = "The sign-in link has been sent to {email_masked}";

    /// <summary>
    /// WhatsApp deep-link prefill, full variant with the application name (SPEC-003 §10.4). Multi-line
    /// plain text: a title, a one-line instruction, then the sign-in code (<c>auth_{transaction_id}</c>)
    /// on its own last line so no line wrap can split it. Only "cheap" URL-safe punctuation is used —
    /// the whole text is percent-encoded into the <c>wa.me ?text=</c> parameter.
    /// </summary>
    public const string WhatsappPrefillWithApp =
        "Sign in to {app}\nSend this message to sign in.\n\nAccess code\n{code}";

    /// <summary>
    /// WhatsApp deep-link prefill, minimal variant without the application name (degradation floor when
    /// the application name is unavailable, SPEC-003 §10.4). The sign-in code stays on its own last line.
    /// </summary>
    public const string WhatsappPrefillWithoutApp =
        "Send this message to sign in.\n\nAccess code\n{code}";

    /// <summary>
    /// WhatsApp deep-link prefill of a link that is never drawn into a QR code itself — the channel button
    /// and the target a hop QR code leads to — full variant with the application name (SPEC-003 §10.4.4,
    /// layout V6). Dashed rules and a centered heading cost more URL than the graphics-free variant, which
    /// matters only for a link a QR code carries; the sign-in code stays on its own last line.
    /// </summary>
    public const string WhatsappRuledPrefillWithApp =
        "------------------------\n   SIGN IN TO {app}\n------------------------\nJust send this message.\n\nAccess code:\n{code}";

    /// <summary>
    /// WhatsApp deep-link prefill of a link that is never drawn into a QR code itself, minimal variant
    /// without the application name (the degradation floor of <see cref="WhatsappRuledPrefillWithApp"/>).
    /// </summary>
    public const string WhatsappRuledPrefillWithoutApp =
        "------------------------\nJust send this message to sign in.\n------------------------\n\nAccess code:\n{code}";

    /// <summary>
    /// Deep-link prefill carrying no payload in its text at all — the degradation floor of the ladder,
    /// and the live text of a prefill whose payload rides elsewhere (the Email Push mail whose
    /// correlation token is the recipient local part, SPEC-016 §5.3).
    /// </summary>
    public const string PrefillWithoutPayload = "Send this message to sign in.";

    /// <summary>Subject of the payload-free mail prefill — deliberately short, so the QR stays small.</summary>
    public const string PrefillMailSubject = "Sign in";

    /// <summary>
    /// Opening line of the mail prefill whose body carries the correlation token: it asks the user not to
    /// edit what he is about to send, since the token travels in the text.
    /// </summary>
    public const string PrefillMailIntro =
        "This mail confirms sign-in to Veriqa. Do not change the subject or the body of the mail.";
}
