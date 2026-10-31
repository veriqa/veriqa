// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.ClaimCompletion;

/// <summary>
/// Natural Keys of the errors of the email block of the web step (SPEC-041 L56, L108, E61) — the texts the
/// page shows in the block after an action or when the block cannot offer its way. Resolved against the
/// locale files of the host where the error is decided.
/// </summary>
internal static class EmailCompletionBlockTexts
{
    /// <summary>
    /// The entered text is not an email address.
    /// </summary>
    public const string InvalidAddress = "Invalid email address.";

    /// <summary>
    /// The entered code does not have the shape of a code; no attempt was spent.
    /// </summary>
    public const string CodeShape = "Enter all digits of the code from the email.";

    /// <summary>
    /// The entered code does not match; the slot takes the attempts left.
    /// </summary>
    public const string WrongCode = "Wrong code. Attempts left: {attempts}";

    /// <summary>
    /// Slot of <see cref="WrongCode"/> the attempts left go into.
    /// </summary>
    public const string AttemptsSlot = "{attempts}";

    /// <summary>
    /// The live code has no attempts left, and a new mail may be sent — the block shows its address step
    /// beside this error.
    /// </summary>
    public const string CodeExhausted = "The code has no attempts left. Request a new email.";

    /// <summary>
    /// The live code has no attempts left, and no mail may be sent any more — no call to request one.
    /// </summary>
    public const string CodeNoAttemptsLeft = "The code has no attempts left.";

    /// <summary>
    /// The live code has expired.
    /// </summary>
    public const string CodeExpired = "The code has expired. Request a new email.";

    /// <summary>
    /// A code was entered before any was sent.
    /// </summary>
    public const string NoCode = "Request a code first.";

    /// <summary>
    /// The mails of the transaction or of the channel account are used up.
    /// </summary>
    public const string NoMoreMails = "No more emails can be sent.";

    /// <summary>
    /// The mails to the entered address are used up for now; another address may be entered. No moment of
    /// a repeat is named: the refusal of the limit does not carry one.
    /// </summary>
    public const string AddressLimit = "Too many emails have been sent to this address. Try again later or enter another address.";

    /// <summary>
    /// The mails to the domain of the entered address are used up for now.
    /// </summary>
    public const string DomainLimit = "Emails cannot be sent to this domain right now. Enter another address.";

    /// <summary>
    /// The previous mail of the sign-in went out less than the minimal interval between mails ago.
    /// </summary>
    public const string SendTooSoon = "An email has just been sent. Wait a little before requesting another one.";

    /// <summary>
    /// "Continue" saved the fields, but the email of the same page is still to be confirmed.
    /// </summary>
    public const string ConfirmAddressToContinue = "Confirm the address to continue.";

    /// <summary>
    /// "Continue" came with an address typed into the block that is not confirmed yet: nothing is sent or
    /// written until the address is confirmed or the field cleared.
    /// </summary>
    public const string ConfirmOrClearAddress = "Confirm your email address or clear the field.";

    /// <summary>
    /// The mail could not be sent.
    /// </summary>
    public const string DeliveryFailed = "Failed to send the mail. Try again later.";

    /// <summary>
    /// A mail was asked for without an address, and no mail was sent before.
    /// </summary>
    public const string NoAddress = "Enter your email address.";

    /// <summary>
    /// The way asked for is not available on this page.
    /// </summary>
    public const string MethodUnavailable = "This way is no longer available.";

    /// <summary>
    /// No way of obtaining the email is available on this page.
    /// </summary>
    public const string NoMethods = "The email address cannot be obtained right now.";

    /// <summary>
    /// The state of the mails of the transaction could not be read.
    /// </summary>
    public const string StateUnknown = "Email confirmation is temporarily unavailable. Try again later.";

    /// <summary>
    /// The mail the user sends could not be prepared.
    /// </summary>
    public const string PushUnavailable = "The email could not be prepared. Try again later.";

    /// <summary>
    /// The posted action is not one of the block.
    /// </summary>
    public const string UnknownAction = "The action could not be completed. Try again.";
}
