// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Domain;
using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Email.Templates;

/// <summary>
/// Default <see cref="IEmailMessageComposer"/>: assembles the mail out of the sign-in mail MESSAGE —
/// the two settings a message is made of, resolved for this send (SPEC-016 §4.3). The kind follows what the
/// mail carries: the magic link alone is <c>sign-in-mail</c>, a code alone <c>sign-in-code-mail</c>, both
/// <c>sign-in-code-link-mail</c>.
/// </summary>
/// <remarks>
/// It builds no markup of its own. The document of the mail is the text of a template variant, one
/// language-independent string whose visible phrases are Natural Keys the contract names (SPEC-036
/// §4.1); the composer supplies the values of the slots, asks the message renderer for the HTML part
/// and for the plain-text part, and reports whether the variant that was rendered referenced the QR
/// attachment. The delivery provider owns MIME, the attachment and the connection. A host that needs
/// its own slot values, its own render engine or its own subject replaces this composer — that is the
/// one seam of the mail body, and configuration covers everything short of it.
/// </remarks>
internal sealed class EmailMessageComposer : IEmailMessageComposer
{
    /// <summary>
    /// Localizer of the mail Natural Keys into the recipient's language (SPEC-017 ICC-050).
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// The single point at which the channel contour obtains a resolved message.
    /// </summary>
    private readonly IMessageTemplateAccessor _messageTemplates;

    /// <summary>
    /// Logger (the message renderer reports a broken translation and a degraded ladder through it).
    /// </summary>
    private readonly ILogger<EmailMessageComposer> _logger;

    /// <summary>
    /// Creates the composer.
    /// </summary>
    /// <param name="promptLocalizer">Localizer of the mail Natural Keys (SPEC-017 ICC-050).</param>
    /// <param name="messageTemplates">Accessor of the resolved message of the mail.</param>
    /// <param name="logger">Logger.</param>
    public EmailMessageComposer(
        IConfirmationPromptLocalizer promptLocalizer,
        IMessageTemplateAccessor messageTemplates,
        ILogger<EmailMessageComposer> logger)
    {
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<EmailBodyContent> ComposeAsync(
        EmailLoginMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The message of the mail is resolved ONCE per send, here, on the asynchronous path; the
        // rendering below is synchronous while the resolution behind it is not (SPEC-036 TPL-001).
        // Neither the surface nor the channel is named: the mail IS the message of this kind and its
        // wording does not narrow by where it is shown. The ownership comes from the send, so a wording
        // declared by an application or a ui_config record answers instead of the shipped one
        // (SPEC-036 TPL-116).
        // The type of the transaction narrows the address, as it does for every message of the mechanism:
        // the product words the mail of a confirmation at ByType:confirmation, and a sign-in falls back to
        // the kind itself — the same mail, with the words of its action.
        var kind = KindOf(message);
        var point = string.IsNullOrEmpty(message.TransactionType)
            ? ConfigDimensionValues.Of((MessageTemplateConfigKeys.KindDimensionName, kind))
            : ConfigDimensionValues.Of(
                (MessageTemplateConfigKeys.KindDimensionName, kind),
                (MessageTemplateConfigKeys.TransactionTypeDimensionName, message.TransactionType));
        var mail = await _messageTemplates.RequireAsync(point, message.Ownership, cancellationToken);

        var locale = message.Locale;
        var timeZone = message.TimeZone;
        var values = SlotValues(message);

        // The two parts are two renders of ONE message: each asks the ladder for the edition it is about
        // to deliver, so the HTML wording and the plain-text wording stand side by side in one ladder and
        // the two parts may well come from DIFFERENT steps of it — the editions degrade apart, because
        // what each of them rests on differs (the QR of the HTML wording has no place in the text one).
        var html = MessageTextRenderer.RenderParts(
            mail, values, locale, timeZone, _promptLocalizer, MessageRenderMode.Html, _logger);

        // A null part is a one-part mail the deployment ASKED for: no step of its ladder states that
        // edition (SPEC-016 §4.3). The mail goes out without it rather than with an empty one.
        var text = MessageTextRenderer.RenderParts(
            mail, values, locale, timeZone, _promptLocalizer, MessageRenderMode.PlainText, _logger);

        // The subject is the one the step chosen for the HTML part states — the part a mail client shows
        // first; the step chosen for the plain-text part answers for it only when there is no HTML part
        // at all. The two steps may state different subjects, and a mail has one: the second is dropped
        // without a word, which is the stated resolution of the model and not an anomaly.
        var subject = html?.Subject ?? text?.Subject ?? string.Empty;

        // The attachment follows the text that was actually rendered: a QR image is shipped only when
        // the chosen HTML text referenced it by Content-Id, so a mail never carries an attachment
        // nothing points at, and never points at one that was not attached.
        return new EmailBodyContent(
            subject,
            html?.Body ?? string.Empty,
            text?.Body ?? string.Empty,
            EmbedsQrImage: html?.ReferencedSlots.Contains(SlotNames.QrCid) is true);
    }

    /// <summary>
    /// The message kind of a send: each kind guarantees what its mail carries, so the minimal step of its
    /// ladder leans only on that.
    /// </summary>
    /// <param name="message">The sign-in mail data.</param>
    /// <returns>The message kind.</returns>
    private static string KindOf(EmailLoginMessage message) =>
        message switch
        {
            { Code: not null, CarriesLink: true } => MessageKinds.SignInCodeLinkMail,
            { Code: not null } => MessageKinds.SignInCodeMail,
            { CarriesLink: true } => MessageKinds.SignInMail,
            _ => throw new ArgumentException("A sign-in mail carries the magic link, a code or both.", nameof(message))
        };

    /// <summary>
    /// Builds the slot values of the mail: the system fields this send knows and the initiator context
    /// of the transaction, mapped by the one place that maps it (SPEC-017 §7.2). The link and its QR code
    /// are values only of a mail that shows the link, and the code only of a mail that carries one.
    /// </summary>
    /// <param name="message">The sign-in mail data.</param>
    /// <returns>Slot name → typed value.</returns>
    private Dictionary<string, object?> SlotValues(EmailLoginMessage message)
    {
        var values = message.InitiatorContext is DetailedConfirmationPromptContext details
            ? TransactionSlotValues.Of(details)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        // The application name of the mail is the one the send resolved (the initiator context with the
        // adapter's own default behind it), so it is always there — which is what lets the minimal
        // variant of the ladder rest on it.
        values[SlotNames.App] = message.ClientName;
        values[SlotNames.ValidUntil] = message.ExpiresAt;
        values[SlotNames.Lang] = MailDocumentLanguage.Resolve(message.Locale, _promptLocalizer);

        // The brand comes with every variant of the context, the one without the initiator details too.
        TransactionSlotValues.AddBrandName(values, message.InitiatorContext?.BrandName);

        if (message.Code is { } code)
        {
            values[SlotNames.Code] = code;
        }

        // A mail of the code alone does not show the link: its token only identifies the letter, so no value
        // of it — the link or the QR code that encodes it — is handed to the template.
        if (!message.CarriesLink)
        {
            return values;
        }

        values[SlotNames.Link] = EmailMagicLink.Build(message);

        // Content-Id of the attachment holding the magic link QR code, without the "cid:" prefix (the
        // markup reference is src="cid:{qr_cid}"). Whether an attachment is issued follows from the
        // variant that was rendered referencing it, not from this value being present.
        values[SlotNames.QrCid] = EmailAdapterConstants.PullQrContentId;

        return values;
    }
}
