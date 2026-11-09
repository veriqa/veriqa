// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenIddict.Client;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.WebProviders.Configuration;
using Veriqa.Core.ChannelAdapter.WebProviders.Constants;
using Veriqa.Core.ChannelAdapter.WebProviders.State;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.ChannelAdapter.WebProviders;

/// <summary>
/// The adapter of the web provider channels: ONE class registered once per enabled channel, each
/// instance serving its own registration of the OpenIddict client under its own channel type. The user
/// signs in at the provider, and the page of the channel stands in for the chat of a messenger: what
/// the core would send to a chat — the question, its messages, the outcome — is kept in the state store
/// of the channel and shown by the page.
/// <para>
/// The channel has no webhook: its events come from its own routes, which hand them to the core the way
/// a webhook event is handed. Nothing here depends on the type of the transaction: a sign-in and a
/// confirmation go through the same page, and only the texts the core renders tell them apart.
/// </para>
/// </summary>
internal sealed class WebProviderChannelAdapter : IChannelAdapter, IChannelDisplayMetadata, IChannelOwnPage
{
    /// <summary>
    /// Facts every web provider channel shares. The question and its answer live on the page; messages
    /// are texts, links to the claim form and buttons, none of which the page edits later; the outcome is
    /// shown, and the page in the browser renders the emoji its receipt carries; the provider states the
    /// language of the account. The phone number is the fact of each channel's own
    /// (<see cref="Capabilities"/>): none here.
    /// </summary>
    private static readonly ChannelCapabilities WebProviderCapabilities = new()
    {
        SupportsInChannelConfirmation = true,
        SupportedMessageKinds = new[]
        {
            ChannelMessageKind.PlainText,
            ChannelMessageKind.ClaimFormLink,
            ChannelMessageKind.ActionButtons
        }.ToFrozenSet(),
        SupportsMessageUpdate = false,
        DeliversOutcomeNotice = true,
        RendersEmoji = true,
        ProvidesRecipientLocale = true,
        PhoneNumber = PhoneNumberCapability.None,
        SupportedCompletionBehaviorActions = new[]
        {
            CompletionBehaviorAction.None,
            CompletionBehaviorAction.WelcomeMessage,
            CompletionBehaviorAction.Menu,
            CompletionBehaviorAction.Redirect
        }.ToFrozenSet(),
        DefaultCompletionBehaviorAction = CompletionBehaviorAction.None
    };

    /// <summary>
    /// The channel this instance serves.
    /// </summary>
    private readonly WebProviderRegistration _registration;

    /// <summary>
    /// Facts of the channel this instance serves.
    /// </summary>
    private readonly ChannelCapabilities _capabilities;

    /// <summary>
    /// The accessor this adapter takes the templates of its channel messages from.
    /// </summary>
    private readonly IChannelMessageTemplates _messageTemplates;

    /// <summary>
    /// Localizer of the question into the language of the recipient.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// State of the pages of the channel.
    /// </summary>
    private readonly IWebProviderStateStore _stateStore;

    /// <summary>
    /// Store of the transactions — the end of a transaction bounds the life of its state.
    /// </summary>
    private readonly ITransactionStore _transactionStore;

    /// <summary>
    /// Configuration of the transaction engine — the retention of a completed transaction.
    /// </summary>
    private readonly IOptionsMonitor<TransactionEngineOptions> _engineOptions;

    /// <summary>
    /// Options of the OpenIddict client — the display name of the provider of the registration.
    /// </summary>
    private readonly IOptionsMonitor<OpenIddictClientOptions> _clientOptions;

    /// <summary>
    /// Factory of the scopes the health probe resolves the OpenIddict client service in.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Clock of the host.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<WebProviderChannelAdapter> _logger;

