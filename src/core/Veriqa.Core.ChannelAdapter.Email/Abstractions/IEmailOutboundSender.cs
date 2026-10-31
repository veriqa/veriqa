// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Domain;

namespace Veriqa.Core.ChannelAdapter.Email.Abstractions;

/// <summary>
/// Contract of the outbound email delivery provider for Pull mode (SPEC-016 §7.1).
/// The implementation is selected via configuration and DI.
/// </summary>
public interface IEmailOutboundSender
{
    /// <summary>
    /// Sends a magic link email for Pull-mode authentication.
    /// </summary>
    /// <param name="message">Data for building and sending the email.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The email send result.</returns>
    Task<EmailSendResult> SendLoginEmailAsync(
        EmailLoginMessage message,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether this provider sends the mails of the claims completion (SPEC-041 L51). A provider that
    /// does not leaves the <c>Code</c> and <c>MagicLink</c> ways of obtaining the email unavailable,
    /// and the web step offers only what remains.
    /// </summary>
    /// <remarks>
    /// The default answers <c>false</c>, so a provider written before these mails existed keeps
    /// compiling and is simply not asked to send them.
    /// </remarks>
    bool SupportsClaimCompletionEmail => false;

    /// <summary>
    /// Sends a mail of the claims completion — a code or a link that confirms the recipient address.
    /// </summary>
    /// <remarks>
    /// The default refuses the send; it is reached only by a provider that answers
    /// <see cref="SupportsClaimCompletionEmail"/> with <c>false</c>, which is never asked to send.
    /// </remarks>
    /// <param name="message">Data for building and sending the email.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The email send result.</returns>
    Task<EmailSendResult> SendClaimCompletionEmailAsync(
        EmailClaimCompletionMessage message,
        CancellationToken cancellationToken) =>
        Task.FromResult(EmailSendResult.Fail(
            EmailAdapterConstants.ErrorCodeDeliveryFailed,
            "This delivery provider does not send the mails of the claims completion."));
}
