// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Email.Domain;
using Veriqa.Core.ChannelAdapter.Email.Templates;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// Assembles a mail of the claims completion out of its MESSAGE (SPEC-041 L51, SPEC-036): a code mail is
/// the message <c>claim-code-mail</c>, a link mail the message <c>claim-link-mail</c>, a mail with both the
/// message <c>claim-code-link-mail</c> — each a kind of its own with its own ladder, so the minimal step of
/// any of them leans only on the slots that kind guarantees.
/// Like <see cref="EmailMessageComposer"/> it builds no markup of its own: it supplies the slot values
/// and asks the message renderer for the HTML part and the plain-text part.
/// </summary>
/// <remarks>
/// The values are the ones the contracts of the three kinds declare and nothing else: no initiator details
/// and no value of the calling party reach the mail, even when a level of the configuration declares
/// such a slot on its own (SPEC-041 L51) — a slot with no value is simply unfilled.
/// </remarks>
internal sealed class ClaimCompletionMailComposer
{
    /// <summary>
    /// Localizer of the mail Natural Keys into the recipient's language.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// The single point at which the channel contour obtains a resolved message.
    /// </summary>
    private readonly IMessageTemplateAccessor _messageTemplates;

    /// <summary>
    /// Logger (the message renderer reports a broken translation and a degraded ladder through it).
    /// </summary>
    private readonly ILogger<ClaimCompletionMailComposer> _logger;

    /// <summary>
    /// Creates the composer.
    /// </summary>
    /// <param name="promptLocalizer">Localizer of the mail Natural Keys.</param>
    /// <param name="messageTemplates">Accessor of the resolved message of the mail.</param>
    /// <param name="logger">Logger.</param>
    public ClaimCompletionMailComposer(
        IConfirmationPromptLocalizer promptLocalizer,
        IMessageTemplateAccessor messageTemplates,
        ILogger<ClaimCompletionMailComposer> logger)
    {
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Renders the mail body for one send.
    /// </summary>
    /// <param name="message">The mail data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ready mail body; it references no image.</returns>
    public async ValueTask<EmailBodyContent> ComposeAsync(
        EmailClaimCompletionMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SlotNames.App] = message.ClientName,
            [SlotNames.ValidUntil] = message.ExpiresAt,
            [SlotNames.Lang] = MailDocumentLanguage.Resolve(message.Locale, _promptLocalizer),
        };

        TransactionSlotValues.AddBrandName(values, message.BrandName);

        // The kind follows what the mail carries: a code mail guarantees the code, a link mail the link,
        // a mail with both guarantees both.
        var kind = message switch
        {
            { Code: not null, Link: not null } => MessageKinds.ClaimCodeLinkMail,
            { Code: not null } => MessageKinds.ClaimCodeMail,
            { Link: not null } => MessageKinds.ClaimLinkMail,
            _ => throw new ArgumentException("A claims-completion mail states a code, a link or both.", nameof(message))
        };

        if (message.Code is { } code)
        {
            values[SlotNames.Code] = code;
        }

        if (message.Link is { } link)
        {
            values[SlotNames.Link] = link;
        }

        // Resolved once per send over the ownership of the transaction (SPEC-036 TPL-116); neither the
        // surface nor the channel narrows the wording of a mail.
        var mail = await _messageTemplates.RequireAsync(
            TransactionMessageAddress.Of(
                kind, surface: null, message.TransactionType, message.ActionType, channelType: null, linkPurpose: null),
            message.Ownership,
            cancellationToken);

        var html = MessageTextRenderer.RenderParts(
            mail, values, message.Locale, message.TimeZone, _promptLocalizer, MessageRenderMode.Html, _logger);

        var text = MessageTextRenderer.RenderParts(
            mail, values, message.Locale, message.TimeZone, _promptLocalizer, MessageRenderMode.PlainText, _logger);

        // As for the sign-in mail: the subject of the step chosen for the HTML part, and a part no step
        // states is a part the mail goes without (SPEC-016 §4.3).
        return new EmailBodyContent(
            html?.Subject ?? text?.Subject ?? string.Empty,
            html?.Body ?? string.Empty,
            text?.Body ?? string.Empty,
            EmbedsQrImage: false);
    }
}
