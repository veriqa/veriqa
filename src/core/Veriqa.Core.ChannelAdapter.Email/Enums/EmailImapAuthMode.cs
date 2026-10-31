// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Email.Enums;

/// <summary>
/// How Veriqa signs in to the mailbox it reads over IMAP
/// (<see cref="EmailInboundProvider.ImapPolling"/>).
/// </summary>
public enum EmailImapAuthMode
{
    /// <summary>
    /// User name and password — typically an application password of the mailbox.
    /// </summary>
    Password,

    /// <summary>
    /// OAuth 2.0: Veriqa obtains an access token from the configured authorization server and signs in
    /// with it. For mailboxes where password sign-in is switched off.
    /// </summary>
    OAuth2
}
