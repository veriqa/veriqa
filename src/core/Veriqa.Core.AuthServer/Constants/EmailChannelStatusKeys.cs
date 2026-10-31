// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// Intermediate Email transaction statuses the sign-in window shows the user (SPEC-016 §10.1).
/// These duplicate the values the Email channel publishes — solely for use in AuthServer (the UI
/// renderer), the same reason and the same shape as <see cref="EmailEndpointPaths"/>: the auth
/// server renders a status the channel published, and it does so without depending on the channel's
/// own assembly. The values are wire-level codes carried in
/// <c>TransactionChannelStatusChangedEvent</c>, so they are as stable as the event itself.
/// </summary>
internal static class EmailChannelStatusKeys
{
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
}
