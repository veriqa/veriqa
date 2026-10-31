// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The default implementation of <see cref="IOutcomeReceiptText"/>: resolve the message at the address
/// the caller stated, apply the display decision of the transaction to its initiator context, then
/// render it.
/// <para>
/// The resolution goes through the one accessor of the contour
/// (<see cref="IMessageTemplateAccessor"/>) rather than through a reading of its own: a second path
/// to the same two settings is exactly the fork the canonical resolver exists to prevent
/// (SPEC-012 CFG-202). What that buys the receipts for free is the runtime half of the cross-key
/// rule — a variant referencing a slot the receipt's (empty) contract does not declare is refused
/// there, wherever it came from, the <c>ui_config</c> level included (SPEC-036 TPL-111).
/// </para>
/// </summary>
internal sealed class OutcomeReceiptTextResolver : IOutcomeReceiptText
{
    /// <summary>
    /// The one point at which the contour obtains a message.
    /// </summary>
    private readonly IMessageTemplateAccessor _messageTemplates;

    /// <summary>
    /// Localizer of the chosen wording into the recipient's language (SPEC-017 §7.2, ICC-050).
    /// </summary>
    private readonly IConfirmationPromptLocalizer _localizer;

    /// <summary>
    /// Configuration resolver: per-application resolution of the display decision of the initiator
    /// context (SPEC-017 ICC-081).
    /// </summary>
    private readonly IConfigurationResolver _configResolver;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<OutcomeReceiptTextResolver> _logger;

    /// <summary>
    /// Creates the resolver.
    /// </summary>
    /// <param name="messageTemplates">Message accessor of the contour.</param>
    /// <param name="localizer">Natural Key localizer.</param>
    /// <param name="configResolver">Configuration resolver.</param>
    /// <param name="logger">Logger.</param>
    public OutcomeReceiptTextResolver(
        IMessageTemplateAccessor messageTemplates,
        IConfirmationPromptLocalizer localizer,
        IConfigurationResolver configResolver,
        ILogger<OutcomeReceiptTextResolver> logger)
    {
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _configResolver = configResolver ?? throw new ArgumentNullException(nameof(configResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<string> RenderAsync(
        OutcomeReceiptAddress address,
        string? recipientLocale,
        string? recipientTimeZone,
        DateTimeOffset? outcomeMoment,
        TransactionSlotSource? transaction,
        ResolutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A receipt answers the user: something was pressed (or the time ran out) and the result
        // must be told. A surface nobody named still gets an answer — the step that names none —
        // and is reported rather than refused, because refusing would leave the screen silent. The
        // channel goes out of the address with the surface it refines, which is what keeps this a
        // degradation rather than a refusal (see OutcomeReceiptAddress.ToDimensions).
        if (string.IsNullOrEmpty(address.Surface))
        {
            _logger.LogWarning(
                "An outcome receipt of kind {MessageKind} is asked for without naming a surface; the "
                + "wording that narrows by neither the surface nor the channel refining it answers "
                + "instead.",
                address.Kind);
        }

        var receipt = await _messageTemplates.FindAsync(
            address.ToDimensions(), context, cancellationToken);

        if (receipt is null)
        {
            // The step that names nothing is shipped with the product, so an address answered by no
            // level at all is a broken build rather than a deployment's configuration — which is why
            // this throws where a missing translation degrades.
            _logger.LogError(
                "No level declares an outcome receipt for message kind {MessageKind}; the receipt "
                + "text of the transaction cannot be shown.",
                address.Kind);

            throw new InvalidOperationException(
                $"No level declares an outcome receipt for message kind '{address.Kind}'. The wording "
                + "that narrows by nothing ships with the product, so this is a broken installation of "
                + "it rather than a configuration of the deployment.");
        }

        // A field hidden in the question is not revealed in the answer: the initiator details reach the
        // receipt through the same display decision the prompt of the transaction is shown by, over the
        // same ownership (SPEC-017 ICC-081). Asked only when there are details to decide about.
        if (transaction?.InitiatorContext is { } initiator)
        {
            var display = await InitiatorContextDisplay.ResolveAsync(_configResolver, context, cancellationToken);
            transaction = transaction with { InitiatorContext = display.Apply(initiator) };
        }

        return OutcomeReceiptText.Render(
            receipt,
            recipientLocale,
            recipientTimeZone,
            outcomeMoment,
            transaction,
            address.Surface,
            _localizer,
            _logger);
    }
}
