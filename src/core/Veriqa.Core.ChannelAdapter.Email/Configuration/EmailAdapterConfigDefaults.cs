// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Email.Enums;

namespace Veriqa.Core.ChannelAdapter.Email.Configuration;

/// <summary>
/// Default configuration constants of the Email adapter.
/// Used as default values in the Options classes.
/// </summary>
internal static class EmailAdapterConfigDefaults
{
    /// <summary>
    /// Default preferred sign-in mode — Push (the one-tap email), the most convenient one on a mobile
    /// device; Pull is the manual fallback.
    /// </summary>
    public const EmailMode DefaultPreferredMode = EmailMode.Push;

    /// <summary>
    /// Default set of what the mail of a sign-in by an address carries — the magic link alone, the mail every
    /// deployment sent before the set existed.
    /// </summary>
    public static readonly IReadOnlyList<EmailPullMethod> DefaultPullMethods = [EmailPullMethod.MagicLink];

    /// <summary>
    /// Default number of attempts at entering one code of a sign-in mail — the number the code of the claims
    /// completion ships with (SPEC-041 E61).
    /// </summary>
    public const int DefaultSignInCodeAttempts = 5;

    /// <summary>
    /// Default number of sign-in mails to one recipient address within the window of the address counter
    /// (one hour) — the number the mails of the claims completion ship with (SPEC-041 E61).
    /// </summary>
    public const int DefaultSignInMailsPerAddress = 5;

    /// <summary>
    /// Default number of sign-in mails to one recipient domain within the window of the domain counter (one
    /// hour): <c>0</c> — the limit is off, as for the mails of the claims completion (SPEC-041 E61). One
    /// counter per domain would block the mass domains of mailbox providers, so an operator switches it on.
    /// </summary>
    public const int DefaultSignInMailsPerDomain = 0;

    /// <summary>
    /// Default client application name in the email — used when the transaction has no
    /// initiator context (SPEC-017) (capture is disabled or the transaction was created
    /// before it was enabled) and the client's DisplayName cannot be determined.
    /// </summary>
    public const string DefaultClientName = "Veriqa";

    /// <summary>
    /// Default action/correlation token TTL — 5 minutes.
    /// </summary>
    public static readonly TimeSpan DefaultTokenTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default minimal interval between two mails of one kind within a transaction — 30 seconds.
    /// </summary>
    public static readonly TimeSpan DefaultMailResendInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default IMAP port — 993, the port of IMAP over implicit TLS.
    /// </summary>
    public const int DefaultImapPort = 993;

    /// <summary>
    /// Default IMAP folder Veriqa reads.
    /// </summary>
    public const string DefaultImapFolder = "INBOX";

    /// <summary>
    /// Default upper bound of the pause between two visits to the mailbox, in seconds.
    /// </summary>
    public const int DefaultImapPollIntervalSeconds = 10;

    /// <summary>
    /// Lowest pause between two visits to the mailbox a deployment may set, in seconds.
    /// </summary>
    public const int MinImapPollIntervalSeconds = 1;

    /// <summary>
    /// Highest pause between two visits to the mailbox a deployment may set, in seconds — one hour.
    /// </summary>
    public const int MaxImapPollIntervalSeconds = 3600;

    /// <summary>
    /// Lowest TCP port number.
    /// </summary>
    public const int MinTcpPort = 1;

    /// <summary>
    /// Highest TCP port number.
    /// </summary>
    public const int MaxTcpPort = 65535;
}
