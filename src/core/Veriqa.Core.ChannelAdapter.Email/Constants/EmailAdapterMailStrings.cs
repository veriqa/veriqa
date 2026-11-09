// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Email.Constants;

/// <summary>
/// Localization strings of the Email adapter's served pages and API answers.
/// Natural Keys — the English text is the key and the core default (base language after the
/// TASK-057 flip). The served pages (confirm page, standalone pull form) and the
/// Pull API error texts are resolved into the request/recipient language through
/// <c>IConfirmationPromptLocalizer</c> and are registered in the host locale files; a missing
/// locale/translation degrades to the key itself. Kept English-only by design: the email placeholder
/// <c>PullFormEmailPlaceholder</c>.
/// </summary>
internal static class EmailAdapterMailStrings
{
    /// <summary>
    /// The title of the confirmation page — of its outcomes and its refusals.
    /// </summary>
    public const string ConfirmPageTitle = "Approve sign-in";

    /// <summary>
    /// The title of the page a link of a sign-in letter opens while the sign-in is decided on the sign-in
    /// window: the page asks nothing, it only passes the sign-in on.
    /// </summary>
    public const string SignInRequestTitle = "Sign-in request";

    /// <summary>
    /// The line of that page: which application the sign-in is requested to, and what to do when the
    /// recipient did not request it. <c>{brand_name}</c> — the brand of the application being signed in to,
    /// as the letter names it, or the name of the application when no level states a brand; a replacement
    /// may name it by <c>{app}</c> instead (<see cref="EmailLinkPageNameSlots"/>).
    /// </summary>
    public const string SignInRequestText = "A sign-in to {brand_name} was requested. If it was not you, close this page.";

    /// <summary>
    /// The button of that page; it posts the action token and passes the sign-in on to the sign-in window.
    /// </summary>
    public const string SignInRequestContinue = "Continue signing in";

    /// <summary>
    /// The line of that page once the sign-in was passed on: the decision is the sign-in window's.
    /// </summary>
    public const string SignInRequestContinued = "Return to the sign-in window and confirm the sign-in there.";

    /// <summary>
    /// The button that only closes the tab of a page a link of a letter opens, beside the main action of the
    /// page: it changes nothing.
    /// </summary>
    public const string CancelButton = "Cancel";

    /// <summary>
    /// The one button of the confirmation page opened by the fast link while the form of the claims
    /// completion waits on this device (SPEC-041 N78): pressing it spends the link and opens the form. Shared
    /// Natural Key with the submit button of the form of the completion.
    /// </summary>
    public const string ConfirmPageContinue = "Continue";

    /// <summary>
    /// The title of the pages the letter of a CONFIRMATION transaction is answered with — the request page
    /// its link opens and the refusals of its hand-over: the page names neither the subject nor a sign-in
    /// (SPEC-039 E28). Shared Natural Key with the title of the core confirmation page the request leads to.
    /// </summary>
    public const string ConfirmationContinueTitle = "Confirmation required";

    /// <summary>
    /// The line of the request page a link of a CONFIRMATION transaction opens — the wording of
    /// <see cref="SignInRequestText"/> for a confirmation: what to do when the recipient did not request it;
    /// what is being confirmed is shown on the next page only. Unlike the sign-in line it names no
    /// application: the relying party that created the confirmation server-to-server words this line for its
    /// application through <c>AuthPage.Texts</c> when it wants it named (SPEC-016 EM-206) — by
    /// <c>{brand_name}</c> or <c>{app}</c>, which the page fills as in the sign-in line
    /// (<see cref="EmailLinkPageNameSlots"/>).
    /// </summary>
    public const string ConfirmationRequestText = "A confirmation was requested. If it was not you, close this page.";

    /// <summary>
    /// The button of that page — the wording of <see cref="SignInRequestContinue"/> for a confirmation; it
    /// posts the action token and leads to the core page that asks what is being confirmed.
    /// </summary>
    public const string ConfirmationRequestContinue = "Continue to confirmation";

    /// <summary>
    /// The line of the confirmation page of a sign-in confirmed into the claims completion whose form is not
    /// shown on this page — it is on the device the sign-in was started on, or the sign-in is already being
    /// finished there (SPEC-041 L53). Shared Natural Key with the scanner page of the hop, which points to the
    /// same device.
    /// </summary>
    public const string ConfirmPageContinueOnStartDevice = "Sign-in confirmed. Continue on the device where you started.";

