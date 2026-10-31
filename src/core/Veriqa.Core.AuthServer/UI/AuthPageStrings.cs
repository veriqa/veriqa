// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// Natural Keys of the authentication page (SPEC-007 §12, UI-079). The key IS the English text —
/// the base language (TASK-057), i.e. source text, not a translation. No translations live here:
/// every non-base language is resolved data-driven from the host locale files
/// (<c>wwwroot/locales/&lt;code&gt;.json</c>) through
/// <see cref="ChannelAdapter.Pipeline.IConfirmationPromptLocalizer"/> — see
/// <see cref="AuthPageLocalization"/> for the resolution rule and
/// <see cref="AuthPageLanguageRegistry"/> for the language registry.
/// </summary>
internal static class AuthPageStrings
{
    /// <summary>
    /// Page title of a sign-in. Besides, the key the content language of the window is probed on
    /// (<see cref="AuthPageLocalization.ResolveContentLanguage"/>), whatever the transaction of the page.
    /// </summary>
    public const string Title = "Sign in via trusted channel";

    /// <summary>
    /// The wording of <see cref="Title"/> for a transaction that is not a sign-in.
    /// </summary>
    public const string ConfirmationTitle = "Confirm via trusted channel";

    /// <summary>
    /// Instruction of a sign-in page with the unified QR code for several channels, while it shows both
    /// the QR code and the channel buttons. The renderer's own instruction exists in three wordings — the
    /// QR code and the button, the QR code alone, the button alone (SPEC-007 UI-020) — all three are
    /// rendered, and the page shows the one that matches what it actually displays, because only the
    /// browser knows the device. Every wording exists for a sign-in and for a confirmation — the
    /// transaction of any other type.
    /// </summary>
    public const string Instruction = "Scan the QR code or tap a channel button to sign in";

    /// <summary>
    /// The wording of <see cref="Instruction"/> for a page that shows the QR code and hides the channel
    /// buttons.
    /// </summary>
    public const string InstructionWithoutButton = "Scan the QR code to sign in";

    /// <summary>
    /// The wording of <see cref="Instruction"/> for a page that hides the QR code.
    /// </summary>
    public const string InstructionWithoutQr = "Tap a channel button to sign in";

    /// <summary>
    /// The confirmation counterpart of <see cref="Instruction"/>.
    /// </summary>
    public const string ConfirmInstruction = "Scan the QR code or tap a channel button to confirm";

    /// <summary>
    /// The confirmation counterpart of <see cref="InstructionWithoutButton"/>.
    /// </summary>
    public const string ConfirmInstructionWithoutButton = "Scan the QR code to confirm";

    /// <summary>
    /// The confirmation counterpart of <see cref="InstructionWithoutQr"/>.
    /// </summary>
    public const string ConfirmInstructionWithoutQr = "Tap a channel button to confirm";

    /// <summary>
    /// The wording of a sign-in page with the "Any channel" tab while that tab is selected and shows the
    /// unified QR code beside the buttons of every channel (SPEC-025 HOP-187): the page then offers a QR
    /// code and the buttons of several messengers, and the person picks one. A channel tab names its
    /// channel (<see cref="SingleChannelInstruction"/>).
    /// </summary>
    public const string AnyChannelInstruction = "Scan the QR code or tap your messaging app's button to sign in";

    /// <summary>
    /// The confirmation counterpart of <see cref="AnyChannelInstruction"/>.
    /// </summary>
    public const string ConfirmAnyChannelInstruction = "Scan the QR code or tap your messaging app's button to confirm";

    /// <summary>
    /// Instruction of a sign-in page with several channels, each under a QR code of its own, that are all
    /// on screen at once (the grid, the buttons layout), while it shows both the QR codes and the channel
    /// buttons.
    /// </summary>
    public const string MultiChannelInstruction = "Choose a channel, then scan its QR code or tap its button to sign in";

    /// <summary>
    /// The wording of <see cref="MultiChannelInstruction"/> for a page that shows the QR codes and hides
    /// the channel buttons.
    /// </summary>
    public const string MultiChannelInstructionWithoutButton = "Choose a channel, then scan its QR code to sign in";

