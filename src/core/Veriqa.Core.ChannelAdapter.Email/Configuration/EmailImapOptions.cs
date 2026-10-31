// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Email.Enums;

namespace Veriqa.Core.ChannelAdapter.Email.Configuration;

/// <summary>
/// Settings of the mailbox Veriqa reads over IMAP in Push mode. Read and validated only when
/// <see cref="EmailInboundOptions.Provider"/> is <see cref="EmailInboundProvider.ImapPolling"/>.
/// </summary>
public sealed class EmailImapOptions
{
    /// <summary>
    /// The IMAP server host.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// The IMAP server port. Default — 993 (implicit TLS).
    /// </summary>
    public int Port { get; set; } = EmailAdapterConfigDefaults.DefaultImapPort;

    /// <summary>
    /// The user name of the mailbox.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// How Veriqa signs in to the mailbox. Default — <see cref="EmailImapAuthMode.Password"/>.
    /// </summary>
    public EmailImapAuthMode AuthMode { get; set; } = EmailImapAuthMode.Password;

    /// <summary>
    /// The password of the mailbox, or its application password. Required with
    /// <see cref="EmailImapAuthMode.Password"/>, ignored otherwise.
    /// Must be stored via the standard secrets mechanism from SPEC-012 (EM-052).
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The folder Veriqa reads. Default — <c>INBOX</c>. Every unread message of the folder that is
    /// recent enough to carry a live token gets marked as read, so the folder has to be dedicated.
    /// </summary>
    public string Folder { get; set; } = EmailAdapterConfigDefaults.DefaultImapFolder;

    /// <summary>
    /// Upper bound, in seconds, of the pause between two visits to the mailbox. Default — 10.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = EmailAdapterConfigDefaults.DefaultImapPollIntervalSeconds;

    /// <summary>
    /// The <c>authserv-id</c> the mail server of the mailbox writes into its
    /// <c>Authentication-Results</c> header (RFC 8601). SPF, DKIM and DMARC results are taken only
    /// from a header carrying this identifier: any other one may have been written by the sender.
    /// Required when <see cref="EmailInboundOptions.VerificationPolicy"/> is
    /// <see cref="EmailVerificationPolicy.DmarcAlignedPass"/> or
    /// <see cref="EmailVerificationPolicy.SpfOrDkimPass"/>.
    /// </summary>
    public string AuthservId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth 2.0 settings of the sign-in. Used only with <see cref="EmailImapAuthMode.OAuth2"/>.
    /// </summary>
    public EmailImapOAuthOptions OAuth { get; set; } = new();
}