    /// <summary>
    /// The confirmation error message (expired token).
    /// </summary>
    public const string ConfirmPageTokenExpired = "The link has expired. Request a new one.";

    /// <summary>
    /// The confirmation error message (used token).
    /// </summary>
    public const string ConfirmPageTokenUsed = "The link has already been used. Request a new one.";

    /// <summary>
    /// The confirmation error message (token not found).
    /// </summary>
    public const string ConfirmPageTokenNotFound = "The link is invalid or has expired. Request a new one.";

    /// <summary>
    /// The generic error message on the confirmation page.
    /// </summary>
    public const string ConfirmPageError = "Something went wrong. Try requesting a new link.";

    /// <summary>
    /// The served "link sent" text — an ordinary localized string of the page, not a message of the
    /// template mechanism (migrated from a concatenation, TPL-052). The <c>{email_masked}</c> token is
    /// substituted client-side by token replacement into <c>textContent</c> (not innerHTML, not prefix
    /// concatenation) so the localized position of the address is preserved.
    /// </summary>
    public const string PullFormLinkSent = MessageTemplateNaturalKeys.MailLinkSent;

    /// <summary>
    /// The counterpart of <see cref="PullFormLinkSent"/> on any other transaction: the words naming the act
    /// being confirmed, the same <c>{email_masked}</c> position of the address.
    /// </summary>
    public const string PullFormConfirmLinkSent = "The confirmation link has been sent to {email_masked}";

    /// <summary>
    /// The generic error message of the pull form for a link that was not sent.
    /// </summary>
    public const string PullFormSendError = "Failed to send the link. Check the address and try again.";

    /// <summary>
    /// <see cref="PullFormSendError"/> where the mail carries a code: the line names the mail, not the link.
    /// </summary>
    public const string PullFormSendMailError = ApiErrorSendFailed;

    /// <summary>
    /// The message for a disabled Pull mode (standalone pull form).
    /// </summary>
    public const string PullFormModeDisabled = "The Email adapter Pull mode is disabled.";

    /// <summary>
    /// The title of the standalone pull form page of a sign-in.
    /// </summary>
    public const string PullFormTitle = "Sign in via email link";

    /// <summary>
    /// The counterpart of <see cref="PullFormTitle"/> on any other transaction: the words naming the act
    /// being confirmed. Shared Natural Key with the same text of the sign-in window.
    /// </summary>
    public const string PullFormConfirmTitle = "Confirm via email link";

    /// <summary>
    /// <see cref="PullFormTitle"/> where the mail carries a code alone.
    /// </summary>
    public const string PullFormTitleCode = "Sign in with a code from email";

    /// <summary>
    /// The counterpart of <see cref="PullFormTitleCode"/> on any other transaction: the words naming the act
    /// being confirmed. Shared Natural Key with the same text of the sign-in window.
    /// </summary>
    public const string PullFormConfirmTitleCode = "Confirm with a code from email";

    /// <summary>
    /// <see cref="PullFormTitle"/> where the mail carries both the magic link and a code; the heading of the
    /// page is the same text.
    /// </summary>
    public const string PullFormTitleLinkAndCode = "Sign in via email";

    /// <summary>
    /// The counterpart of <see cref="PullFormTitleLinkAndCode"/> on any other transaction: the words naming the act
    /// being confirmed. Shared Natural Key with the same text of the sign-in window.
    /// </summary>
    public const string PullFormConfirmTitleLinkAndCode = "Confirm via email";

    /// <summary>
    /// The heading (h1) of the standalone pull form page of a sign-in.
    /// </summary>
    public const string PullFormHeading = "Sign in with a link";

    /// <summary>
    /// The counterpart of <see cref="PullFormHeading"/> on any other transaction: the words naming the act
    /// being confirmed.
    /// </summary>
    public const string PullFormConfirmHeading = "Confirm with a link";

    /// <summary>
    /// <see cref="PullFormHeading"/> where the mail carries a code alone.
    /// </summary>
    public const string PullFormHeadingCode = "Sign in with a code";