    /// <summary>
    /// The wording of <see cref="MultiChannelInstruction"/> for a page that hides the QR codes.
    /// </summary>
    public const string MultiChannelInstructionWithoutQr = "Choose a channel and tap its button to sign in";

    /// <summary>
    /// The confirmation counterpart of <see cref="MultiChannelInstruction"/>.
    /// </summary>
    public const string ConfirmMultiChannelInstruction = "Choose a channel, then scan its QR code or tap its button to confirm";

    /// <summary>
    /// The confirmation counterpart of <see cref="MultiChannelInstructionWithoutButton"/>.
    /// </summary>
    public const string ConfirmMultiChannelInstructionWithoutButton = "Choose a channel, then scan its QR code to confirm";

    /// <summary>
    /// The confirmation counterpart of <see cref="MultiChannelInstructionWithoutQr"/>.
    /// </summary>
    public const string ConfirmMultiChannelInstructionWithoutQr = "Choose a channel and tap its button to confirm";

    /// <summary>
    /// Instruction of a sign-in page with one channel, and of a channel tab, while it shows both the QR
    /// code and the channel button. The <c>{0}</c> slot takes the channel — its brand icon and its name.
    /// </summary>
    public const string SingleChannelInstruction = "Scan the QR code or tap the button to sign in with {0}";

    /// <summary>
    /// The wording of <see cref="SingleChannelInstruction"/> for a page that shows the QR code and hides
    /// the channel button.
    /// </summary>
    public const string SingleChannelInstructionWithoutButton = "Scan the QR code to sign in with {0}";

    /// <summary>
    /// The wording of <see cref="SingleChannelInstruction"/> for a page that hides the QR code, and for a
    /// panel that shows a button and no QR code of its own — the address form of Email Pull among them.
    /// </summary>
    public const string SingleChannelInstructionWithoutQr = "Tap the button to sign in with {0}";

    /// <summary>
    /// The confirmation counterpart of <see cref="SingleChannelInstruction"/>.
    /// </summary>
    public const string ConfirmSingleChannelInstruction = "Scan the QR code or tap the button to confirm in {0}";

    /// <summary>
    /// The confirmation counterpart of <see cref="SingleChannelInstructionWithoutButton"/>.
    /// </summary>
    public const string ConfirmSingleChannelInstructionWithoutButton = "Scan the QR code to confirm in {0}";

    /// <summary>
    /// The confirmation counterpart of <see cref="SingleChannelInstructionWithoutQr"/>.
    /// </summary>
    public const string ConfirmSingleChannelInstructionWithoutQr = "Tap the button to confirm in {0}";

    /// <summary>
    /// Instruction of the Email panel in Pull mode with no QR code beside it, whatever the wording: the
    /// panel holds the address form alone, neither a QR code nor a channel button. One text for a sign-in
    /// and a confirmation — it names no act.
    /// </summary>
    public const string EmailFormInstruction = "Enter your email to get a link";

    /// <summary>
    /// <see cref="EmailFormInstruction"/> where the mail of Pull carries a code alone.
    /// </summary>
    public const string EmailFormInstructionCode = "Enter your email to get a code";

    /// <summary>
    /// <see cref="EmailFormInstruction"/> where the mail of Pull carries both the magic link and a code.
    /// </summary>
    public const string EmailFormInstructionLinkAndCode = "Enter your email to get a link and a code";

    /// <summary>
    /// QR code alt text.
    /// </summary>
    public const string QrCodeAlt = "Sign-in QR code";

    /// <summary>
    /// Label of the first tab of a page that shows the unified QR code next to the QR code of each
    /// channel: the tab with the unified QR code and the buttons of every channel, standing before the
    /// tabs named by their channels.
    /// </summary>
    public const string AnyChannelTab = "Any channel";

    /// <summary>
    /// Label of a channel tab on a page with the "Any channel" tab, when the panel of that channel shows a
    /// QR code of its own: <c>{0}</c> — the name of the channel, and the mark <c>▦</c> (U+25A6), which the
    /// renderer replaces, wherever it stands, with the glyph of a QR code. The mark is a character and not
    /// a slot, so an override may move it, repeat it or leave it out; the slot of the name stays.
    /// </summary>
    public const string ChannelTabWithQrCode = "{0} ▦";

