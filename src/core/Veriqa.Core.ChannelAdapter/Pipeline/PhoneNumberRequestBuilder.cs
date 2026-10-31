// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Builds the phone number request of claim completion (SPEC-041 L44, L73) — the one builder of it:
/// the starter of the phone step asks with it, and the processing of an answer asks again with it when
/// a shared contact is not the sender's own (L46).
/// </summary>
/// <remarks>
/// The secondary button follows the level of the missing number: an <c>Optional</c> one may be
/// skipped, a <c>Required</c> one may not, and in its place the user cancels the sign-in (L73).
/// <para>
/// The text of the request (SPEC-041 L94) is a message of the template mechanism, and its kind follows
/// the level too: <see cref="MessageKinds.PhoneNumberRequest"/> for an <c>Optional</c> number,
/// <see cref="MessageKinds.PhoneNumberRequestRequired"/> for a <c>Required</c> one — what the user is
/// told to do without a number in the profile differs between the two. Either is resolved through the
/// one accessor of the contour (SPEC-036 TPL-116) at the address of the transaction, so a tenant, an
/// application and a channel may each state a wording of their own (TPL-001). A wording declared for
/// one kind does not answer for the other. The labels of the buttons are Natural Keys of the
/// shared channel localizer. Both are rendered into the language of the user of the channel snapshot —
/// the one the request is addressed to.
/// </para>
/// </remarks>
internal sealed class PhoneNumberRequestBuilder
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
    /// Configuration resolver: per-application resolution of the display decision of the initiator
    /// context (SPEC-017 ICC-081).
    /// </summary>
    private readonly IConfigurationResolver _configurationResolver;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<PhoneNumberRequestBuilder> _logger;

    /// <summary>
    /// Creates the builder.
    /// </summary>
    /// <param name="messageTemplates">Message accessor of the contour.</param>
    /// <param name="promptLocalizer">Channel message localizer.</param>
    /// <param name="configurationResolver">Configuration resolver.</param>
    /// <param name="logger">Logger.</param>
    public PhoneNumberRequestBuilder(
        IMessageTemplateAccessor messageTemplates,
        IConfirmationPromptLocalizer promptLocalizer,
        IConfigurationResolver configurationResolver,
        ILogger<PhoneNumberRequestBuilder> logger)
    {
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _configurationResolver = configurationResolver ?? throw new ArgumentNullException(nameof(configurationResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The secondary action the request of a number at the given level carries.
    /// </summary>
    /// <param name="level">Requirement level of the missing number.</param>
    /// <returns>The action; null — a number at this level is not asked for (L42).</returns>
    public static PhoneStepAction? SecondaryActionOf(ClaimRequirementLevel level) => level switch
    {
        ClaimRequirementLevel.Optional => PhoneStepAction.Skip,
        ClaimRequirementLevel.Required => PhoneStepAction.CancelSignIn,
        _ => null
    };

    /// <summary>
    /// The kind of the message that words the request of a number at the given level.
    /// </summary>
    /// <param name="level">Requirement level of the missing number.</param>
    /// <returns>The message kind: the one of a number the sign-in cannot finish without for
    /// <c>Required</c>, the one of a number that may be skipped otherwise.</returns>
    public static string MessageKindOf(ClaimRequirementLevel level) =>
        level is ClaimRequirementLevel.Required
            ? MessageKinds.PhoneNumberRequestRequired
            : MessageKinds.PhoneNumberRequest;

    /// <summary>
    /// The level of the missing phone number of a transaction's claims completion.
    /// </summary>
    /// <param name="transaction">Transaction in the completion.</param>
    /// <returns>The level; null — the number is not among the missing claims.</returns>
    public static ClaimRequirementLevel? PhoneLevelOf(Transaction transaction) =>
        transaction.ClaimCompletion?.Items
            .FirstOrDefault(item => VeriqaClaimTypes.NameComparer.Equals(item.Claim, VeriqaClaimTypes.PhoneNumber))
            ?.Level;

    /// <summary>
    /// Builds the request of the transaction's missing phone number and the record that ties the chat it
    /// goes to back to the transaction.
    /// </summary>
    /// <param name="transaction">Transaction in the phone step of its claims completion.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The request; null — the transaction has no channel snapshot, or its missing number is
    /// not one a request is made for.</returns>
    /// <exception cref="InvalidOperationException">No level yields a text of the request that can be
    /// rendered — a broken installation of the product.</exception>
    public async Task<PhoneNumberRequestDraft?> BuildAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (transaction.ChannelIdentitySnapshot is not { } snapshot
            || PhoneLevelOf(transaction) is not { } level
            || SecondaryActionOf(level) is not { } secondaryAction)
        {
            return null;
        }

        var locale = snapshot.Locale;
        var text = await RenderTextAsync(transaction, snapshot, MessageKindOf(level), cancellationToken);

        var secondaryButtonLabel = secondaryAction is PhoneStepAction.Skip
            ? _promptLocalizer.ResolveOrBaseText(ClaimCompletionPhoneStepTexts.SkipButtonLabel, locale)
            : _promptLocalizer.ResolveOrBaseText(ClaimCompletionPhoneStepTexts.CancelSignInButtonLabel, locale);

        var message = new ChannelMessage
        {
            ChannelUserId = snapshot.ChannelUserId,
            Text = text,
            Kind = ChannelMessageKind.PhoneNumberRequest,
            PhoneNumberRequest = new PhoneNumberRequestContent
            {
                TransactionId = transaction.Id,
                ShareButtonLabel = _promptLocalizer.ResolveOrBaseText(
                    ClaimCompletionPhoneStepTexts.ShareButtonLabel, locale),
                SecondaryAction = secondaryAction,
                SecondaryButtonLabel = secondaryButtonLabel
            }
        };

        var correlation = new PhoneRequestCorrelation
        {
            TenantId = snapshot.TenantId,
            ChannelType = snapshot.ChannelType,
            ChannelUserId = snapshot.ChannelUserId,
            TransactionId = transaction.Id,
            SecondaryAction = secondaryAction,
            SecondaryButtonLabel = secondaryButtonLabel,
            ExpiresAt = transaction.ExpiresAt
        };

        return new PhoneNumberRequestDraft(message, correlation);
    }

    /// <summary>
    /// Resolves the text of the request at the address of the transaction and renders it for the user
    /// of the channel snapshot.
    /// </summary>
    /// <remarks>
    /// The render point states everything the mechanism owns and this point knows: the whole point of
    /// the dimensions, the ownership of the transaction, the locale, the zone moments are shown in and
    /// the server values of the transaction. Which of them reach the text is decided by the contract
    /// and the wording a level declares, never here. The caller values are the one thing left out: they
    /// were accepted against the contract of another message, and showing them outside that message is
    /// admitted for the outcome receipts alone (SPEC-036 TPL-123).
    /// </remarks>
    /// <param name="transaction">Transaction whose number is requested.</param>
    /// <param name="snapshot">Its channel snapshot.</param>
    /// <param name="messageKind">Kind of the message that words the request at the level of the
    /// number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The text of the request, plain and localized.</returns>
    /// <exception cref="InvalidOperationException">No level declares the message, or no step of its
    /// resolved ladder states a plain text.</exception>
    private async Task<string> RenderTextAsync(
        Transaction transaction,
        ChannelIdentitySnapshot snapshot,
        string messageKind,
        CancellationToken cancellationToken)
    {
        var ownership = TransactionResolutionContext.For(transaction);

        var template = await _messageTemplates.FindAsync(
            InChannelMessageAddress.Of(messageKind, transaction, snapshot.ChannelType), ownership, cancellationToken);

        if (template is null)
        {
            // The wording that narrows by nothing ships with the product, so an address answered by no
            // level at all is a broken installation rather than a configuration of the deployment —
            // which is why this throws where a missing translation degrades.
            _logger.LogError(
                "No level declares a message of kind {MessageKind}; the phone number request cannot be "
                + "sent. TransactionId: {TransactionId}",
                messageKind,
                transaction.Id.ToString());

            throw new InvalidOperationException(
                $"No level declares a message of kind '{messageKind}'. The wording "
                + "that narrows by nothing ships with the product, so this is a broken installation of "
                + "it rather than a configuration of the deployment.");
        }

        // A field hidden in the question of the sign-in is not revealed in a later message of the same
        // transaction: the initiator details pass the one display decision, over the same ownership
        // (SPEC-017 ICC-081). Asked only when there are details to decide about.
        var source = TransactionSlotSource.From(transaction).WithoutCallerValues();
        if (source.InitiatorContext is { } initiator)
        {
            var display = await InitiatorContextDisplay.ResolveAsync(
                _configurationResolver, ownership, cancellationToken);
            source = source with { InitiatorContext = display.Apply(initiator) };
        }

        var text = MessageTextRenderer.Render(
            template,
            TransactionSlotValues.Of(source, template.Contract, outcomeMoment: null, MessageSurfaces.InChannel),
            snapshot.Locale,
            transaction.GetUiTimeZone(),
            _promptLocalizer,
            MessageRenderMode.PlainText,
            _logger);

        if (text is not null)
        {
            return text;
        }

        _logger.LogError(
            "No step of the resolved ladder of message kind {MessageKind} states a plain text; the phone "
            + "number request cannot be sent. TransactionId: {TransactionId}",
            template.Kind,
            transaction.Id.ToString());

        throw new InvalidOperationException(
            $"The resolved ladder of message kind '{template.Kind}' states no plain text. A phone number "
            + "request is delivered as plain text and has no other edition to fall back on.");
    }
}
