// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.Abstractions;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// What the address form of Email Pull carries in the sign-in window, in the page of the Email buttons of hop
/// and in the window of a confirmation (SPEC-016 §4): one field for the address and one button that sends the
/// mail, the button named by what the mail carries, and — where the mail carries a code — the step of the
/// code on the same place, shown by the page script once the start says the mail went out with a code. The
/// user is given no choice of what the mail carries: the integrator chooses it by configuration.
/// </summary>
internal static class EmailPullForm
{
    /// <summary>
    /// Element id of the step of the code. Rendered hidden: without the page script the code is never asked
    /// for, and the form posts the address to the start by itself.
    /// </summary>
    public const string CodeStepElementId = "veriqa-email-code-step";

    /// <summary>
    /// What a mail of Pull carries, as the texts of the form tell it apart.
    /// </summary>
    public enum Mail
    {
        /// <summary>The magic link alone.</summary>
        Link,

        /// <summary>A code alone.</summary>
        Code,

        /// <summary>The magic link and a code.</summary>
        LinkAndCode
    }

    /// <summary>
    /// What the mail of the Pull mode of a channel carries.
    /// </summary>
    /// <param name="channel">The Email channel.</param>
    /// <returns>The kind of the mail.</returns>
    public static Mail MailOf(ChannelDisplayInfo channel) =>
        (channel.EmailPullMethods.Contains(EmailPullMethod.MagicLink), channel.EmailPullMethods.Contains(EmailPullMethod.Code)) switch
        {
            (true, true) => Mail.LinkAndCode,
            (false, true) => Mail.Code,
            _ => Mail.Link
        };

    /// <summary>
    /// Builds the hidden step of the code, placed inside the address form ahead of its message line: the line
    /// naming the address the code was sent to (filled by the script), the code field, the button that checks
    /// the code and the text link that brings the address step back. The field has no name, so the form posted
    /// without the script never carries it; its buttons are plain buttons, so neither sends the form.
    /// </summary>
    /// <param name="codeLabel">Localized accessible name of the code field.</param>
    /// <param name="confirmText">Localized text of the button that checks the code.</param>
    /// <param name="sendAgainText">Localized text of the link that brings the address step back.</param>
    /// <returns>The markup.</returns>
    public static string CodeStepMarkup(string codeLabel, string confirmText, string sendAgainText) =>
        $$"""
                <div class="veriqa-email-code" id="{{CodeStepElementId}}" hidden>
                    <p class="veriqa-email-code-sent" id="veriqa-email-code-sent"></p>
                    <input
                        class="veriqa-email-input veriqa-email-code-input"
                        id="veriqa-email-code"
                        type="text"
                        inputmode="numeric"
                        autocomplete="one-time-code"
                        aria-label="{{WebUtility.HtmlEncode(codeLabel)}}"
                    />
                    <button class="veriqa-btn" id="veriqa-email-code-submit" type="button">{{WebUtility.HtmlEncode(confirmText)}}</button>
                    <button class="veriqa-text-link" id="veriqa-email-resend" type="button">{{WebUtility.HtmlEncode(sendAgainText)}}</button>
                </div>
        """;
}