    /// <summary>
    /// The button that leads into a messenger on a sign-in page. The <c>{0}</c> slot takes the name of the
    /// channel in the language of the page — of a built-in channel (<see cref="ChannelNames.TelegramChannelName"/>
    /// and the rest) or the label its adapter declared. A phrase of one channel (<c>Sign in with WhatsApp</c>) is a
    /// key of its own and outranks this template (<see cref="ChannelButton.Text"/>).
    /// </summary>
    public const string SignInWithChannel = "Sign in with {0}";

    /// <summary>
    /// The confirmation counterpart of <see cref="SignInWithChannel"/>: the person confirms in the
    /// messenger, not by the tap, so the button names the place and not the act.
    /// </summary>
    public const string ConfirmInChannel = "Confirm in {0}";

    /// <summary>
    /// The button that reveals the email form of the Email Pull mode, on a page with a choice of channels,
    /// on a sign-in. It names the act as the button of a messenger does (<see cref="SignInWithChannel"/>).
    /// </summary>
    public const string SignInWithEmail = "Sign in with Email";

    /// <summary>
    /// The counterpart of <see cref="SignInWithEmail"/> on any other transaction: the same button, the
    /// words naming the act being confirmed, as <see cref="ConfirmInChannel"/> does for a messenger.
    /// </summary>
    public const string ConfirmWithEmail = "Confirm with Email";

    /// <summary>
    /// The button of the Email Push mode. It names what the user does with one tap — sends the prepared
    /// confirmation email — rather than the program it opens, so the same words fit a sign-in and a
    /// confirmation alike.
    /// </summary>
    public const string SendConfirmationEmail = "Send confirmation email";

    /// <summary>
    /// Toggle link to manual email entry (from the Push panel), on a sign-in.
    /// </summary>
    public const string EmailLoginByLink = "Sign in via email link";

    /// <summary>
    /// The counterpart of <see cref="EmailLoginByLink"/> on any other transaction: the same link, the words
    /// naming the act being confirmed.
    /// </summary>
    public const string EmailConfirmByLink = "Confirm via email link";

    /// <summary>
    /// <see cref="EmailLoginByLink"/> where the mail of Pull carries a code alone.
    /// </summary>
    public const string EmailLoginByCode = "Sign in with a code from email";

    /// <summary>
    /// The counterpart of <see cref="EmailLoginByCode"/> on any other transaction.
    /// </summary>
    public const string EmailConfirmByCode = "Confirm with a code from email";

    /// <summary>
    /// <see cref="EmailLoginByLink"/> where the mail of Pull carries both the magic link and a code.
    /// </summary>
    public const string EmailLoginByMail = "Sign in via email";

    /// <summary>
    /// The counterpart of <see cref="EmailLoginByMail"/> on any other transaction.
    /// </summary>
    public const string EmailConfirmByMail = "Confirm via email";

    /// <summary>
    /// "OR" divider between the email client button and the manual email entry link.
    /// </summary>
    public const string OrDivider = "OR";

    /// <summary>
    /// Toggle link back to the QR code (from the Pull panel).
    /// </summary>
    public const string EmailBackToQr = "← Back to QR code";

    /// <summary>
    /// The same toggle link on a page that hides the QR code — the counterpart of
    /// <see cref="EmailBackToQr"/> (SPEC-007 UI-020). It names the direction only: the section it
    /// returns to is there either way, the QR in it is not.
    /// </summary>
    public const string EmailBackWithoutQr = "← Back";

    /// <summary>
    /// Email input field label (used for aria-label and the label element).
    /// </summary>
    public const string EmailInputLabel = "Email address";

    /// <summary>
    /// Email input field placeholder (not localized — the format is the same for all languages).
    /// </summary>
    public const string EmailInputPlaceholder = "your@email.com";

    /// <summary>
    /// "Get a link" button for the email form.
    /// </summary>
    public const string EmailSendLink = "Get a link";

    /// <summary>
    /// <see cref="EmailSendLink"/> where the mail of Pull carries a code alone.
    /// </summary>
    public const string EmailSendCode = "Get a code";

    /// <summary>
    /// <see cref="EmailSendLink"/> where the mail of Pull carries both the magic link and a code: the button
    /// names the mail rather than one of the two things in it.
    /// </summary>
    public const string EmailSendMail = "Send email";