    /// <summary>
    /// The counterpart of <see cref="PullFormHeadingCode"/> on any other transaction: the words naming the act
    /// being confirmed.
    /// </summary>
    public const string PullFormConfirmHeadingCode = "Confirm with a code";

    /// <summary>
    /// The description of the standalone pull form page of a sign-in.
    /// </summary>
    public const string PullFormDescription = "Enter your email — we will send you a sign-in link.";

    /// <summary>
    /// The counterpart of <see cref="PullFormDescription"/> on any other transaction: the words naming the act
    /// being confirmed.
    /// </summary>
    public const string PullFormConfirmDescription = "Enter your email — we will send you a confirmation link.";

    /// <summary>
    /// <see cref="PullFormDescription"/> where the mail carries a code alone.
    /// </summary>
    public const string PullFormDescriptionCode = "Enter your email — we will send you a sign-in code.";

    /// <summary>
    /// The counterpart of <see cref="PullFormDescriptionCode"/> on any other transaction: the words naming the act
    /// being confirmed.
    /// </summary>
    public const string PullFormConfirmDescriptionCode = "Enter your email — we will send you a confirmation code.";

    /// <summary>
    /// <see cref="PullFormDescription"/> where the mail carries both the magic link and a code.
    /// </summary>
    public const string PullFormDescriptionLinkAndCode = "Enter your email — we will send you a sign-in link and a code.";

    /// <summary>
    /// The counterpart of <see cref="PullFormDescriptionLinkAndCode"/> on any other transaction: the words
    /// naming the act being confirmed.
    /// </summary>
    public const string PullFormConfirmDescriptionLinkAndCode = "Enter your email — we will send you a confirmation link and a code.";

    /// <summary>
    /// A warning about a missing session on the pull form page.
    /// </summary>
    public const string PullFormNoSession = "Session not found. Return to the sign-in page.";

    /// <summary>
    /// The email field placeholder (the address format is identical across languages — not localized).
    /// </summary>
    public const string PullFormEmailPlaceholder = "your@email.com";

    /// <summary>
    /// The accessible name (aria-label) of the email field on the pull form page.
    /// </summary>
    public const string PullFormEmailAriaLabel = "Email address";

    /// <summary>
    /// The text of the magic-link submit button on the pull form page.
    /// </summary>
    public const string PullFormSubmitButton = "Get a link";

    /// <summary>
    /// <see cref="PullFormSubmitButton"/> where the mail carries a code alone. Shared Natural Key with the
    /// same button of the sign-in window.
    /// </summary>
    public const string PullFormSubmitCode = "Get a code";

    /// <summary>
    /// <see cref="PullFormSubmitButton"/> where the mail carries both the magic link and a code. Shared
    /// Natural Key with the same button of the sign-in window.
    /// </summary>
    public const string PullFormSubmitLinkAndCode = "Send email";

    /// <summary>
    /// The line over the code field of the pull form page once a mail carrying a code is sent; the slot
    /// <see cref="PullFormCodeAddressSlot"/> takes the masked address. Shared Natural Key with the sign-in
    /// window and the email block of the claims completion.
    /// </summary>
    public const string PullFormCodeSent = "The code has been sent to {address}";

    /// <summary>
    /// Slot of <see cref="PullFormCodeSent"/> the masked address goes into.
    /// </summary>
    public const string PullFormCodeAddressSlot = "{address}";

    /// <summary>
    /// The accessible name of the code field of the pull form page.
    /// </summary>
    public const string PullFormCodeAriaLabel = "Code from the email";

    /// <summary>
    /// The button of the pull form page that checks the entered code.
    /// </summary>
    public const string PullFormCodeConfirm = "Confirm";

    /// <summary>
    /// The line of the pull form page when the code could not be checked and the server stated no reason.
    /// </summary>
    public const string PullFormCodeCheckError = "Failed to check the code. Please try again.";

    /// <summary>
    /// The text the submit button of the pull form page takes once a link has been sent. Shared Natural
    /// Key with the same button of the sign-in window and of the claims completion.
    /// </summary>
    public const string PullFormSendAgainButton = "Send again";

    /// <summary>
    /// API error: unsupported request content type.
    /// </summary>
    public const string ApiErrorUnsupportedContentType = "Unsupported content type.";

    /// <summary>
    /// API error: invalid email address.
    /// </summary>
    public const string ApiErrorInvalidEmail = "Invalid email address.";

