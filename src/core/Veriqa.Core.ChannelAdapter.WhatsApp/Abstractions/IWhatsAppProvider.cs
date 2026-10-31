// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Common;

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Abstractions;

/// <summary>
/// Abstraction of a WhatsApp message delivery provider.
/// Each provider implements outgoing message sending, health checking, and the two provider-specific
/// halves of the inbound path — how its webhook proves authenticity and in what format it carries the
/// messages (SPEC-003 §10.2) — while the channel logic (the sign-in code, the buttons, identity,
/// outcomes) stays unified in the adapter.
/// </summary>
public interface IWhatsAppProvider
{
    /// <summary>
    /// Code of this provider; matched against the configured <c>Veriqa:Channels:WhatsApp:Provider</c>
    /// at startup. The shipped codes live in <c>WhatsAppProviderNames</c>; a provider supplied by the
    /// host names itself with its own code.
    /// </summary>
    string ProviderType { get; }

    /// <summary>
    /// Sends a text message to the user.
    /// </summary>
    /// <param name="recipientUserId">
    /// Business-scoped user ID (BSUID) of the recipient, as it arrived in
    /// <see cref="WhatsAppInboundMessage.SenderUserId"/>.
    /// </param>
    /// <param name="messageText">Message text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the send succeeded.</returns>
    Task<bool> SendTextMessageAsync(
        string recipientUserId,
        string messageText,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an interactive message with confirm/decline buttons.
    /// </summary>
    /// <param name="recipientUserId">
    /// Business-scoped user ID (BSUID) of the recipient, as it arrived in
    /// <see cref="WhatsAppInboundMessage.SenderUserId"/>.
    /// </param>
    /// <param name="bodyText">Message body text.</param>
    /// <param name="confirmButtonId">Confirm button identifier.</param>
    /// <param name="confirmButtonTitle">Confirm button text.</param>
    /// <param name="declineButtonId">Decline button identifier.</param>
    /// <param name="declineButtonTitle">Decline button text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the send succeeded.</returns>
    Task<bool> SendInteractiveButtonsMessageAsync(
        string recipientUserId,
        string bodyText,
        string confirmButtonId,
        string confirmButtonTitle,
        string declineButtonId,
        string declineButtonTitle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the user to share the contact of their own account (SPEC-041 R9): an interactive
    /// <c>request_contact_info</c> message with the text, and the reply button of the secondary action of
    /// the phone step (L73) — a separate message where the platform cannot put it next to the request.
    /// </summary>
    /// <param name="recipientUserId">
    /// Business-scoped user ID (BSUID) of the recipient, as it arrived in
    /// <see cref="WhatsAppInboundMessage.SenderUserId"/>.
    /// </param>
    /// <param name="bodyText">Text of the request.</param>
    /// <param name="secondaryButtonId">Identifier of the reply button of the secondary action.</param>
    /// <param name="secondaryButtonTitle">Text of the reply button of the secondary action.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — both the request and the button were sent; <c>false</c> — not sent, or the
    /// provider does not support the request. The default body answers <c>false</c>, so a provider built
    /// before this member keeps compiling and simply leaves the phone step waiting (SPEC-041 E64).</returns>
    Task<bool> SendContactRequestAsync(
        string recipientUserId,
        string bodyText,
        string secondaryButtonId,
        string secondaryButtonTitle,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Sends an interactive <c>cta_url</c> message: the body text and one button that opens a link
    /// (SPEC-041 C76, SPEC-012 §4.9). The values are already fitted into the limits of the form by the
    /// adapter. The link may carry a claim form token, so it is never written to a log.
    /// </summary>
    /// <param name="recipientUserId">
    /// Business-scoped user ID (BSUID) of the recipient, as it arrived in
    /// <see cref="WhatsAppInboundMessage.SenderUserId"/>.
    /// </param>
    /// <param name="bodyText">Message body text.</param>
    /// <param name="buttonText">Caption of the button.</param>
    /// <param name="url">Address the button opens.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — sent; <c>false</c> — not sent, or the provider does not support the
    /// message. The default body answers <c>false</c>, so a provider built before this member keeps
    /// compiling and the core treats the link as undelivered.</returns>
    Task<bool> SendLinkButtonMessageAsync(
        string recipientUserId,
        string bodyText,
        string buttonText,
        string url,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Sends an interactive reply buttons message with one to three options (SPEC-012 §4.9). The values
    /// are already fitted into the limits of the form by the adapter.
    /// </summary>
    /// <param name="recipientUserId">
    /// Business-scoped user ID (BSUID) of the recipient, as it arrived in
    /// <see cref="WhatsAppInboundMessage.SenderUserId"/>.
    /// </param>
    /// <param name="bodyText">Message body text.</param>
    /// <param name="buttons">Reply buttons, in the order they are displayed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — sent; <c>false</c> — not sent, or the provider does not support the
    /// message (the default body).</returns>
    Task<bool> SendReplyButtonsMessageAsync(
        string recipientUserId,
        string bodyText,
        IReadOnlyList<WhatsAppReplyOption> buttons,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Sends an interactive list message with one to ten rows in one section (SPEC-012 §4.9). The values
    /// are already fitted into the limits of the form by the adapter.
    /// </summary>
    /// <param name="recipientUserId">
    /// Business-scoped user ID (BSUID) of the recipient, as it arrived in
    /// <see cref="WhatsAppInboundMessage.SenderUserId"/>.
    /// </param>
    /// <param name="bodyText">Message body text.</param>
    /// <param name="listButtonText">Caption of the button that opens the list; also the title of its
    /// section.</param>
    /// <param name="rows">Rows of the list, in the order they are displayed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — sent; <c>false</c> — not sent, or the provider does not support the
    /// message (the default body).</returns>
    Task<bool> SendListMessageAsync(
        string recipientUserId,
        string bodyText,
        string listButtonText,
        IReadOnlyList<WhatsAppReplyOption> rows,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Checks provider availability and credential validity.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the provider is available; error details when unavailable.</returns>
    Task<(bool IsHealthy, string? Details)> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the authenticity of an inbound webhook request with this provider's model
    /// (a GET handshake where the provider has one, a signature on POST).
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>Success(true)</c> — accepted; <c>Success(false)</c> — rejected; a failure — the check itself failed
    /// (it maps to the error path of the webhook pipeline).
    /// </returns>
    Task<Result<bool>> ValidateWebhookAsync(ChannelInboundRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses the body of an already validated request into neutral messages. Never throws on a malformed
    /// body: an unreadable or foreign payload yields an event with no messages.
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <returns>The messages the request carries, in the order they arrived, with the provider's parsed payload.</returns>
    WhatsAppInboundEvent ReadInboundEvent(ChannelInboundRequest request);
}