    /// <summary>
    /// Message shown after the email was sent successfully.
    /// </summary>
    public const string EmailSentMessage = "Link sent to {0}. Check your inbox.";

    /// <summary>
    /// Email send error message.
    /// </summary>
    public const string EmailSendError = "Failed to send the link. Please try again.";

    /// <summary>
    /// <see cref="EmailSendError"/> where the mail of Pull carries a code: the line names the mail, not the
    /// link. Shared Natural Key with the answer of the start that failed to send the mail.
    /// </summary>
    public const string EmailSendMailError = "Failed to send the mail. Try again later.";

    /// <summary>
    /// The line over the code field once a mail carrying a code is sent; the slot takes the masked address.
    /// Shared Natural Key with the email block of the claims completion.
    /// </summary>
    public const string EmailCodeSent = "The code has been sent to {address}";

    /// <summary>
    /// Accessible name of the code field. Shared Natural Key with the email block of the claims completion.
    /// </summary>
    public const string EmailCodeLabel = "Code from the email";

    /// <summary>
    /// The button that checks the entered code. Shared Natural Key with the email block of the claims
    /// completion.
    /// </summary>
    public const string EmailCodeConfirm = "Confirm";

    /// <summary>
    /// Line shown when the code could not be checked at all — no answer of the server to show instead.
    /// </summary>
    public const string EmailCodeCheckError = "Failed to check the code. Please try again.";

    /// <summary>
    /// Text the button of the email form takes once a link has been sent: the next press sends another one.
    /// </summary>
    public const string EmailSendAgain = "Send again";

    /// <summary>
    /// Email Pull status: the user opened the confirmation page from the email (SPEC-016 §10.1), on a
    /// sign-in.
    /// </summary>
    public const string EmailLinkOpened = "Email link opened. Confirm the sign-in.";

    /// <summary>
    /// The counterpart of <see cref="EmailLinkOpened"/> on any other transaction: the same status, the words
    /// naming the act being confirmed.
    /// </summary>
    public const string EmailLinkOpenedConfirmation = "Email link opened. Continue the confirmation.";

    /// <summary>
    /// Email Push: the line that replaces the line of the start of the transition into the Email channel
    /// once the user, having tapped the mail-client button on this very page, comes back to it — the mail
    /// may already be on its way, and what is left is to wait for the confirmation.
    /// </summary>
    public const string EmailReturnWait =
        "If you have already sent the email, wait for the confirmation. It usually takes a few seconds.";

    /// <summary>
    /// Email Push status: the incoming email was received (SPEC-016 §10.1).
    /// </summary>
    public const string EmailMailReceived = "Email received. Verifying the sender…";

    /// <summary>
    /// Email Push status: the sender was verified and is the subject the transaction admits (SPEC-016 §10.1),
    /// on a sign-in.
    /// </summary>
    public const string EmailSenderVerified = "Sender verified. Completing sign-in…";

    /// <summary>
    /// The counterpart of <see cref="EmailSenderVerified"/> on any other transaction. The letter of a
    /// confirmation only says who confirms: what is confirmed is still asked on the page of the core, so the
    /// line says the confirmation goes on rather than that it is being completed (SPEC-039 E29).
    /// </summary>
    public const string EmailSenderVerifiedConfirmation = "Sender verified. Continuing the confirmation…";

    /// <summary>
    /// Email Push status of a sign-in: the sender was verified, but the sign-in is already bound to the
    /// subject of another channel, so this mail does not complete it (SPEC-039 N34).
    /// </summary>
    public const string EmailSubjectMismatchSignIn = "Sign-in has already started in another channel.";

    /// <summary>
    /// The wording of <see cref="EmailSubjectMismatchSignIn"/> for a transaction that is not a sign-in.
    /// </summary>
    public const string EmailSubjectMismatch = "Confirmation has already started in another channel.";

    /// <summary>
    /// Hop status: the QR code was scanned (SPEC-025 HOP-101), for an event that names no channel of the
    /// page — the unified QR code, or a channel the page does not show.
    /// </summary>
    public const string HopScanned = "QR code scanned. Continue on your phone.";

