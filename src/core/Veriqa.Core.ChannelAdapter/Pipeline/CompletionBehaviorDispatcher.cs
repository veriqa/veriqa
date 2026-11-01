// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Implementation of the post-completion action of a sign-in (SPEC-012 §4.9): the channel and the
/// tenant are taken off the identity snapshot of the transaction, the action is the one
/// <see cref="ICompletionBehaviorResolver"/> answers for that channel, and a welcome message is a
/// message of the typed placeholder schema sent by the standard send of the channel (SPEC-003 CA-153).
/// A menu and a redirect are the same kind of message with buttons under it (SPEC-012 CFG-094,
/// CFG-095): what the buttons are is resolved for the ownership of the transaction and the channel.
/// An address that carries the link handle placeholder gets a handle issued for the message and
/// recorded in <see cref="ILinkHandleStore"/>; an address without it never touches the store.
/// </summary>
internal sealed class CompletionBehaviorDispatcher : ICompletionBehaviorDispatcher
{
    /// <summary>
    /// Natural Key of the label of the control a platform shows instead of the buttons when it folds
    /// them into a list to pick from (<see cref="ActionButtonsContent.ChooserLabel"/>). Its base text
    /// fits the shortest such control a platform names today — 20 characters for the button of a
    /// WhatsApp list message.
    /// </summary>
    private const string ChooserLabelNaturalKey = "Choose";

    /// <summary>
    /// Registered channel adapters.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Resolver of the action the channel performs.
    /// </summary>
    private readonly ICompletionBehaviorResolver _resolver;

    /// <summary>
    /// Canonical level resolver the buttons of a menu and of a redirect are read through.
    /// </summary>
    private readonly IConfigurationResolver _configuration;

    /// <summary>
    /// Answers whether the host registered a handler of menu item presses. The dispatcher is a
    /// singleton and the lifetime of the handler is the host's choice, so the handler itself is never
    /// taken here — only the fact of its registration is read.
    /// </summary>
    private readonly IServiceProviderIsService _registeredServices;

    /// <summary>
    /// Access to the resolved messages the actions are worded with.
    /// </summary>
    private readonly IMessageTemplateAccessor _messageTemplates;

    /// <summary>
    /// Natural Key localizer.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _localizer;

    /// <summary>
    /// Store the fingerprint of an issued link handle is recorded in.
    /// </summary>
    private readonly ILinkHandleStore _linkHandles;

    /// <summary>
    /// Transaction Engine settings: the retention of a completed transaction is the lifetime of a
    /// link handle, so the handle does not outlive the sign-in it stands for.
    /// </summary>
    private readonly IOptionsMonitor<TransactionEngineOptions> _engineOptions;

    /// <summary>
    /// Clock the deadline of a link handle is counted from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Component metrics a refused send is counted into.
    /// </summary>
    private readonly ChannelAdapterMetrics _metrics;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<CompletionBehaviorDispatcher> _logger;