    /// <summary>
    /// Creates the adapter of one channel.
    /// </summary>
    /// <param name="registration">The channel this instance serves.</param>
    /// <param name="messageTemplates">Accessor of the channel message templates of this adapter.</param>
    /// <param name="promptLocalizer">Localizer of the channel texts.</param>
    /// <param name="stateStore">State of the pages of the channel.</param>
    /// <param name="transactionStore">Store of the transactions.</param>
    /// <param name="engineOptions">Configuration of the transaction engine.</param>
    /// <param name="clientOptions">Options of the OpenIddict client.</param>
    /// <param name="scopeFactory">Factory of service scopes.</param>
    /// <param name="timeProvider">Clock of the host.</param>
    /// <param name="logger">Logger.</param>
    public WebProviderChannelAdapter(
        WebProviderRegistration registration,
        IChannelMessageTemplates messageTemplates,
        IConfirmationPromptLocalizer promptLocalizer,
        IWebProviderStateStore stateStore,
        ITransactionStore transactionStore,
        IOptionsMonitor<TransactionEngineOptions> engineOptions,
        IOptionsMonitor<OpenIddictClientOptions> clientOptions,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<WebProviderChannelAdapter> logger)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _capabilities = WebProviderCapabilities with
        {
            PhoneNumber = registration.PhoneNumber?.Capability ?? PhoneNumberCapability.None
        };
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _transactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
        _engineOptions = engineOptions ?? throw new ArgumentNullException(nameof(engineOptions));
        _clientOptions = clientOptions ?? throw new ArgumentNullException(nameof(clientOptions));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Channel type — the name of the section of the channel.
    /// </summary>
    public string ChannelType => _registration.ChannelType;

    /// <summary>
    /// Facts of the channel: those every web provider channel shares, and the phone number its
    /// registration declares — <see cref="PhoneNumberCapability.None"/> without one.
    /// </summary>
    public ChannelCapabilities Capabilities => _capabilities;

    /// <summary>
    /// Name of the channel on the sign-in button and in the head of its page: the one the section
    /// states, else the display name OpenIddict gives the provider, else the name of the provider.
    /// </summary>
    public string DisplayName =>
        _registration.DisplayName
        ?? _clientOptions.CurrentValue.Registrations
            .FirstOrDefault(registration => string.Equals(registration.RegistrationId, ChannelType, StringComparison.Ordinal))
            ?.ProviderDisplayName
        ?? _registration.Provider.Name;

    /// <summary>
    /// The page of the channel — the chat of the account, which a page the core opens from it returns to.
    /// </summary>
    /// <returns>The rooted path of the page.</returns>
    public string PagePath() => WebProviderRoutes.RoutePath(ChannelType, WebProviderRoutes.PageSegment);

    /// <summary>
    /// Inner markup of the channel glyph stated by the section; <c>null</c> — no icon.
    /// </summary>
    public string? IconSvgPath => _registration.IconSvgPath;

