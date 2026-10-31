// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// Outcome of sending a completion mail (SPEC-041 E61): sent, or the reason it was not.
/// </summary>
internal enum ClaimEmailSendOutcome
{
    /// <summary>
    /// The mail was delivered to the sender, and its code or link is now the live one.
    /// </summary>
    Sent,

    /// <summary>
    /// The mails of the transaction are used up.
    /// </summary>
    TransactionLimit,

    /// <summary>
    /// The mails of the channel account within its window are used up.
    /// </summary>
    SubjectLimit,

    /// <summary>
    /// The mails to the recipient address within its window are used up; another address may be entered.
    /// </summary>
    AddressLimit,

    /// <summary>
    /// The mails to the recipient domain within its window are used up.
    /// </summary>
    DomainLimit,

    /// <summary>
    /// The previous mail of the transaction went out less than the minimal interval between mails ago;
    /// the same request is accepted once the interval has passed.
    /// </summary>
    TooSoon,

    /// <summary>
    /// The mail could not be sent; a code or link sent before stays live.
    /// </summary>
    DeliveryFailed,

    /// <summary>
    /// A repeat was asked for, but no mail was sent before in the transaction.
    /// </summary>
    NoAddress
}
