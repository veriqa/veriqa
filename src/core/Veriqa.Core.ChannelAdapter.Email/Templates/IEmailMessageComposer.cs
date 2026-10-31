// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Email.Domain;

namespace Veriqa.Core.ChannelAdapter.Email.Templates;

/// <summary>
/// Assembly of the mail body — shared by every <c>IEmailOutboundSender</c> implementation, so a
/// delivery provider only deals with transport (MIME, attachments, the connection) and never owns the
/// body.
/// </summary>
/// <remarks>
/// This is the ONE code seam of the mail body. The wording of a mail is configuration — the message
/// <c>sign-in-mail</c> (worded for a confirmation at <c>ByType:confirmation</c>) with its slot contract and its
/// ladder of template variants (SPEC-036) — and a
/// host changes it there, without C#. Replacing this port is for what configuration cannot express:
/// slot values of the host's own, a foreign render engine, or a subject the host decides itself.
/// </remarks>
public interface IEmailMessageComposer
{
    /// <summary>
    /// Renders the mail body for one send.
    /// </summary>
    /// <param name="message">The sign-in mail data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ready mail body.</returns>
    ValueTask<EmailBodyContent> ComposeAsync(
        EmailLoginMessage message,
        CancellationToken cancellationToken = default);
}