    /// <summary>
    /// Creates the dispatcher of the post-completion action.
    /// </summary>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="resolver">Resolver of the action the channel performs.</param>
    /// <param name="configuration">Canonical level resolver.</param>
    /// <param name="registeredServices">Registration facts of the container.</param>
    /// <param name="messageTemplates">Access to resolved messages.</param>
    /// <param name="localizer">Natural Key localizer.</param>
    /// <param name="linkHandles">Store of issued link handles.</param>
    /// <param name="engineOptions">Transaction Engine settings.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="metrics">Component metrics.</param>
    /// <param name="logger">Logger.</param>
    public CompletionBehaviorDispatcher(
        IEnumerable<IChannelAdapter> adapters,
        ICompletionBehaviorResolver resolver,
        IConfigurationResolver configuration,
        IServiceProviderIsService registeredServices,
        IMessageTemplateAccessor messageTemplates,
        IConfirmationPromptLocalizer localizer,
        ILinkHandleStore linkHandles,
        IOptionsMonitor<TransactionEngineOptions> engineOptions,
        TimeProvider timeProvider,
        ChannelAdapterMetrics metrics,
        ILogger<CompletionBehaviorDispatcher> logger)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _registeredServices = registeredServices ?? throw new ArgumentNullException(nameof(registeredServices));
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _linkHandles = linkHandles ?? throw new ArgumentNullException(nameof(linkHandles));
        _engineOptions = engineOptions ?? throw new ArgumentNullException(nameof(engineOptions));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task DispatchAsync(Transaction completed, CancellationToken cancellationToken = default)
    {
        // Filters what the axis does not cover, finds the channel and the tenant of the identity
        // snapshot, asks the resolver for the action and performs it. Whatever fails below is logged
        // and swallowed: the sign-in is already completed, and its continuation must not undo it.
        ArgumentNullException.ThrowIfNull(completed);

        // The axis covers sign-in only: a confirmation transaction has no post-completion action.
        if (!string.Equals(completed.Type, TransactionTypes.Login, StringComparison.Ordinal))
        {
            return;
        }

        if (completed.ChannelIdentitySnapshot is not { } snapshot)
        {
            _logger.LogDebug(
                "The completed sign-in has no channel identity snapshot — no post-completion action. TransactionId: {TransactionId}",
                completed.Id.ToString());
            return;
        }

        try
        {
            // The caller may be a web path that has no channel tenant of its own, so the tenant the
            // resolution and the send below read their credentials for is the one of the snapshot.
            using var tenantScope = ChannelTenantContext.BeginScope(snapshot.TenantId);

            var adapter = ChannelWebhookPipeline.FindAdapter(_adapters, snapshot.ChannelType);
            if (adapter is null)
            {
                _logger.LogDebug(
                    "No adapter serves the channel of the completed sign-in — no post-completion action. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                    snapshot.ChannelType,
                    completed.Id.ToString());
                return;
            }

            var action = await _resolver.ResolveAsync(completed, adapter, cancellationToken);

            switch (action)
            {
                case CompletionBehaviorAction.None:
                    return;

                case CompletionBehaviorAction.WelcomeMessage:
                    await SendWelcomeAsync(completed, snapshot, adapter, cancellationToken);
                    return;

                case CompletionBehaviorAction.Menu:
                    await SendMenuAsync(completed, snapshot, adapter, cancellationToken);
                    return;

                case CompletionBehaviorAction.Redirect:
                    await SendRedirectAsync(completed, snapshot, adapter, cancellationToken);
                    return;

                default:
                    // Reached only through a channel that declares an action the core has no rendering
                    // of: the resolver clamps every action outside the facts of the channel.
                    _logger.LogWarning(
                        "The channel declares post-completion action {Action}, which has no rendering — nothing is sent. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                        action,
                        adapter.ChannelType,
                        completed.Id.ToString());
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The post-completion action of the sign-in failed; the sign-in result is not affected. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                snapshot.ChannelType,
                completed.Id.ToString());
        }
    }