    /// <summary>
    /// Hop status: the intermediate page of the hop was opened on the phone (SPEC-025 HOP-101).
    /// </summary>
    public const string HopInterstitialOpened = "The page is open on your phone.";

    /// <summary>
    /// Default content of the block the page shows in place of the QR code and the channel choice
    /// once the hop has started, when no level states content of its own.
    /// </summary>
    public const string HopInstruction = "Continue on your phone and confirm in the app that has opened.";

    /// <summary>
    /// Button of the block shown once the hop has started: it brings the channel choice back on the page.
    /// </summary>
    public const string HopChooseAnother = "Choose another way";

    /// <summary>
    /// Button of the scanner page shown once a channel was chosen there: it brings the channel choice back on
    /// the page (SPEC-025 HOP-193).
    /// </summary>
    public const string BackToChannelSelection = "Back to channel selection";

    /// <summary>
    /// Link that opens the spare start code of a channel after the person came back from it with nothing
    /// started — on the phone under the status line of the sign-in window, and on the launch page of the
    /// hop — on a sign-in.
    /// </summary>
    public const string ProblemSigningIn = "Problem signing in";

    /// <summary>
    /// The counterpart of <see cref="ProblemSigningIn"/> on any other transaction: the same link opening
    /// the same block, worded by the act being confirmed.
    /// </summary>
    public const string ProblemConfirming = "Problem confirming";

    /// <summary>
    /// Warning of a hop link opened by a person once more, from another browser (SPEC-041 L93): shown on
    /// the sign-in page when the channel status <c>reopened</c> arrives and on the form of the claims
    /// completion of a transaction carrying the mark. A risk signal only — it changes nothing on the page.
    /// </summary>
    public const string HopReopened =
        "This QR code was also scanned on another device. If that wasn't you, start over.";

    /// <summary>
    /// Claims completion (SPEC-041 L77): the form of the web step is open on the device that confirmed the
    /// sign-in. The hint of the channel status <c>claim_form</c> on the sign-in page and the waiting line of
    /// the page of the completion while the web step is on that device.
    /// </summary>
    public const string ClaimFormOnPhone = "Complete the form on your phone";

    /// <summary>
    /// Final line of the scanner page of a sign-in once the sign-in is complete (SPEC-025 HOP-186): the
    /// sign-in itself finishes in the browser that started it, so this page points there instead of
    /// continuing anywhere.
    /// </summary>
    public const string ScannerSignInConfirmed = "Sign-in confirmed. Continue on the device where you started.";

    /// <summary>
    /// Line of a phone page of a sign-in once the core has taken the question onto the page of the sign-in
    /// window (SPEC-025 HOP-186): the scanner page and the launch page of the hop have nowhere to go then, so
    /// they point back to the window where the question waits. The same text as the line of the page a link
    /// of an Email letter opens once the sign-in was passed on, so the two read alike and share one
    /// translation.
    /// </summary>
    public const string ReturnToSignInWindow = "Return to the sign-in window and confirm the sign-in there.";

    /// <summary>
    /// Line of the scanner page of a sign-in whose transaction ran out of time or is no longer stored
    /// (SPEC-025 HOP-146). The page cannot tell which of the two it is, so the line holds for both and
    /// points to the device where the sign-in started. A sign-in that succeeded is not one of them: its
    /// transaction is kept, and the page shows <see cref="ScannerSignInConfirmed"/>.
    /// </summary>
    public const string ScannerSignInInactive =
        "This sign-in is no longer active here. Check the device where you started.";

    /// <summary>
    /// Status: waiting for scan.
    /// </summary>
    public const string WaitingForScan = "Waiting for confirmation…";

    /// <summary>
    /// Status: confirmed, processing.
    /// </summary>
    public const string ConfirmedProcessing = "Confirmed. Processing…";

    /// <summary>
    /// Status: successful sign-in.
    /// </summary>
    public const string LoginSuccess = "Sign-in successful!";

    /// <summary>
    /// Status: the time given for the sign-in ran out.
    /// </summary>
    public const string SessionExpired = "This sign-in request has expired";

    /// <summary>
    /// The one action an expired sign-in window offers: go back and start over. It is worded as the
    /// user's intent and not as a mechanism ("retry", "reload"), because what the link does is begin a
    /// NEW sign-in — the expired one cannot be resumed.
    /// </summary>
    public const string RestartSignIn = "Start the sign-in again";

