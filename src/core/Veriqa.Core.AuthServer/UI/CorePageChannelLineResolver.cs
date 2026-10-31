// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.ObjectModel;

using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// The one point the lines a core page shows for a channel are resolved at: the line once the answer is
/// awaited in the channel, the line once the transition into it began, the line once its QR code was
/// scanned and the hint of the channel once the phone is on its way into it — messages of the template
/// mechanism on the core page surface. The values and the render live
/// here once, for the sign-in window and the entry page of a confirmation (their render contexts) and for
/// the launch page of the hop, so that the pages never address one line differently; the address is the one
/// every message of a transaction is built by (<see cref="TransactionMessageAddress"/>).
/// <para>
/// A line is addressed by its kind, the type and the action type of the transaction, the surface and the
/// channel, and resolved over the ownership of the transaction: the wording of a channel, the Email channel
/// included, is a step of the ladder rather than a branch of the code, and a level that states a wording
/// for a channel reaches every page that shows the line. The values are the name of the channel and of its
/// chat, plus the server values of the transaction.
/// </para>
/// <para>
/// A line that cannot be resolved is left out with a warning, and the page goes on without it: a text of
/// a status line is not a reason to fail the page. The hint of a channel is the one exception to the
/// warning: its kind has no ladder at its base, so a channel no level words a hint for simply has none.
/// A host without the channel contour has no message to resolve, and its pages show none of these lines.
/// </para>
/// </summary>
internal sealed class CorePageChannelLineResolver
{
    /// <summary>
    /// The slot the script of the sign-in window puts the name of the channel of an event into, in a status
    /// line that names a channel outside the page.
    /// </summary>
    private const string ScriptChannelSlot = "{0}";

    /// <summary>
    /// Locale-file localizer the texts of the lines are translated by.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _localizer;

    /// <summary>
    /// Canonical layer resolver — the reader of the display decision over the initiator context.
    /// </summary>
    private readonly IConfigurationResolver _configurationResolver;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<CorePageChannelLineResolver> _logger;

    /// <summary>
    /// Accessor of the messages; null — the host runs without the channel contour.
    /// </summary>
    private readonly IMessageTemplateAccessor? _messageTemplates;

    /// <summary>
    /// Creates the resolver.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="configurationResolver">Canonical layer resolver.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="messageTemplates">
    /// Accessor of the messages; a host without the channel contour registers none, and its pages go without
    /// the lines.
    /// </param>
    public CorePageChannelLineResolver(
        IConfirmationPromptLocalizer localizer,
        IConfigurationResolver configurationResolver,
        ILogger<CorePageChannelLineResolver> logger,
        IMessageTemplateAccessor? messageTemplates = null)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(configurationResolver);
        ArgumentNullException.ThrowIfNull(logger);

