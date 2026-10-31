// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Sends the chat of the sender the one message of a transaction whose question is asked on the core
/// page (SPEC-012 CFG-266): the sign-in or the confirmation has started, and it is answered on the page
/// — so the person who has just written to the bot knows to look at the page rather than wait for the
/// chat. A sign-in and a confirmation get the same message; only the words naming the action differ,
/// and they come from the template, addressed by the type of the transaction (SPEC-036 TPL-130).
/// </summary>
/// <remarks>
/// The text is a message of the template mechanism (<see cref="MessageKinds.WebConfirmationNotice"/>),
/// resolved through the one accessor of the contour at the address of the transaction, so a tenant, an
/// application and a channel may each state a wording of their own (SPEC-036 TPL-001). It leaves through
/// the content-kind gate of the channel, which logs and counts a refused send; nothing is retried and the
/// transaction is not touched. Whatever fails here is logged and swallowed: the identity is already
/// attached, and the transaction continues on the page whether the chat got the message or not.
/// </remarks>
internal sealed class WebConfirmationNoticeSender
{
    /// <summary>
    /// The one point at which the contour obtains a message.
    /// </summary>
    private readonly IMessageTemplateAccessor _messageTemplates;

    /// <summary>
    /// Channel message localizer.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// Component metrics a refused send is counted into.
    /// </summary>
    private readonly ChannelAdapterMetrics _metrics;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<WebConfirmationNoticeSender> _logger;

    /// <summary>
    /// Creates the sender.
    /// </summary>
    /// <param name="messageTemplates">Message accessor of the contour.</param>
    /// <param name="promptLocalizer">Channel message localizer.</param>
    /// <param name="metrics">Component metrics.</param>
    /// <param name="logger">Logger.</param>
    public WebConfirmationNoticeSender(
        IMessageTemplateAccessor messageTemplates,
        IConfirmationPromptLocalizer promptLocalizer,
        ChannelAdapterMetrics metrics,
        ILogger<WebConfirmationNoticeSender> logger)
    {
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Resolves the notice at the address of the transaction, renders it as plain text for the
    /// recipient and sends it into the chat.
    /// </summary>
    /// <remarks>
    /// The caller values are left out: they were accepted against the contract of another message, and
    /// showing them outside that message is admitted for the outcome receipts alone (SPEC-036 TPL-123).
    /// </remarks>
    /// <param name="transaction">Transaction whose question has just been handed over to the core page.</param>
    /// <param name="adapter">Adapter of the channel the sender wrote through.</param>
    /// <param name="channelUserId">Recipient within the channel.</param>
    /// <param name="recipientLocale">Recipient locale — the chain the receipt of the same branch is
    /// worded in (null — base language).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SendAsync(
        Transaction transaction,
        IChannelAdapter adapter,
        string channelUserId,
        string? recipientLocale,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(adapter);

        try
        {
            var template = await _messageTemplates.FindAsync(
                InChannelMessageAddress.Of(MessageKinds.WebConfirmationNotice, transaction, adapter.ChannelType),
                TransactionResolutionContext.For(transaction),
                cancellationToken);

            if (template is null)
            {
                _logger.LogWarning(
                    "No level declares a message of kind {MessageKind} — the web confirmation notice is not sent. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                    MessageKinds.WebConfirmationNotice,
                    adapter.ChannelType,
                    transaction.Id.ToString());
                return;
            }

            var text = MessageTextRenderer.Render(
                template,
                TransactionSlotValues.Of(
                    TransactionSlotSource.From(transaction).WithoutCallerValues(),
                    template.Contract,
                    outcomeMoment: null,
                    MessageSurfaces.InChannel),
                recipientLocale,
                transaction.GetUiTimeZone(),
                _promptLocalizer,
                MessageRenderMode.PlainText,
                _logger);

            if (text is null)
            {
                _logger.LogWarning(
                    "No step of the resolved ladder of message kind {MessageKind} states a plain text — the web confirmation notice is not sent. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                    template.Kind,
                    adapter.ChannelType,
                    transaction.Id.ToString());
                return;
            }

            await ChannelOutbound.SendMessageAsync(
                adapter,
                new ChannelMessage
                {
                    ChannelUserId = channelUserId,
                    Text = text
                },
                _logger,
                _metrics,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "The web confirmation notice failed; the transaction continues on the core page. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                adapter.ChannelType,
                transaction.Id.ToString());
        }
    }
}
