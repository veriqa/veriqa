// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Constants;

/// <summary>
/// Well-known content kinds a channel message can carry (SPEC-003 §6.2).
/// The set is open: a third-party adapter may declare its own code, which is why the codes live
/// here as constants and the kind itself is an extensible value, not a closed enum.
/// </summary>
public static class ChannelMessageKinds
{
    /// <summary>
    /// Plain text without markup — the only kind every channel is required to accept.
    /// </summary>
    public const string PlainText = "plain_text";

    /// <summary>
    /// A request for the user's phone number: the text of the request, a button that shares the
    /// number by the platform's own means and a button of the secondary action of the step
    /// (SPEC-041 L44, L73). The content travels in <c>ChannelMessage.PhoneNumberRequest</c>.
    /// </summary>
    public const string PhoneNumberRequest = "phone_number_request";

    /// <summary>
    /// A link to the claim completion form on the device that confirmed the sign-in: the text above
    /// the button and one button that opens the link (SPEC-041 C76). A channel declares that it can
    /// draw such a button by listing this kind among its supported kinds — there is no separate flag.
    /// The content travels in <c>ChannelMessage.ClaimFormLink</c>.
    /// </summary>
    public const string ClaimFormLink = "claim_form_link";

    /// <summary>
    /// A text with buttons under it, each button either a link or a callback whose payload comes back
    /// as an inbound event — the message of the post-sign-in actions (SPEC-012 §4.9). A channel
    /// declares that it can draw such buttons by listing this kind among its supported kinds.
    /// The content travels in <c>ChannelMessage.ActionButtons</c>.
    /// </summary>
    public const string ActionButtons = "action_buttons";
}
