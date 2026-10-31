// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Implementation of the confirmation context factory (SPEC-017 §7.1, ICC-041).
/// </summary>
internal sealed class ConfirmationPromptContextFactory : IConfirmationPromptContextFactory
{
    /// <summary>
    /// Initiator context check settings.
    /// </summary>
    private readonly IOptions<InitiatorContextOptions> _initiatorContextOptions;

    /// <summary>
    /// Anomaly heuristic (no-op without a history source, ICC-072).
    /// </summary>
    private readonly IInitiatorAnomalyDetector _anomalyDetector;

    /// <summary>
    /// Configuration resolver: per-application resolution of DisplayFields/Enabled, and the configured name
    /// of the chat of the channel the question is asked in.
    /// </summary>
    private readonly IConfigurationResolver _configResolver;

    /// <summary>
    /// The single point at which the subject of a confirmation is obtained (SPEC-036 TPL-001):
    /// the message whose kind IS the action type of the transaction.
    /// </summary>
    private readonly IMessageTemplateAccessor _messageTemplates;

    /// <summary>
    /// Localizer of the Natural Keys a template variant is written in (SPEC-017 §7.2, ICC-050).
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// Logger the render passes its degradation warnings to — without it the two WARNINGs of the
    /// render protections (a variant fallen back onto, a translation whose slot set changed) are lost.
    /// </summary>
    private readonly ILogger<ConfirmationPromptContextFactory> _logger;

    /// <summary>
    /// Registered channel adapters — the label a third-party channel declared for itself names it in the
    /// text of the question, and the adapter names its chat there, as both do on the pages.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Creates the confirmation context factory.
    /// </summary>
    /// <param name="initiatorContextOptions">Initiator context check settings.</param>
    /// <param name="anomalyDetector">Anomaly heuristic.</param>
    /// <param name="configResolver">Configuration resolver.</param>
    /// <param name="messageTemplates">Accessor of the messages of the template mechanism.</param>
    /// <param name="promptLocalizer">Localizer of the Natural Keys of a template.</param>
    /// <param name="logger">Logger of the render degradations.</param>
    /// <param name="adapters">Registered channel adapters.</param>
    public ConfirmationPromptContextFactory(
        IOptions<InitiatorContextOptions> initiatorContextOptions,
        IInitiatorAnomalyDetector anomalyDetector,
        IConfigurationResolver configResolver,
        IMessageTemplateAccessor messageTemplates,
        IConfirmationPromptLocalizer promptLocalizer,
        ILogger<ConfirmationPromptContextFactory> logger,
        IEnumerable<IChannelAdapter> adapters)
    {
        _initiatorContextOptions = initiatorContextOptions;
        _anomalyDetector = anomalyDetector;
        _configResolver = configResolver;
        _messageTemplates = messageTemplates;
        _promptLocalizer = promptLocalizer;
        _logger = logger;
        _adapters = adapters;
    }

    /// <inheritdoc />
    public async Task<ConfirmationPromptContext> CreateAsync(
        Transaction transaction,
        ConfirmationPromptRecipient recipient,
        string? recipientLocale,
        string surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(surface);

        var context = await CreateVariantAsync(transaction, recipient, recipientLocale, surface, cancellationToken);

        // The brand is stated on every variant alike: it is not an initiator detail, so neither the type
        // of the transaction nor a suppression of the details takes it away from a text.
        return context with { BrandName = transaction.GetBrandName() };
    }