    /// <summary>
    /// Refuses every event: the channel has no webhook, its events come from its own routes.
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A refusal <c>unsupported_event_type</c>.</returns>
    public Task<Result<ChannelInboundResult>> ProcessInboundEventAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<ChannelInboundResult>.Failure(
            ChannelAdapterErrorCodes.UnsupportedEventType,
            WebProviderAdapterConstants.NoInboundEventsMessage));

    /// <summary>
    /// Accepts no webhook request: the channel has none.
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>false</c>.</returns>
    public Task<Result<bool>> ValidateWebhookAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<bool>.Success(false));

    /// <summary>
    /// Puts the question to the account on the page of the channel. The text is resolved and rendered
    /// the way the built-in channels render it — the confirmation message at the address "in-channel
    /// surface + this channel + the owners of the transaction", in the language of the recipient — and
    /// kept for the account until the transaction ends; it replaces any earlier question of the
    /// transaction. A message that cannot be resolved puts nothing.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="channelUserId">The account at the provider.</param>
    /// <param name="promptContext">Confirmation context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>null</c> — the page has no message the core could address later.</returns>
    public async Task<Result<ChannelMessageRef?>> SendConfirmationPromptAsync(
        TransactionId transactionId,
        string channelUserId,
        ConfirmationPromptContext promptContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelUserId);
        ArgumentNullException.ThrowIfNull(promptContext);

        var promptTemplate = await _messageTemplates.ResolveAsync(
            this, MessageKinds.ConfirmationPrompt, transactionId, cancellationToken);
        if (promptTemplate.IsFailure)
        {
            return Result<ChannelMessageRef?>.Failure(promptTemplate.Error);
        }

        var text = ConfirmationPromptRenderer.Render(
            promptContext,
            MessageTemplateNaturalKeys.PromptDefault,
            promptTemplate.Value.Message,
            _promptLocalizer,
            _logger);

        try
        {
            var end = await EndOfStateAsync(transactionId, cancellationToken);
            if (end is null)
            {
                _logger.LogWarning(
                    "Web provider channel {ChannelType}: the transaction {TransactionId} of the question is not found or has ended; the question is not put",
                    ChannelType,
                    transactionId.ToString());

                return Result<ChannelMessageRef?>.Failure(
                    ChannelAdapterErrorCodes.MessageSendFailed,
                    WebProviderAdapterConstants.TransactionNotFoundMessage);
            }

            await _stateStore.SavePromptAsync(
                ChannelType,
                transactionId,
                new WebProviderPrompt(channelUserId, text, Answered: false),
                end.Value,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                exception,
                "Web provider channel {ChannelType}: the question of transaction {TransactionId} could not be stored",
                ChannelType,
                transactionId.ToString());

            return StateStoreFailed<ChannelMessageRef?>();
        }

        _logger.LogInformation(
            "Web provider channel {ChannelType}: confirmation prompt put to user {ChannelUserIdHash}, transaction {TransactionId}",
            ChannelType,
            LogMasking.Fingerprint(channelUserId),
            transactionId.ToString());

        return Result<ChannelMessageRef?>.Success(null);
    }

    /// <summary>
    /// Adds a message of the core to the feed of the account — while a session of the page of that
    /// account in this channel is live. Without one the message reaches nobody, and the answer says so:
    /// a success would read to the core as a delivery. A link to the claim form and the buttons travel
    /// with the text; a button the page cannot draw — neither a web link nor an item of the menu — is
    /// left out, and a message left without buttons is refused.
    /// </summary>
    /// <param name="message">The message; its kind is one the channel declares.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>null</c> — the feed has no message the core could address later.</returns>
    public async Task<Result<ChannelMessageRef?>> SendMessageAsync(
        ChannelMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var entry = FeedMessageOf(message);
        if (entry.IsFailure)
        {
            _logger.LogError(
                "Web provider channel {ChannelType}: a message of kind {Kind} is not sent: {Reason}",
                ChannelType,
                message.Kind.Value,
                entry.Error.Message);

            return Result<ChannelMessageRef?>.Failure(entry.Error);
        }

        bool appended;
        try
        {
            appended = await _stateStore.TryAppendMessageAsync(
                ChannelType, message.ChannelUserId, entry.Value, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                exception,
                "Web provider channel {ChannelType}: a message to user {ChannelUserIdHash} could not be stored",
                ChannelType,
                LogMasking.Fingerprint(message.ChannelUserId));

            return StateStoreFailed<ChannelMessageRef?>();
        }

        if (!appended)
        {
            _logger.LogInformation(
                "Web provider channel {ChannelType}: user {ChannelUserIdHash} has no live page session; the message is not delivered",
                ChannelType,
                LogMasking.Fingerprint(message.ChannelUserId));

            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                WebProviderAdapterConstants.NoLivePageSessionMessage);
        }

        return Result<ChannelMessageRef?>.Success(null);
    }

    /// <summary>
    /// Keeps the status text of the transaction for its page, in place of any earlier one, until the
    /// transaction ends. A transaction the store of transactions no longer holds has no page left to
    /// show it on: nothing is kept, and the answer says nothing was shown.
    /// </summary>
    /// <param name="notice">What to show.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the page shows the outcome; <c>false</c> — the transaction has ended for the
    /// page.</returns>
    public async Task<Result<bool>> ReportOutcomeAsync(
        TransactionOutcomeNotice notice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        try
        {
            var end = await EndOfStateAsync(notice.TransactionId, cancellationToken);
            if (end is null)
            {
                _logger.LogDebug(
                    "Web provider channel {ChannelType}: transaction {TransactionId} has ended for its page; the outcome is not kept",
                    ChannelType,
                    notice.TransactionId.ToString());

                return Result<bool>.Success(false);
            }

            await _stateStore.SaveOutcomeAsync(
                ChannelType, notice.TransactionId, notice.StatusText, end.Value, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                exception,
                "Web provider channel {ChannelType}: the outcome of transaction {TransactionId} could not be stored",
                ChannelType,
                notice.TransactionId.ToString());

            return StateStoreFailed<bool>();
        }

        return Result<bool>.Success(true);
    }

    /// <summary>
    /// The link of the channel for the transaction: the start route under the public base URL of the
    /// channel. It is built from the transaction alone — the same link every call, and nothing is
    /// created; the sign-in at the provider starts only when the link is opened.
    /// </summary>
    /// <param name="request">The transaction the link starts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link.</returns>
    public Task<Result<string>> GetDeepLinkAsync(
        DeepLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(Result<string>.Success(WebProviderRoutes.StartUri(
            _registration.PublicBaseUrl,
            ChannelType,
            request.TransactionId.ToString())));
    }

    /// <summary>
    /// The channel is healthy when OpenIddict retrieves the configuration of the server of its
    /// registration — the discovery document of the provider, or the static one of a provider without
    /// discovery. A failure of the probe is an unhealthy result with the reason; the cancellation of the
    /// caller propagates.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The health of the channel.</returns>
    public async Task<Result<ChannelHealthStatus>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = _timeProvider.GetTimestamp();

        string? details = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var client = scope.ServiceProvider.GetRequiredService<OpenIddictClientService>();

            await client.GetServerConfigurationByRegistrationIdAsync(ChannelType, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The message of OpenIddict names the registration and what failed — never the secret of
            // the client, which the retrieval of a configuration does not use.
            details = WebProviderAdapterConstants.ServerConfigurationUnavailableMessage + exception.Message;

            _logger.LogWarning(
                exception,
                "Web provider channel {ChannelType}: the configuration of the provider could not be retrieved",
                ChannelType);
        }

        return Result<ChannelHealthStatus>.Success(new ChannelHealthStatus(
            IsHealthy: details is null,
            ChannelType,
            _timeProvider.GetElapsedTime(startedAt),
            details,
            _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// The entry of the feed a message becomes, or the refusal of a message the page cannot show.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The entry, or a refusal naming no value of the message.</returns>
    private Result<WebProviderFeedMessage> FeedMessageOf(ChannelMessage message)
    {
        var sentAt = _timeProvider.GetUtcNow();

        switch (message.Kind.Value)
        {
            case ChannelMessageKinds.PlainText:
                return Result<WebProviderFeedMessage>.Success(
                    new WebProviderFeedMessage(message.Text, ClaimFormLink: null, ActionButtons: null, sentAt));

            case ChannelMessageKinds.ClaimFormLink:
                if (message.ClaimFormLink is not { } formLink)
                {
                    return NotSent(WebProviderAdapterConstants.ClaimFormLinkWithoutContentMessage);
                }

                return WebProviderFeedRules.IsDrawable(formLink)
                    ? Result<WebProviderFeedMessage>.Success(
                        new WebProviderFeedMessage(message.Text, formLink, ActionButtons: null, sentAt))
                    : NotSent(WebProviderAdapterConstants.ClaimFormLinkNotWebAddressMessage);

            case ChannelMessageKinds.ActionButtons:
                var drawable = message.ActionButtons?.Buttons.Where(WebProviderFeedRules.IsDrawable).ToArray() ?? [];
                return drawable.Length > 0
                    ? Result<WebProviderFeedMessage>.Success(new WebProviderFeedMessage(
                        message.Text,
                        ClaimFormLink: null,
                        message.ActionButtons! with { Buttons = drawable },
                        sentAt))
                    : NotSent(WebProviderAdapterConstants.ActionButtonsWithoutButtonsMessage);

            default:
                return Result<WebProviderFeedMessage>.Failure(
                    ChannelAdapterErrorCodes.UnsupportedMessageKind,
                    WebProviderAdapterConstants.UnsupportedMessageKindMessage);
        }
    }

    /// <summary>
    /// The refusal of a message before anything is stored.
    /// </summary>
    /// <param name="message">Message of the refusal; it names no value of the message.</param>
    /// <returns>A failed result.</returns>
    private static Result<WebProviderFeedMessage> NotSent(string message) =>
        Result<WebProviderFeedMessage>.Failure(ChannelAdapterErrorCodes.MessageSendFailed, message);

    /// <summary>
    /// The refusal of an operation whose state could not be stored or read.
    /// </summary>
    /// <typeparam name="T">Type of the value of the result.</typeparam>
    /// <returns>A failed result of the infrastructure.</returns>
    private static Result<T> StateStoreFailed<T>() =>
        Result<T>.Failure(
            ChannelAdapterErrorCodes.MessageSendFailed,
            WebProviderAdapterConstants.StateStoreFailedMessage,
            TransactionErrorCategory.Infrastructure);

    /// <summary>
    /// Moment the state of a transaction ends for its page (<see cref="WebProviderStateLifetime"/>).
    /// </summary>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The moment; <c>null</c> — the store of transactions does not hold the transaction, or
    /// its state has ended already.</returns>
    private async Task<DateTimeOffset?> EndOfStateAsync(TransactionId transactionId, CancellationToken cancellationToken)
    {
        var transaction = await _transactionStore.GetByIdAsync(transactionId, cancellationToken);
        if (transaction is null)
        {
            return null;
        }

        var end = WebProviderStateLifetime.EndOf(transaction, _engineOptions.CurrentValue);

        return end > _timeProvider.GetUtcNow() ? end : null;
    }
}