    /// <summary>
    /// API error: missing session_id.
    /// </summary>
    public const string ApiErrorMissingSessionId = "Missing session_id.";

    /// <summary>
    /// API error: invalid session_id.
    /// </summary>
    public const string ApiErrorInvalidSessionId = "Invalid session_id.";

    /// <summary>
    /// API error (generic): the Email channel is unavailable.
    /// </summary>
    public const string ApiErrorChannelUnavailable = "The Email channel is unavailable.";

    /// <summary>
    /// API error (generic): the session is invalid or has expired.
    /// </summary>
    public const string ApiErrorSessionInvalid = "The session is invalid or has expired.";

    /// <summary>
    /// API error: the session has already been processed.
    /// </summary>
    public const string ApiErrorSessionAlreadyProcessed = "The session has already been processed.";

    /// <summary>
    /// API error (generic): the Email channel is unavailable for this session.
    /// </summary>
    public const string ApiErrorChannelNotAllowedForSession = "The Email channel is unavailable for this session.";

    /// <summary>
    /// API error: failed to send the email.
    /// </summary>
    public const string ApiErrorSendFailed = "Failed to send the mail. Try again later.";

    /// <summary>
    /// API error: a sign-in mail was asked for sooner than the minimal interval after the previous one.
    /// Shared Natural Key with the same refusal of the claims completion — one wording for one rule.
    /// </summary>
    public const string ApiErrorResendTooSoon = "An email has just been sent. Wait a little before requesting another one.";

    /// <summary>
    /// API error: the sign-in mails to the entered address have reached their limit for the hour. Shared
    /// Natural Key with the same refusal of the claims completion — one wording for one rule.
    /// </summary>
    public const string ApiErrorAddressMailLimit = "Too many emails have been sent to this address. Try again later or enter another address.";

    /// <summary>
    /// API error: the sign-in mails to the domain of the entered address have reached their limit for the
    /// hour. Shared Natural Key with the same refusal of the claims completion — one wording for one rule.
    /// </summary>
    public const string ApiErrorDomainMailLimit = "Emails cannot be sent to this domain right now. Enter another address.";

    /// <summary>
    /// API error of the entry of a sign-in code: the entry does not have the shape of a code; no attempt was
    /// spent. The code of a sign-in mail follows the norms of the code of the claims completion, so its errors
    /// share the Natural Keys of that code — one wording for one rule.
    /// </summary>
    public const string ApiErrorCodeShape = "Enter all digits of the code from the email.";

    /// <summary>
    /// API error of the entry of a sign-in code: the code does not match; <see cref="ApiErrorAttemptsSlot"/>
    /// takes the attempts left.
    /// </summary>
    public const string ApiErrorWrongCode = "Wrong code. Attempts left: {attempts}";

    /// <summary>
    /// Slot of <see cref="ApiErrorWrongCode"/> the attempts left go into.
    /// </summary>
    public const string ApiErrorAttemptsSlot = "{attempts}";

    /// <summary>
    /// API error of the entry of a sign-in code: the live code has no attempts left, and a new mail brings a
    /// new one.
    /// </summary>
    public const string ApiErrorCodeExhausted = "The code has no attempts left. Request a new email.";

    /// <summary>
    /// API error of the entry of a sign-in code: the live code has expired.
    /// </summary>
    public const string ApiErrorCodeExpired = "The code has expired. Request a new email.";

    /// <summary>
    /// API error of the entry of a sign-in code: no code is live — none was sent, or the letter it was sent in
    /// was answered by its link or has expired.
    /// </summary>
    public const string ApiErrorNoCode = "Request a code first.";

    /// <summary>
    /// Subject of the mail the user sends for the <c>Push</c> way of obtaining the email during the claims
    /// completion (SPEC-041 L50). The same Natural Key heads the two mails of the completion.
    /// </summary>
    public const string ClaimPushSubject = "Confirm your email address";

    /// <summary>
    /// Body of the mail the user sends for the <c>Push</c> way of obtaining the email.
    /// </summary>
    public const string ClaimPushBody = "Send this message as it is to confirm your email address.";

    /// <summary>
    /// Title of the page a link of the claims-completion mail opens — the wording that heads the mails of the
    /// completion (<see cref="ClaimPushSubject"/>).
    /// </summary>
    public const string ClaimLinkPageTitle = ClaimPushSubject;

