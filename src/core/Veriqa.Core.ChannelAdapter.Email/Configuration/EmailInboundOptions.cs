// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Email.Enums;

namespace Veriqa.Core.ChannelAdapter.Email.Configuration;

/// <summary>
/// Inbound email processing settings for Push mode (SPEC-016 §7.2, §8).
/// </summary>
public sealed class EmailInboundOptions
{
    /// <summary>
    /// Inbound processing provider. Default — Webhook (production default).
    /// </summary>
    public EmailInboundProvider Provider { get; set; } = EmailInboundProvider.Webhook;

    /// <summary>
    /// Inbound mailbox email address for Push mode.
    /// </summary>
    public string InboundAddress { get; set; } = string.Empty;

    /// <summary>
    /// Use plus addressing to embed the correlation token into the recipient address.
    /// For example: login+{token}@example.com.
    /// </summary>
    public bool UsePlusAddressing { get; set; } = true;

    /// <summary>
    /// Form of the recipient address of the Push sign-in mail (SPEC-016 §5.3). The Push QR/button carries a
    /// direct <c>mailto:</c> at either value; the setting chooses where the correlation token rides.
    /// True — <c>mailto:{token}@{domain}</c>: the token IS the recipient local part (requires a catch-all
    /// mailbox such as a Cloudflare Email Worker), and the inbound processor extracts it from the full
    /// recipient local part. The token is lower-case Base32 and the local part is folded to lower case
    /// before the lookup, so a mail client or provider that changes the case of the address does not break
    /// the correlation (SPEC-016 §5.4); the subject and the body stay short. False (default) — the
    /// recipient is the fixed <see cref="InboundAddress"/> (with the token in a plus segment when
    /// <see cref="UsePlusAddressing"/> is on), and the subject and the body of the mail carry the token.
    /// Mutually exclusive in intent with <see cref="UsePlusAddressing"/>: with a catch-all local-part
    /// token there is no plus segment.
    /// </summary>
    public bool TokenInLocalPart { get; set; } = false;

    /// <summary>
    /// Sender verification policy. Default — DmarcAlignedPass (production default).
    /// Using None is forbidden in production (EM-030).
    /// </summary>
    public EmailVerificationPolicy VerificationPolicy { get; set; } = EmailVerificationPolicy.DmarcAlignedPass;

    /// <summary>
    /// List of allowed sender domains/addresses.
    /// Used when <see cref="VerificationPolicy"/> = <see cref="EmailVerificationPolicy.AllowListOnly"/>.
    /// </summary>
    public IReadOnlyList<string> AllowedDomains { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Secret token for validating webhook requests from the inbound provider.
    /// Must be stored via the standard secrets mechanism from SPEC-012 (EM-052).
    /// </summary>
    public string WebhookSecretToken { get; set; } = string.Empty;

    /// <summary>
    /// The mailbox Veriqa reads over IMAP. Used only when <see cref="Provider"/> is
    /// <see cref="EmailInboundProvider.ImapPolling"/>; ignored with any other provider.
    /// </summary>
    public EmailImapOptions Imap { get; set; } = new();
}
