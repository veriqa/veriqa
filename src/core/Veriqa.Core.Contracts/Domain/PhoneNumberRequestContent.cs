// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// Content of a <see cref="ChannelMessageKind.PhoneNumberRequest"/> message: what the channel draws
/// next to the request text (<see cref="ChannelMessage.Text"/>) — a button that shares the number by
/// the platform's own means and a button of the secondary action of the step (SPEC-041 L44, L73).
/// </summary>
/// <remarks>
/// Every text here is already localized by the caller, like the message text itself. The channel
/// answers a shared number with <see cref="ChannelPhoneSharedResult"/> and a press of the secondary
/// button with <see cref="ChannelPhoneStepActionResult"/>; a channel whose secondary button can carry
/// no payload (a plain keyboard button) answers it with <see cref="ChannelUnaddressedResult"/> carrying
/// the label as its text.
/// </remarks>
public sealed record PhoneNumberRequestContent
{
    /// <summary>
    /// Transaction the request belongs to — what a callback button of the secondary action carries in
    /// its payload (<c>CallbackDataPrefixes.PhoneSkip</c> / <c>PhoneCancel</c> followed by this value).
    /// </summary>
    public required TransactionId TransactionId { get; init; }

    /// <summary>
    /// Label of the button that shares the phone number.
    /// </summary>
    public required string ShareButtonLabel { get; init; }

    /// <summary>
    /// Secondary action the step offers.
    /// </summary>
    public required PhoneStepAction SecondaryAction { get; init; }

    /// <summary>
    /// Label of the button of the secondary action.
    /// </summary>
    public required string SecondaryButtonLabel { get; init; }
}