    /// <summary>
    /// Sends the welcome message: the message of the kind resolved for the ownership of the
    /// transaction and the channel, rendered as plain text with the claims of the resolved identity
    /// and the brand name of the transaction, and sent through the content-kind gate of the channel. A refused send is already logged and
    /// counted by that gate, so nothing is added here and nothing is retried.
    /// </summary>
    /// <param name="completed">The completed sign-in.</param>
    /// <param name="snapshot">Channel identity snapshot of the transaction — the recipient.</param>
    /// <param name="adapter">Adapter of the snapshot's channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task SendWelcomeAsync(
        Transaction completed,
        ChannelIdentitySnapshot snapshot,
        IChannelAdapter adapter,
        CancellationToken cancellationToken)
    {
        var message = await _messageTemplates.RequireAsync(
            InChannelMessageAddress.Of(MessageKinds.PostLoginWelcome, completed, adapter.ChannelType),
            TransactionResolutionContext.For(completed),
            cancellationToken);

        // Recipient-locale chain: channel snapshot locale → initiation-page language → base.
        var recipientLocale = snapshot.Locale ?? completed.GetUiLocale();

        var text = MessageTextRenderer.Render(
            message,
            CompletedSignInSlotValues(completed),
            recipientLocale,
            completed.GetUiTimeZone(),
            _localizer,
            MessageRenderMode.PlainText,
            _logger);

        if (text is null)
        {
            _logger.LogWarning(
                "No step of the resolved ladder of message kind {MessageKind} states a plain text — the welcome message is not sent. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                message.Kind,
                adapter.ChannelType,
                completed.Id.ToString());
            return;
        }

        await ChannelOutbound.SendMessageAsync(
            adapter,
            new ChannelMessage
            {
                ChannelUserId = snapshot.ChannelUserId,
                Text = text
            },
            _logger,
            _metrics,
            cancellationToken);
    }

    /// <summary>
    /// Sends the redirect: one link button under the text of its message kind. The address is the one
    /// the levels state for the channel, the label is a Natural Key localized for the recipient. A
    /// deployment that chose the action and stated no address gets a WARNING and no message. An
    /// address asking for a link handle gets one issued; when it cannot be issued the message is not
    /// sent — a link with the literal placeholder is worse than no button.
    /// </summary>
    /// <param name="completed">The completed sign-in.</param>
    /// <param name="snapshot">Channel identity snapshot of the transaction — the recipient.</param>
    /// <param name="adapter">Adapter of the snapshot's channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task SendRedirectAsync(
        Transaction completed,
        ChannelIdentitySnapshot snapshot,
        IChannelAdapter adapter,
        CancellationToken cancellationToken)
    {
        var context = TransactionResolutionContext.For(completed);

        var url = await _configuration.ResolveAsync(
            CompletionBehaviorConfigKeys.RedirectUrl,
            context,
            ConfigDimensionValues.Of((ConfigDimensionNames.Channel, adapter.ChannelType)),
            cancellationToken);

        // The key declares no answer of its own: a chain no level answered is an address nobody stated.
        if (url is not { SourceLevel: not null, Value: { } configuredUrl })
        {
            _logger.LogWarning(
                "Post-completion action Redirect has no address stated for the channel — nothing is sent. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                adapter.ChannelType,
                completed.Id.ToString());
            return;
        }

        string? handle = null;

        if (CompletionBehaviorButtonAddress.HasHandlePlaceholder(configuredUrl))
        {
            handle = await IssueLinkHandleAsync(completed, adapter.ChannelType, cancellationToken);

            if (handle is null)
            {
                return;
            }
        }

        var buttonText = await _configuration.ResolveAsync(
            CompletionBehaviorConfigKeys.RedirectButtonText,
            context,
            ConfigDimensionValues.None,
            cancellationToken);

        var recipientLocale = RecipientLocale(completed, snapshot);

        await SendActionButtonsAsync(
            completed,
            snapshot,
            adapter,
            MessageKinds.PostLoginRedirect,
            recipientLocale,
            [
                new ChannelActionButton
                {
                    Label = _localizer.ResolveOrBaseText(buttonText.Value, recipientLocale),
                    Url = CompletionBehaviorButtonAddress.Of(configuredUrl, handle)
                }
            ],
            cancellationToken);
    }

    /// <summary>
    /// Sends the menu: the items the levels state for the channel, in their order, under the text of
    /// its message kind. An item whose action is an address becomes a link button; any other action is
    /// callback data, which is worth a button only while the host has a handler to hand the press to —
    /// without one such items are left out and the link items still go. The link items asking for a
    /// link handle share one handle issued for the message; when it cannot be issued they are left
    /// out and the other items still go. Callback data is never searched for the placeholder.
    /// </summary>
    /// <param name="completed">The completed sign-in.</param>
    /// <param name="snapshot">Channel identity snapshot of the transaction — the recipient.</param>
    /// <param name="adapter">Adapter of the snapshot's channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task SendMenuAsync(
        Transaction completed,
        ChannelIdentitySnapshot snapshot,
        IChannelAdapter adapter,
        CancellationToken cancellationToken)
    {
        var resolved = await _configuration.ResolveAsync(
            CompletionBehaviorConfigKeys.MenuItems,
            TransactionResolutionContext.For(completed),
            ConfigDimensionValues.Of((ConfigDimensionNames.Channel, adapter.ChannelType)),
            cancellationToken);

        // The key declares no answer of its own: a chain no level answered is a menu nobody stated.
        if (resolved is not { SourceLevel: not null, Value: { Count: > 0 } items })
        {
            _logger.LogWarning(
                "Post-completion action Menu has no items stated for the channel — nothing is sent. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                adapter.ChannelType,
                completed.Id.ToString());
            return;
        }

        var recipientLocale = RecipientLocale(completed, snapshot);
        var pressHandlerRegistered = _registeredServices.IsService(typeof(IMenuItemPressHandler));
        var buttons = new List<ChannelActionButton>(items.Count);
        var skippedCallbackItems = 0;
        string? handle = null;
        var handleIssueAttempted = false;

        foreach (var item in items)
        {
            var label = _localizer.ResolveOrBaseText(item.Text, recipientLocale);

            if (CompletionBehaviorButtonAddress.IsUrl(item.Action))
            {
                if (CompletionBehaviorButtonAddress.HasHandlePlaceholder(item.Action))
                {
                    // One handle per message: the first item asking for it issues it, the rest share it.
                    if (!handleIssueAttempted)
                    {
                        handle = await IssueLinkHandleAsync(completed, adapter.ChannelType, cancellationToken);
                        handleIssueAttempted = true;
                    }

                    if (handle is null)
                    {
                        continue;
                    }
                }

                buttons.Add(new ChannelActionButton
                {
                    Label = label,
                    Url = CompletionBehaviorButtonAddress.Of(item.Action, handle)
                });
            }
            else if (pressHandlerRegistered)
            {
                // The marker is what lets the adapter recognize the press as its own; the operator's
                // value follows it untouched and comes back to the handler as it was stated.
                buttons.Add(new ChannelActionButton
                {
                    Label = label,
                    CallbackData = CallbackDataPrefixes.MenuItem + item.Action
                });
            }
            else
            {
                skippedCallbackItems++;
            }
        }

        if (skippedCallbackItems > 0)
        {
            _logger.LogWarning(
                "No handler of menu item presses is registered — {SkippedCount} callback item(s) of the post-completion menu are not rendered. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                skippedCallbackItems,
                adapter.ChannelType,
                completed.Id.ToString());
        }

        if (buttons.Count == 0)
        {
            _logger.LogWarning(
                "The post-completion menu has no item left to render — nothing is sent. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                adapter.ChannelType,
                completed.Id.ToString());
            return;
        }

        await SendActionButtonsAsync(
            completed,
            snapshot,
            adapter,
            MessageKinds.PostLoginMenu,
            recipientLocale,
            buttons,
            cancellationToken);
    }

    /// <summary>
    /// Sends a message with buttons: the text of the kind resolved for the ownership of the
    /// transaction and the channel, rendered the way the welcome is, with the buttons under it. A
    /// refused send — a channel that declares no such content kind included — is logged and counted
    /// by the content-kind gate, so nothing is added here and nothing is retried.
    /// </summary>
    /// <param name="completed">The completed sign-in.</param>
    /// <param name="snapshot">Channel identity snapshot of the transaction — the recipient.</param>
    /// <param name="adapter">Adapter of the snapshot's channel.</param>
    /// <param name="messageKind">Kind of the message the text above the buttons belongs to.</param>
    /// <param name="recipientLocale">
    /// Locale of the recipient the labels were localized for; the label of the list a platform may fold
    /// the buttons into is localized for it too.
    /// </param>
    /// <param name="buttons">Buttons in display order; never empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task SendActionButtonsAsync(
        Transaction completed,
        ChannelIdentitySnapshot snapshot,
        IChannelAdapter adapter,
        string messageKind,
        string? recipientLocale,
        IReadOnlyList<ChannelActionButton> buttons,
        CancellationToken cancellationToken)
    {
        var message = await _messageTemplates.RequireAsync(
            InChannelMessageAddress.Of(messageKind, completed, adapter.ChannelType),
            TransactionResolutionContext.For(completed),
            cancellationToken);

        var text = MessageTextRenderer.Render(
            message,
            CompletedSignInSlotValues(completed),
            recipientLocale,
            completed.GetUiTimeZone(),
            _localizer,
            MessageRenderMode.PlainText,
            _logger);

        if (text is null)
        {
            _logger.LogWarning(
                "No step of the resolved ladder of message kind {MessageKind} states a plain text — the message with buttons is not sent. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                message.Kind,
                adapter.ChannelType,
                completed.Id.ToString());
            return;
        }

        await ChannelOutbound.SendMessageAsync(
            adapter,
            new ChannelMessage
            {
                ChannelUserId = snapshot.ChannelUserId,
                Kind = ChannelMessageKind.ActionButtons,
                Text = text,
                ActionButtons = new ActionButtonsContent
                {
                    Buttons = buttons,
                    ChooserLabel = _localizer.ResolveOrBaseText(ChooserLabelNaturalKey, recipientLocale)
                }
            },
            _logger,
            _metrics,
            cancellationToken);
    }

    /// <summary>
    /// Issues the link handle of one post-completion message and records its fingerprint for the
    /// client the sign-in is attributed to, with the retention of a completed transaction as its
    /// lifetime. A sign-in attributed to no client and a store that refused the record both leave
    /// the message without a handle: a WARNING is written and the caller leaves out the buttons that
    /// asked for it. The handle is a secret and goes nowhere but into the returned value.
    /// </summary>
    /// <param name="completed">The completed sign-in.</param>
    /// <param name="channelType">Channel the message is sent through.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The handle; null — none could be issued.</returns>
    private async Task<string?> IssueLinkHandleAsync(
        Transaction completed,
        string channelType,
        CancellationToken cancellationToken)
    {
        if (completed.GetApplicationId() is not { Length: > 0 } applicationId)
        {
            _logger.LogWarning(
                "The completed sign-in is attributed to no client, so no link handle can be issued — the post-completion buttons asking for one are not sent. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                channelType,
                completed.Id.ToString());
            return null;
        }

        var (handle, hash) = LinkHandle.Issue();

        try
        {
            await _linkHandles.SaveAsync(
                new LinkHandleRecord
                {
                    HandleHash = hash,
                    TransactionId = completed.Id,
                    ApplicationId = applicationId,
                    ExpiresAt = _timeProvider.GetUtcNow()
                        .AddSeconds(_engineOptions.CurrentValue.CompletedRetentionSeconds)
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "The link handle store refused the record — the post-completion buttons asking for a link handle are not sent. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                channelType,
                completed.Id.ToString());
            return null;
        }

        return handle;
    }

    /// <summary>
    /// Locale of the recipient: channel snapshot locale → initiation-page language → base.
    /// </summary>
    /// <param name="completed">The completed sign-in.</param>
    /// <param name="snapshot">Channel identity snapshot of the transaction — the recipient.</param>
    /// <returns>The locale; null — the base one.</returns>
    private static string? RecipientLocale(Transaction completed, ChannelIdentitySnapshot snapshot) =>
        snapshot.Locale ?? completed.GetUiLocale();

    /// <summary>
    /// Values of the slots of a message after a completed sign-in: the claims of the resolved identity
    /// and the brand name the levels of the transaction state, as every text the core renders has it.
    /// </summary>
    /// <param name="completed">The completed sign-in.</param>
    /// <returns>The slot values.</returns>
    private static Dictionary<string, object?> CompletedSignInSlotValues(Transaction completed)
    {
        var values = IdentityClaimSlotValues(completed.ResolvedIdentitySnapshot);

        TransactionSlotValues.AddBrandName(values, completed.GetBrandName());

        return values;
    }

    /// <summary>
    /// Values of the slots filled from the claims of the resolved identity (SPEC-036 TPL-053): slot
    /// name → sanitized claim value. A claim the identity does not carry, or one that is empty once
    /// sanitized, leaves its slot without a value, and the render degrades onto a variant that does
    /// not reference it.
    /// </summary>
    /// <param name="identity">Resolved identity of the completed sign-in; null — none was resolved.</param>
    /// <returns>The slot values.</returns>
    private static Dictionary<string, object?> IdentityClaimSlotValues(ResolvedIdentitySnapshot? identity)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (identity?.Claims is { } claims
            && claims.TryGetValue(VeriqaClaimTypes.Name, out var name)
            && MessageValueSanitizer.Sanitize(name) is { } displayName)
        {
            values[SlotNames.Name] = displayName;
        }

        return values;
    }
}
