// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Email.Enums;

/// <summary>
/// Inbound email processing provider for Push mode (SPEC-016 §7.2).
/// </summary>
public enum EmailInboundProvider
{
    /// <summary>
    /// Webhook — the provider calls the Veriqa HTTPS endpoint when a new message arrives.
    /// Production default.
    /// </summary>
    Webhook,

    /// <summary>
    /// ImapPolling — Veriqa periodically reads the mailbox over IMAP.
    /// Suitable for on-premise and dev.
    /// </summary>
    ImapPolling,

    /// <summary>
    /// SmtpRelay — Veriqa receives messages through its own SMTP relay.
    /// Reserved: no receiver is shipped, and selecting it with Push enabled fails the start.
    /// </summary>
    SmtpRelay,

    /// <summary>
    /// Custom — client-provided inbound implementation via DI.
    /// </summary>
    Custom
}