    /// <summary>
    /// The button of the sign-in window that ends the sign-in and returns the user to the application
    /// that started it. The same Natural Key as the cancel action of the claims completion form, so the
    /// two buttons that do one thing read alike and share one translation.
    /// </summary>
    public const string CancelSignIn = "Cancel sign-in";

    /// <summary>
    /// The button of the entry page of a confirmation that ends the confirmation as declined — the
    /// counterpart of <see cref="CancelSignIn"/>: the same action, worded for a confirmation.
    /// </summary>
    public const string CancelConfirmation = "Cancel confirmation";

    /// <summary>
    /// Status: the user declined the sign-in. A refusal is not a fault, so the window tells it apart from
    /// <see cref="LoginError"/>, as the page of a confirmation tells its own refusal apart.
    /// </summary>
    public const string SignInDeclined = "Sign-in declined";

    /// <summary>
    /// Status: error.
    /// </summary>
    public const string LoginError = "Authentication error";

    /// <summary>
    /// The texts of the sign-in window an owning level may override (SPEC-012 §4.3, key
    /// <c>AuthPage.Texts</c>): every Natural Key of this class — on both surfaces that render the window,
    /// the sign-in and the entry page of a confirmation, and in the statuses its script shows. Not
    /// <see cref="LanguageEn"/>, a language code, and not <see cref="EmailInputPlaceholder"/>, an address
    /// format the page shows untranslated: neither is a Natural Key the page localizes. A Natural Key added
    /// to this class is added here as well, or no override can reach it. The list carries, besides, the
    /// name and the phrases of the button of every built-in channel (<see cref="ChannelPhrases"/>): keys an
    /// override may state for one channel. The name and the phrases of every other registered channel join
    /// the set from the adapters themselves, at the registration of the texts of the server.
    /// </summary>
    public static IReadOnlyList<string> OverridableTexts { get; } =
    [
        Title,
        ConfirmationTitle,
        Instruction,
        InstructionWithoutButton,
        InstructionWithoutQr,
        ConfirmInstruction,
        ConfirmInstructionWithoutButton,
        ConfirmInstructionWithoutQr,
        AnyChannelInstruction,
        ConfirmAnyChannelInstruction,
        MultiChannelInstruction,
        MultiChannelInstructionWithoutButton,
        MultiChannelInstructionWithoutQr,
        ConfirmMultiChannelInstruction,
        ConfirmMultiChannelInstructionWithoutButton,
        ConfirmMultiChannelInstructionWithoutQr,
        SingleChannelInstruction,
        SingleChannelInstructionWithoutButton,
        SingleChannelInstructionWithoutQr,
        ConfirmSingleChannelInstruction,
        ConfirmSingleChannelInstructionWithoutButton,
        ConfirmSingleChannelInstructionWithoutQr,
        EmailFormInstruction,
        EmailFormInstructionCode,
        EmailFormInstructionLinkAndCode,
        QrCodeAlt,
        AnyChannelTab,
        ChannelTabWithQrCode,
        SignInWithChannel,
        ConfirmInChannel,
        .. ChannelNames.BuiltInChannelNames.Select(channel => channel.NameKey),
        .. ChannelNames.BuiltInChannelNames.SelectMany(
            channel => ChannelPhrases(NaturalKeyContext.BaseTextOf(channel.NameKey))),
        SignInWithEmail,
        ConfirmWithEmail,
        SendConfirmationEmail,
        EmailLoginByLink,
        EmailConfirmByLink,
        EmailLoginByCode,
        EmailConfirmByCode,
        EmailLoginByMail,
        EmailConfirmByMail,
        OrDivider,
        EmailBackToQr,
        EmailBackWithoutQr,
        EmailInputLabel,
        EmailSendLink,
        EmailSendCode,
        EmailSendMail,
        EmailSentMessage,
        EmailSendError,
        EmailSendMailError,
        EmailSendAgain,
        EmailCodeSent,
        EmailCodeLabel,
        EmailCodeConfirm,
        EmailCodeCheckError,
        EmailLinkOpened,
        EmailLinkOpenedConfirmation,
        EmailReturnWait,
        EmailMailReceived,
        EmailSenderVerified,
        EmailSenderVerifiedConfirmation,
        EmailSubjectMismatchSignIn,
        EmailSubjectMismatch,
        HopScanned,
        HopInterstitialOpened,
        HopInstruction,
        HopChooseAnother,
        BackToChannelSelection,
        ProblemSigningIn,
        ProblemConfirming,
        HopReopened,
        ClaimFormOnPhone,
        ScannerSignInConfirmed,
        ReturnToSignInWindow,
        ScannerSignInInactive,
        WaitingForScan,
        ConfirmedProcessing,
        LoginSuccess,
        SessionExpired,
        RestartSignIn,
        CancelSignIn,
        CancelConfirmation,
        SignInDeclined,
        LoginError
    ];