    /// <summary>
    /// Builds the variant of the context the transaction calls for — its subject, its initiator details
    /// or "no details" with the reason.
    /// </summary>
    /// <param name="transaction">Transaction the question is asked about.</param>
    /// <param name="recipient">The one the question is asked of.</param>
    /// <param name="recipientLocale">Locale of the current channel event (null — none stated).</param>
    /// <param name="surface">Surface the question is shown on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The context, without the brand name.</returns>
    private async Task<ConfirmationPromptContext> CreateVariantAsync(
        Transaction transaction,
        ConfirmationPromptRecipient recipient,
        string? recipientLocale,
        string surface,
        CancellationToken cancellationToken)
    {
        // The method builds the confirmation context: filters fields by DisplayFields
        // and applies the anomaly heuristic (only when the configuration is enabled and history is available)

        // Effective recipient locale — the one and only expression of the chain "locale of the current
        // channel event → locale already stored on the transaction" (SPEC-017 §7.2). It is computed
        // once here because the context is the single carrier of the locale downstream.
        var effectiveLocale = recipientLocale ?? transaction.ChannelIdentitySnapshot?.Locale;

        // The zone has ONE source and no chain: the transaction, where the relying party's own value
        // or the deployment's default was written at creation. A channel event states none — a
        // messenger reports a language, never a zone — so there is nothing here to fall back from.
        var recipientTimeZone = transaction.GetUiTimeZone();

        // The ownership of the transaction is assembled ONCE, before the branches: EVERY variant of
        // the context carries it, the suppressed ones included. Suppression is about the initiator
        // DETAILS (ICC-042) and says nothing about the address the adapter later resolves the prompt
        // wording at — a suppressed context is exactly the case where the adapter falls back on the
        // declared text of the message, and that text has to be looked up over the levels of this
        // transaction rather than over the core level alone (SPEC-036 TPL-116).
        var resolutionContext = TransactionResolutionContext.For(transaction);

        // A transaction whose subject IS the confirmation is worded from that subject and never from
        // the initiator context: the whole ICC branch below — the snapshot, its settings, its display
        // fields and the anomaly heuristic — is skipped, because the server-to-server entry collects
        // no initiator context at all (SPEC-039 N32) and a sign-in wording must never stand in for a
        // subject (E28).
        if (string.Equals(transaction.Type, TransactionTypes.Confirmation, StringComparison.Ordinal))
        {
            return await CreateSubjectContextAsync(
                transaction, recipient, surface, resolutionContext, effectiveLocale, recipientTimeZone, cancellationToken);
        }

        var snapshot = transaction.InitiatorContextSnapshot;
        if (snapshot is null)
        {
            return Suppressed(
                ContextSuppressionReason.InitiatorSnapshotUnavailable,
                resolutionContext,
                effectiveLocale,
                recipientTimeZone);
        }

        var options = _initiatorContextOptions.Value;

        // DisplayFields/Enabled are resolved per-application over the same context (ICC-081) by the one
        // display decision the receipt of the outcome is shown by as well: an application may narrow the
        // field set and/or disable display; self-hosted = global 1:1.

        // Enabled controls specifically the DISPLAY of the context in the confirmation (decision #8):
        // when false for the application, the context is not shown (the snapshot is already collected
        // separately) — the prompt is suppressed and the anomaly heuristic is not run.
        var display = await InitiatorContextDisplay.ResolveAsync(_configResolver, resolutionContext, cancellationToken);
        if (!display.Enabled)
        {
            return Suppressed(
                ContextSuppressionReason.DisplayDisabledByConfiguration,
                resolutionContext,
                effectiveLocale,
                recipientTimeZone);
        }

        // Anomaly heuristic: only when the configuration is enabled and a history source is available.
        // It compares the snapshot AS COLLECTED — what may be shown does not narrow what is compared.
        var anomaly = InitiatorAnomalyResult.None;
        if (options.AnomalyDetection.Enabled && _anomalyDetector.HasHistorySource)
        {
            anomaly = await _anomalyDetector.EvaluateAsync(
                recipient.ChannelType, recipient.ChannelUserId, snapshot, cancellationToken);
        }

        // Only fields allowed by DisplayFields are passed to the adapter (ICC-015, ICC-040): the display
        // decision has already cleared the hidden ones. A member no field of the table owns passes as
        // collected; in the shipped table that is DeviceType, a derived non-personal field of the
        // SPEC-017 §7.1 contract.
        var shown = display.Apply(snapshot);
        var botName = await BotNameOfAsync(recipient, resolutionContext, cancellationToken);
        return new DetailedConfirmationPromptContext
        {
            Ownership = resolutionContext,
            ClientApplicationName = shown.ClientApplicationName,
            Browser = shown.Browser,
            OsPlatform = shown.OsPlatform,
            DeviceType = shown.DeviceType,
            GeoCountry = shown.GeoCountry,
            GeoCity = shown.GeoCity,
            InitiatedAt = shown.InitiatedAt,
            RequestedAt = transaction.CreatedAt,
            AnomalyFlag = anomaly.IsAnomalous,
            AnomalyReasonKey = anomaly.ReasonKey,
            // The account asked, the channel it is asked in and the chat of that channel — the masked
            // identity slots, the channel name and the bot name of the text (SPEC-036 TPL-136). A sign-in
            // states them on every surface.
            ChannelIdentity = recipient.Identity,
            ChannelName = ChannelNameOf(recipient, effectiveLocale),
            BotName = botName,
            // Recipient locale (SPEC-017 §7.2) and the zone its moments are shown in: resolved once
            // above, for every context variant alike
            RecipientLocale = effectiveLocale,
            RecipientTimeZone = recipientTimeZone
        };
    }

