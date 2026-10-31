// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Email.Constants;

/// <summary>
/// Email-specific intermediate transaction statuses (SPEC-016 §10.1).
/// Published additively via <c>TransactionChannelStatusChangedEvent</c> and do not change
/// the transaction's lifecycle state (Pending/Confirmed/Completed/Expired/Failed).
/// The <c>confirmed</c> and <c>expired</c> statuses from SPEC-016 §10.1 are not published separately —
/// they are fully covered by the Confirmed/Expired lifecycle events.
/// Public class: the values are used both by the adapter (publication) and by the
/// auth page renderer (display to the user).
/// </summary>
public static class EmailChannelStatuses
{
    /// <summary>
    /// Pull: the magic-link email was successfully sent to the user.
    /// </summary>
    public const string PullSent = "sent";

    /// <summary>
    /// Pull: the confirmation page was opened with a valid action token
    /// (the email was opened, the user followed the link/QR).
    /// </summary>
    public const string PullOpened = "opened";

    /// <summary>
    /// Push: an inbound email was received from the inbound provider and accepted for processing.
    /// </summary>
    public const string PushMailReceived = "mail_received";

    /// <summary>
    /// Push: the email sender passed verification according to the configured policy.
    /// </summary>
    public const string PushVerified = "verified";

    /// <summary>
    /// Push: the email sender passed verification, but the transaction is bound to the subject of another
    /// channel, so the mail does not confirm it (SPEC-039 N34). Published instead of
    /// <see cref="PushVerified"/>.
    /// </summary>
    public const string PushSubjectMismatch = "subject_mismatch";

    /// <summary>
    /// The claims completion: the address of its email was confirmed — by the link of the completion mail or
    /// by the mail its owner sent (the way <c>Push</c>). A code of the completion rather than of an Email sign-in, so it stays out of the public set above:
    /// the auth server owns it and keeps the value of its own, which this copy repeats — the adapter
    /// publishes it without depending on the auth server's assembly.
    /// </summary>
    internal const string ClaimEmailConfirmed = "email_confirmed";
}