        _localizer = localizer;
        _configurationResolver = configurationResolver;
        _logger = logger;
        _messageTemplates = messageTemplates;
    }

    /// <summary>
    /// The lines of the sign-in window, or of the entry page of a confirmation, for every channel of the
    /// page, the hint of each, and the line of a begun transition into a channel the page does not show. They are worded in the
    /// content language the built-in renderer speaks the page in, and each channel is named by the rule the
    /// tabs of the page are named with.
    /// </summary>
    /// <param name="transaction">Transaction of the page.</param>
    /// <param name="channels">Channels of the page.</param>
    /// <param name="texts">Text overrides of the page — the name of a channel takes them.</param>
    /// <param name="language">Language of the page as requested.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lines; empty — the host has no messages to resolve.</returns>
    public async Task<CorePageChannelLineSet> ForWindowAsync(
        Transaction transaction,
        IReadOnlyList<ChannelDisplayInfo> channels,
        CorePageTexts texts,
        string language,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(texts);

        if (_messageTemplates is null)
        {
            return CorePageChannelLineSet.None;
        }

        var contentLanguage = AuthPageLocalization.ResolveContentLanguage(_localizer, AuthPageStrings.Title, language);
        var request = await RequestOfAsync(transaction, contentLanguage, cancellationToken);

        // An indexer rather than Add: a channel type repeated in the set names the same channel twice.
        var lines = new Dictionary<string, AuthPageChannelLines>(StringComparer.Ordinal);
        foreach (var channel in channels)
        {
            var naming = new ChannelNaming(
                ChannelNames.LocalizeChannelName(
                    _localizer, texts, channel.ChannelType, channel.DisplayName, contentLanguage),
                channel.BotName);

            lines[channel.ChannelType] = new AuthPageChannelLines
            {
                Answer = await LineAsync(
                        request, MessageKinds.ChannelAnswerLine, channel.ChannelType, naming, optional: false, cancellationToken)
                    ?? string.Empty,
                Launched = await LineAsync(
                        request, MessageKinds.ChannelLaunchedLine, channel.ChannelType, naming, optional: false, cancellationToken)
                    ?? string.Empty,
                Scanned = await LineAsync(
                        request, MessageKinds.QrScannedLine, channel.ChannelType, naming, optional: false, cancellationToken)
                    ?? string.Empty,
                HopHint = await HintAsync(request, channel.ChannelType, naming, cancellationToken)
            };
        }

        // The transition into a channel outside the page: the line of no channel, with the slot of the script
        // in place of the name, which the script fills by the channel of the event.
        var fallbackLaunched = await LineAsync(
            request,
            MessageKinds.ChannelLaunchedLine,
            channelType: null,
            new ChannelNaming(ScriptChannelSlot, BotName: null),
            optional: false,
            cancellationToken);

        return new CorePageChannelLineSet(lines.AsReadOnly(), fallbackLaunched);
    }

    /// <summary>
    /// The lines of the channel of the launch page of the hop: the line once the transition into it began —
    /// the line the sign-in window shows once the answer is awaited in that channel — and the hint the
    /// sign-in window shows once the phone is on its way into it. Both are resolved over one request of the
    /// page, so the display decision over the initiator context is read once.
    /// </summary>
    /// <param name="transaction">Transaction of the page.</param>
    /// <param name="channelType">Channel of the page.</param>
    /// <param name="channelName">Name of the channel, by the rule the tabs of the sign-in window are named with.</param>
    /// <param name="botName">Name of the chat; null or blank — none.</param>
    /// <param name="contentLanguage">Content language of the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lines, not encoded; an empty one — none, or it could not be resolved.</returns>
    public async Task<HopLaunchChannelLines> ForLaunchPageAsync(
        Transaction transaction,
        string channelType,
        string channelName,
        string? botName,
        string contentLanguage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (_messageTemplates is null)
        {
            return HopLaunchChannelLines.None;
        }

        var request = await RequestOfAsync(transaction, contentLanguage, cancellationToken);
        var naming = new ChannelNaming(channelName, botName);

        var answer = await LineAsync(
                request, MessageKinds.ChannelAnswerLine, channelType, naming, optional: false, cancellationToken)
            ?? string.Empty;
        var hint = await HintAsync(request, channelType, naming, cancellationToken);

        return new HopLaunchChannelLines(answer, hint);
    }

    /// <summary>
    /// What every line of one page is resolved over: the ownership of the transaction and what the core knows
    /// about it. A field of the initiator context hidden in the question of the sign-in is not revealed by a
    /// line of the same transaction either (SPEC-017 ICC-081): the details pass the one display decision.
    /// The caller values are not a source of these lines.
    /// </summary>
    /// <param name="transaction">Transaction of the page.</param>
    /// <param name="contentLanguage">Content language of the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The request of the lines.</returns>
    private async Task<LineRequest> RequestOfAsync(
        Transaction transaction,
        string contentLanguage,
        CancellationToken cancellationToken)
    {
        var ownership = TransactionResolutionContext.For(transaction);
        var source = TransactionSlotSource.From(transaction).WithoutCallerValues();

        if (source.InitiatorContext is { } initiator)
        {
            try
            {
                var display = await InitiatorContextDisplay.ResolveAsync(
                    _configurationResolver, ownership, cancellationToken);
                source = source with { InitiatorContext = display.Apply(initiator) };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Without the decision nothing of the context may be shown; the lines go on without it.
                _logger.LogWarning(
                    exception,
                    "The display decision over the initiator context could not be resolved; the channel lines "
                    + "of the page go without it. TransactionId: {TransactionId}",
                    transaction.Id.ToString());
                source = source with { InitiatorContext = null };
            }
        }

        return new LineRequest(transaction, ownership, source, contentLanguage);
    }

    /// <summary>
    /// Resolves and renders the hint of one channel. The kind has no ladder at its base, so a channel no
    /// level words a hint for has none, without a warning; any other refusal is the one of a line.
    /// </summary>
    /// <param name="request">What the lines of the page are resolved over.</param>
    /// <param name="channelType">Channel the hint speaks of.</param>
    /// <param name="naming">Names the hint takes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hint, not encoded; empty — none.</returns>
    private async Task<string> HintAsync(
        LineRequest request,
        string channelType,
        ChannelNaming naming,
        CancellationToken cancellationToken) =>
        await LineAsync(request, MessageKinds.HopChannelHint, channelType, naming, optional: true, cancellationToken)
            ?? string.Empty;

    /// <summary>
    /// Resolves and renders one line. Nothing resolved, no plain text in the ladder or a failure of the
    /// resolution leave the line out with a warning; a cancelled request is not swallowed.
    /// </summary>
    /// <param name="request">What the lines of the page are resolved over.</param>
    /// <param name="kind">Kind of the line.</param>
    /// <param name="channelType">Channel the line speaks of; null — no channel in particular.</param>
    /// <param name="naming">Names the line takes.</param>
    /// <param name="optional">
    /// Whether the kind is shown only where some level words it: no level stating a ladder for the address
    /// then leaves the line out without a warning (<see cref="IMessageTemplateAccessor.FindOptionalAsync"/>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The line, not encoded; null — it could not be resolved.</returns>
    private async Task<string?> LineAsync(
        LineRequest request,
        string kind,
        string? channelType,
        ChannelNaming naming,
        bool optional,
        CancellationToken cancellationToken)
    {
        try
        {
            var address = TransactionMessageAddress.Of(kind, MessageSurfaces.CorePage, request.Transaction, channelType);
            var message = optional
                ? await _messageTemplates!.FindOptionalAsync(address, request.Ownership, cancellationToken)
                : await _messageTemplates!.FindAsync(address, request.Ownership, cancellationToken);

            if (message is null && optional)
            {
                // The accessor has reported every refusal but the missing ladder, which is no defect here.
                return null;
            }

            if (message is null)
            {
                _logger.LogWarning(
                    "No level declares a message of kind {MessageKind}; the page shows no such line for channel "
                    + "{ChannelType}. TransactionId: {TransactionId}",
                    kind,
                    channelType,
                    request.Transaction.Id.ToString());

                return null;
            }

            var values = TransactionSlotValues.Of(
                request.Source, message.Contract, outcomeMoment: null, MessageSurfaces.CorePage);
            values[SlotNames.ChannelName] = naming.ChannelName;

            // A chat without a name gives its slot no value: the ladder then takes the step that does not
            // name the chat, rather than one with a gap in its place.
            if (!string.IsNullOrWhiteSpace(naming.BotName))
            {
                values[SlotNames.BotName] = naming.BotName;
            }

            var text = MessageTextRenderer.Render(
                message,
                values,
                request.ContentLanguage,
                request.Transaction.GetUiTimeZone(),
                _localizer,
                MessageRenderMode.PlainText,
                _logger);

            if (text is null)
            {
                _logger.LogWarning(
                    "No step of the message of kind {MessageKind} states a plain text; the page shows no such line "
                    + "for channel {ChannelType}. TransactionId: {TransactionId}",
                    kind,
                    channelType,
                    request.Transaction.Id.ToString());
            }

            return text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "The message of kind {MessageKind} could not be resolved; the page shows no such line for channel "
                + "{ChannelType}. TransactionId: {TransactionId}",
                kind,
                channelType,
                request.Transaction.Id.ToString());

            return null;
        }
    }

    /// <summary>
    /// What the lines of one page are resolved over.
    /// </summary>
    /// <param name="Transaction">Transaction of the page.</param>
    /// <param name="Ownership">Ownership context of the transaction.</param>
    /// <param name="Source">What the core knows about the transaction, without the caller values.</param>
    /// <param name="ContentLanguage">Content language of the page.</param>
    private sealed record LineRequest(
        Transaction Transaction,
        ResolutionContext Ownership,
        TransactionSlotSource Source,
        string ContentLanguage);

    /// <summary>
    /// The names a line of a channel takes.
    /// </summary>
    /// <param name="ChannelName">Name of the channel.</param>
    /// <param name="BotName">Name of its chat; null or blank — none.</param>
    private readonly record struct ChannelNaming(string ChannelName, string? BotName);
}