    /// <summary>
    /// What the link page asks the recipient to confirm: <c>{address}</c> — the masked address the link was
    /// sent to, <c>{brand_name}</c> — the brand of the application being signed in to, as in
    /// <see cref="SignInRequestText"/>.
    /// </summary>
    public const string ClaimLinkPageQuestion = "Confirm the address {address} to sign in to {brand_name}.";

    /// <summary>
    /// Warning of the link page — the same as in the link mail: a recipient who did not sign in does not
    /// confirm. <c>{brand_name}</c> — the brand of the application being signed in to, as in
    /// <see cref="SignInRequestText"/>.
    /// </summary>
    public const string ClaimLinkPageWarning = "If you did not sign in to {brand_name}, do not confirm the address.";

    /// <summary>
    /// The button of the link page — the wording of the button of the link mail.
    /// </summary>
    public const string ClaimLinkPageConfirm = "Confirm the address";

    /// <summary>
    /// The link page after the address was confirmed.
    /// </summary>
    public const string ClaimLinkPageConfirmed = "The address is confirmed. Return to the sign-in page.";

    /// <summary>
    /// The link page for a link that no longer works: replaced by a newer one, used, expired, or the sign-in
    /// no longer waits for the address.
    /// </summary>
    public const string ClaimLinkPageGone = "This link no longer works. Request a new one on the sign-in page.";

    /// <summary>
    /// The texts of the adapter's pages an entry of <c>AuthPage.Texts</c> may override for one application:
    /// the confirmation page of the link, its request of a sign-in and of a confirmation, the pull form and the
    /// link page of the claims completion, with the texts of the button that closes the tab of those pages.
    /// Left out on purpose:
    /// <list type="bullet">
    /// <item><see cref="PullFormEmailPlaceholder"/> — an address format rather than a wording, rendered as it
    /// is and never through the locale files;</item>
    /// <item><c>ApiError*</c> — answers of the JSON API
    /// (the one shown on a page, <see cref="ApiErrorChannelUnavailable"/>, keeps its regular text there
    /// too); <see cref="ClaimPushSubject"/> and <see cref="ClaimPushBody"/> — a mail, whose wording is its
    /// message template.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> OverridablePageTexts { get; } =
    [
        ConfirmPageTitle,
        SignInRequestTitle,
        SignInRequestText,
        SignInRequestContinue,
        SignInRequestContinued,
        CancelButton,
        ConfirmPageContinue,
        ConfirmPageContinueOnStartDevice,
        ConfirmPageTokenExpired,
        ConfirmPageTokenUsed,
        ConfirmPageTokenNotFound,
        ConfirmPageError,
        ConfirmationContinueTitle,
        ConfirmationRequestText,
        ConfirmationRequestContinue,
        PullFormLinkSent,
        PullFormConfirmLinkSent,
        PullFormSendError,
        PullFormSendMailError,
        PullFormModeDisabled,
        PullFormTitle,
        PullFormTitleCode,
        PullFormTitleLinkAndCode,
        PullFormHeading,
        PullFormHeadingCode,
        PullFormDescription,
        PullFormDescriptionCode,
        PullFormDescriptionLinkAndCode,
        PullFormConfirmTitle,
        PullFormConfirmTitleCode,
        PullFormConfirmTitleLinkAndCode,
        PullFormConfirmHeading,
        PullFormConfirmHeadingCode,
        PullFormConfirmDescription,
        PullFormConfirmDescriptionCode,
        PullFormConfirmDescriptionLinkAndCode,
        PullFormNoSession,
        PullFormEmailAriaLabel,
        PullFormSubmitButton,
        PullFormSubmitCode,
        PullFormSubmitLinkAndCode,
        PullFormSendAgainButton,
        PullFormCodeSent,
        PullFormCodeAriaLabel,
        PullFormCodeConfirm,
        PullFormCodeCheckError,
        ClaimLinkPageTitle,
        ClaimLinkPageQuestion,
        ClaimLinkPageWarning,
        ClaimLinkPageConfirm,
        ClaimLinkPageConfirmed,
        ClaimLinkPageGone,
        .. CorePageCloseAction.OverridableTexts,
    ];
}