    /// <summary>
    /// Base language code. The only language code that belongs in code: it is the language OF the
    /// Natural Keys (the key is the text), not a supported-language entry. Every other language
    /// comes from the locale files — see <see cref="AuthPageLanguageRegistry"/>.
    /// </summary>
    public const string LanguageEn = "en";

    /// <summary>
    /// Detects the language from the Accept-Language header. If the language is not recognized
    /// or the header is missing — the default language from configuration is returned (SPEC-007 §12).
    /// Detection follows the order of header segments (the client orders them by preference);
    /// q-weights are NOT taken into account — the first segment whose primary subtag
    /// (the part before "-", e.g. "en" from "en-US") is in the supplied registry is used.
    /// Comparison via Contains is robust to the set's enumeration order (unlike StartsWith) and to
    /// language codes that are prefixes of each other. The registry is data-driven and built by
    /// <see cref="AuthPageLanguageRegistry"/>: dropping a locale file into the host's localization
    /// directory automatically includes the language in detection.
    /// </summary>
    /// <param name="acceptLanguage">Accept-Language header value.</param>
    /// <param name="defaultLanguage">Default language from configuration (Veriqa:Localization:DefaultLanguage).</param>
    /// <param name="supportedLanguages">Data-driven registry of supported languages.</param>
    /// <returns>Supported language code.</returns>
    public static string DetectLanguage(
        string? acceptLanguage,
        string defaultLanguage,
        IReadOnlySet<string> supportedLanguages)
    {
        // An invalid default language is normalized to English (the base language, TASK-057).
        var fallback = supportedLanguages.Contains(defaultLanguage) ? defaultLanguage : LanguageEn;

        if (string.IsNullOrEmpty(acceptLanguage))
        {
            return fallback;
        }

        // Limit the length to protect against malformed headers
        var headerValue = acceptLanguage.Length > 256
            ? acceptLanguage[..256]
            : acceptLanguage;

        // Iterate the segments in preference order (e.g. "en-US,ru;q=0.9" → ["en-US", "ru"]),
        // take the segment's primary subtag ("en-US" → "en") and check it against the registry via Contains.
        foreach (var segment in headerValue.Split(','))
        {
            var lang = segment.Trim().Split(';')[0].Trim();
            if (lang.Length == 0)
            {
                continue;
            }

            // The primary subtag, normalized to lowercase (registry codes are lowercase).
            var primarySubtag = lang.Split('-')[0].ToLowerInvariant();

            if (supportedLanguages.Contains(primarySubtag))
            {
                return primarySubtag;
            }
        }

        return fallback;
    }

    /// <summary>
    /// The phrases of the button of one channel — <see cref="SignInWithChannel"/> and
    /// <see cref="ConfirmInChannel"/> with the untranslated name of the channel in the slot
    /// (<c>Confirm in WhatsApp</c>). Computed from the two templates and the name, so a phrase can never drift
    /// from the text the page looks it up by (<see cref="ChannelButton.Text"/>).
    /// </summary>
    /// <param name="channelName">Untranslated name of the channel (<see cref="ChannelNames.GetChannelName"/>).</param>
    /// <returns>The two phrases.</returns>
    internal static IReadOnlyList<string> ChannelPhrases(string channelName) =>
    [
        ChannelButton.Phrase(SignInWithChannel, channelName),
        ChannelButton.Phrase(ConfirmInChannel, channelName)
    ];
}