/// <summary>
/// The lines of the channels of one sign-in window or entry page of a confirmation.
/// </summary>
/// <param name="Lines">The lines by channel type of the page.</param>
/// <param name="FallbackLaunched">
/// The line of a begun transition into a channel the page does not show, with <c>{0}</c> in place of the
/// name the script of the page fills; null — none.
/// </param>
internal sealed record CorePageChannelLineSet(
    IReadOnlyDictionary<string, AuthPageChannelLines> Lines,
    string? FallbackLaunched)
{
    /// <summary>
    /// No line at all — the host has no messages to resolve.
    /// </summary>
    public static CorePageChannelLineSet None { get; } =
        new(ReadOnlyDictionary<string, AuthPageChannelLines>.Empty, FallbackLaunched: null);
}

/// <summary>
/// The lines of the channel of one launch page of the hop.
/// </summary>
/// <param name="Answer">The line of a begun transition into the channel, not encoded; empty — none.</param>
/// <param name="HopHint">The hint of the channel, not encoded; empty — none.</param>
internal sealed record HopLaunchChannelLines(string Answer, string HopHint)
{
    /// <summary>
    /// No line at all — the host has no messages to resolve.
    /// </summary>
    public static HopLaunchChannelLines None { get; } = new(string.Empty, string.Empty);
}
