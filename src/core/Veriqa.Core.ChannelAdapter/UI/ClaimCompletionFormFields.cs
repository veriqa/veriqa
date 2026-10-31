// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.UI;

/// <summary>
/// Names of the posted fields of the form of the claims completion — one spelling for the form that renders
/// them and the handler that reads them back. The right fields the forms carry besides (the session
/// identifier, the anti-forgery token) keep the names the callback already accepts; the form token is the
/// one right field of the form's own. The email block posts fields of its own besides.
/// </summary>
internal static class ClaimCompletionFormFields
{
    /// <summary>
    /// Name of the form token (SPEC-041 N78) — the right to act on the form on the page of the device that
    /// confirmed the sign-in. The same name is the query parameter of the link that opens that page, and a
    /// form that carries a field of this name is checked by the token alone (C39).
    /// </summary>
    public const string FormToken = "form_token";

    /// <summary>
    /// Prefix of the name of a posted value: the value of a claim is posted as the prefix followed by the
    /// claim name, so the values cannot collide with the right fields of the form.
    /// </summary>
    public const string ClaimValuePrefix = "claim.";

    /// <summary>
    /// Name of the field that says which action of the email block is posted (SPEC-041 L108); its values are
    /// <see cref="EmailOperations"/>. A request that posts <see cref="EmailMethod"/> without this field is
    /// the switch to that way: on a page with simple fields the button of a way belongs to the form of the
    /// fields and can post one field only.
    /// </summary>
    public const string EmailOperation = "email_op";

    /// <summary>
    /// Name of the email address the user entered in the email block.
    /// </summary>
    public const string EmailAddress = "email";

    /// <summary>
    /// Name of the code from the completion mail the user entered in the email block.
    /// </summary>
    public const string EmailCode = "code";

    /// <summary>
    /// Name of the way of obtaining the email the user chose in the email block — the name of the method.
    /// </summary>
    public const string EmailMethod = "method";

    /// <summary>
    /// Name the value of a claim is posted under.
    /// </summary>
    /// <param name="claim">Claim name.</param>
    /// <returns>Name of the posted field.</returns>
    public static string ClaimValueName(string claim) => ClaimValuePrefix + claim;

    /// <summary>
    /// Values of <see cref="EmailOperation"/> — the actions of the email block; any other value is refused.
    /// </summary>
    public static class EmailOperations
    {
        /// <summary>
        /// Send a completion mail to the entered address. A request that posts no address field at all
        /// sends it to the address of the last mail.
        /// </summary>
        public const string Send = "send";

        /// <summary>
        /// Show the address step of the way "confirm via email" again, its input filled with the address of
        /// the last mail — "Send again" while a mail is live. Nothing is sent or written: the mail goes out
        /// from that step, and only the answer to this action shows it — the page shown afresh shows the
        /// live mail again.
        /// </summary>
        public const string Address = "address";

        /// <summary>
        /// Check the entered code.
        /// </summary>
        public const string Verify = "verify";

        /// <summary>
        /// Save the entered address without verifying it.
        /// </summary>
        public const string Save = "save";

        /// <summary>
        /// Switch to another way of obtaining the email.
        /// </summary>
        public const string Method = "method";

        /// <summary>
        /// Show the block again as it stands — after the user confirmed the link or sent the mail elsewhere.
        /// </summary>
        public const string Refresh = "refresh";

        /// <summary>
        /// Change the address the block holds: the email is asked for again, and the block shows the address
        /// step of its way with that address in the input. A confirmed address is confirmed again; the limits
        /// of the mails go on counting.
        /// </summary>
        public const string Change = "change";
    }
}