    /// <summary>
    /// Builds the question of a confirmation transaction from its snapshot: the declared message of the
    /// action type, worded with the values the calling party supplied plus the server slot of the
    /// application attribution (SPEC-039 R7, L21).
    /// </summary>
    /// <remarks>
    /// Every way of NOT getting a usable subject ends the same way — the prompt is suppressed with one
    /// reason and the caller stops the transaction (E28). They are not told apart on purpose: which
    /// level failed to declare a message, and whether a configuration source answered at all, is the
    /// mechanism's own diagnostic and is logged by it (SPEC-036 TPL-120).
    /// </remarks>
    /// <param name="transaction">Transaction being confirmed.</param>
    /// <param name="recipient">The one the question is asked of.</param>
    /// <param name="surface">Surface the subject is shown on.</param>
    /// <param name="ownership">Ownership context of the transaction, carried by every variant.</param>
    /// <param name="recipientLocale">Effective recipient locale (null — base language).</param>
    /// <param name="recipientTimeZone">Time zone the moments of the subject are shown in (null — UTC
    /// with its marker).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subject context, or a suppressed context when there is no subject to show.</returns>
    private async Task<ConfirmationPromptContext> CreateSubjectContextAsync(
        Transaction transaction,
        ConfirmationPromptRecipient recipient,
        string surface,
        ResolutionContext ownership,
        string? recipientLocale,
        string? recipientTimeZone,
        CancellationToken cancellationToken)
    {
        var actionType = transaction.ConfirmationSnapshot?.ActionType;

        // Asked BEFORE the lookup: the accessor takes a kind and not an outcome of one, so a blank
        // action type is this branch's answer to give, not an exception to raise.
        if (string.IsNullOrEmpty(actionType))
        {
            return Unavailable(transaction, actionType, ownership, recipientLocale, recipientTimeZone);
        }

        // Neither the surface nor the channel is named: the wording of a subject does not depend on
        // where it is shown, and an axis that is not named is not asked about at all. The ownership
        // context is the transaction's — the application level takes part, because it is the
        // application that picks the contract of the message (SPEC-039 R3/C14, SPEC-036 TPL-112).
        var message = await _messageTemplates.FindAsync(
            actionType,
            surface: null,
            channel: null,
            SubjectOwnershipOf(transaction),
            cancellationToken);

        // What is asked is the LEVEL that declared the CONTRACT, exactly as the entry asks it: a kind
        // served by the core level alone is one the product ships for its own purposes, and wording a
        // subject out of it would put a sign-in text in the place of the subject (SPEC-036 TPL-113).
        // Reachable without any fault of the calling party — a declaration removed after the
        // transaction was created, on an action type whose name the core also ships.
        if (message is null || message.ContractLevel is null or ConfigLevel.Core)
        {
            return Unavailable(transaction, actionType, ownership, recipientLocale, recipientTimeZone);
        }

        // The subject is worded outside the initiator-context branch (SPEC-039 N32): no initiator
        // context takes part in it, whatever the transaction carries, since no display decision was made
        // for it here. The account is the one the question is asked of, exactly as the caller names it —
        // never the snapshot of the transaction in its place: a recipient that knows no account (the mail
        // of the Email channel) names none, whatever channel the transaction is bound to. Whether the
        // surface may name it is decided by the assembly of the values.
        var source = TransactionSlotSource.From(transaction) with
        {
            InitiatorContext = null,
            ChannelIdentity = recipient.Identity
        };
        var values = TransactionSlotValues.Of(source, message.Contract, outcomeMoment: null, surface);

        TransactionSlotValues.AddChannelName(values, ChannelNameOf(recipient, recipientLocale));

        // The adapter is asked for the name only when the contract of the subject declares the slot.
        if (message.Contract.FindSlot(SlotNames.BotName) is not null)
        {
            TransactionSlotValues.AddBotName(values, await BotNameOfAsync(recipient, ownership, cancellationToken));
        }

        var text = MessageTextRenderer.Render(
            message,
            values,
            recipientLocale,
            recipientTimeZone,
            _promptLocalizer,
            MessageRenderMode.PlainText,
            _logger);

        // No step of the ladder states the plain-text edition a messenger delivers. There is no wording
        // of the subject to show, and the sign-in fallback of the channel is not one: same fail-safe.
        if (string.IsNullOrEmpty(text))
        {
            return Unavailable(transaction, actionType, ownership, recipientLocale, recipientTimeZone);
        }

        return new SubjectConfirmationPromptContext
        {
            Ownership = ownership,
            PromptText = text,
            RecipientLocale = recipientLocale,
            RecipientTimeZone = recipientTimeZone
        };
    }

    /// <summary>
    /// The name of the channel the question is asked in, in the language of the text, by the one rule a
    /// channel is named by on the pages (<see cref="ChannelNames.NameChannel"/>). The text overrides of a
    /// page do not take part: the channel has none, and the rule is one for both surfaces.
    /// </summary>
    /// <param name="recipient">The one the question is asked of.</param>
    /// <param name="recipientLocale">Effective recipient locale (null — base language).</param>
    /// <returns>The name; null when the channel is not known.</returns>
    private string? ChannelNameOf(ConfirmationPromptRecipient recipient, string? recipientLocale) =>
        string.IsNullOrEmpty(recipient.ChannelType)
            ? null
            : ChannelNames.NameChannel(
                recipient.ChannelType,
                _adapters,
                _promptLocalizer,
                recipientLocale ?? ChannelLocalizationOptions.DefaultBaseLocale,
                _logger);

