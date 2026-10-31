// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// Intermediate channel statuses of the claims completion: the codes that tell a page of the device the
/// sign-in was started on what happened to the completion elsewhere — the form is being filled on the
/// device that confirmed the sign-in (SPEC-041 L77, SPEC-025 HOP-104a), the address of the email was
/// confirmed by the link of its mail or by the mail its owner sent. They travel in the same <c>TransactionChannelStatusChangedEvent</c>
/// and the same hub method as the codes of <see cref="HopChannelStatusKeys"/> and
/// <see cref="EmailChannelStatusKeys"/>, and repeat none of them.
/// <para>
/// The statuses are informative (best-effort, SPEC-025 HOP-189): they neither end nor advance the
/// transaction, and no page relies on them — a page that misses one is where it would be without it. The
/// values are wire-level codes, as stable as the event that carries them.
/// </para>
/// </summary>
internal static class ClaimCompletionChannelStatusKeys
{
    /// <summary>
    /// The form of the web step is open on the device that confirmed the sign-in — the link button reached
    /// the chat, or the confirmation page of the mail showed the form. The event names the channel of the
    /// transaction.
    /// </summary>
    public const string FormOnPhone = "claim_form";

    /// <summary>
    /// The address of the email of the completion was confirmed — by the link of its mail, opened on any
    /// device, or by the mail its owner sent (the way <c>Push</c>); the event names the channel <c>email</c>.
    /// The form of the web step, on either device, then performs its action "Check status", as if
    /// pressed, so the block shows the confirmed address without the user pressing it. The Email adapter publishes the
    /// code with a copy of this value of its own, as it does the codes of <see cref="EmailChannelStatusKeys"/>.
    /// </summary>
    public const string EmailConfirmed = "email_confirmed";
}