    /// <summary>
    /// The name of the chat of the channel the question is asked in, by the one rule the pages name it by
    /// (<see cref="ChannelBotNames"/>): the name a level states for the channel, otherwise the one its
    /// adapter reports. The adapter is asked under the tenant of the ownership context, which the rule
    /// opens itself — the pages ask for a question outside any tenant scope.
    /// </summary>
    /// <param name="recipient">The one the question is asked of.</param>
    /// <param name="ownership">Ownership context of the transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The name; null when the channel is not known or states none.</returns>
    private async Task<string?> BotNameOfAsync(
        ConfirmationPromptRecipient recipient,
        ResolutionContext ownership,
        CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(recipient.ChannelType)
            ? null
            : await ChannelBotNames.ReadAsync(
                recipient.ChannelType,
                _adapters,
                _configResolver,
                ownership,
                _logger,
                cancellationToken);

    /// <summary>
    /// The ownership context a SUBJECT is worded over: the tenant and the application, and no
    /// <c>ui_config</c>.
    /// </summary>
    /// <remarks>
    /// The same context the entry validated the request over. The <c>ui_config</c> record is a
    /// presentational artifact chosen by a request parameter, and a level chosen that way may not take
    /// part in deciding what the question SAYS: putting it in would let it widen the set of action
    /// types and the slot allowlist the entry admitted the request against, and put a tenant branding
    /// text in the place of the subject (SPEC-039 R3/C14/C18, SPEC-036 TPL-108).
    /// </remarks>
    /// <param name="transaction">Transaction being confirmed.</param>
    /// <returns>The resolution context of the subject.</returns>
    private static ResolutionContext SubjectOwnershipOf(Transaction transaction)
    {
        // The ui_config level is deliberately not stated here — see the remarks above. A level stated as
        // whitespace is a level nobody owns, and the context factory normalizes it away, so a blank
        // attribution never creates a phantom level.
        return ResolutionContext.Of(
            transaction.GetTenantId(),
            transaction.GetApplicationId(),
            uiConfigSelector: null);
    }

    /// <summary>
    /// The one answer to every way of not having a subject to show, plus the record of it. The record
    /// names the transaction, its action type and the fact — never a value the calling party supplied
    /// (SPEC-039 N31).
    /// </summary>
    /// <param name="transaction">Transaction being confirmed.</param>
    /// <param name="actionType">Action type of the transaction (null/empty — the snapshot states none).</param>
    /// <param name="ownership">Ownership context of the transaction.</param>
    /// <param name="recipientLocale">Effective recipient locale.</param>
    /// <param name="recipientTimeZone">Time zone of the recipient (null — UTC with its marker).</param>
    /// <returns>The suppressed context.</returns>
    private ConfirmationPromptContext Unavailable(
        Transaction transaction,
        string? actionType,
        ResolutionContext ownership,
        string? recipientLocale,
        string? recipientTimeZone)
    {
        _logger.LogWarning(
            "No message declares the subject of the confirmation — the question is not asked. TransactionId: {TransactionId}, ActionType: {ActionType}",
            transaction.Id.ToString(),
            actionType);

        return Suppressed(
            ContextSuppressionReason.ConfirmationTemplateUnavailable,
            ownership,
            recipientLocale,
            recipientTimeZone);
    }

    /// <summary>
    /// The suppressed context of a transaction whose ownership IS known. It is built here rather than
    /// through <see cref="ConfirmationPromptContext.Suppressed"/>: that shorthand serves a caller with
    /// no transaction and states <see cref="ResolutionContext.Core"/>, which would send the adapter's
    /// fallback wording to the core level and lose the levels this transaction does state.
    /// </summary>
    /// <param name="reason">Why the initiator details are absent.</param>
    /// <param name="ownership">Ownership context of the transaction.</param>
    /// <param name="recipientLocale">Effective recipient locale (null — base language).</param>
    /// <param name="recipientTimeZone">Time zone of the recipient (null — UTC with its marker).</param>
    /// <returns>The suppressed context.</returns>
    private static SuppressedConfirmationPromptContext Suppressed(
        ContextSuppressionReason reason,
        ResolutionContext ownership,
        string? recipientLocale,
        string? recipientTimeZone) => new()
        {
            Reason = reason,
            Ownership = ownership,
            RecipientLocale = recipientLocale,
            RecipientTimeZone = recipientTimeZone
        };
}
