// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.WeChat.Configuration;
using Veriqa.Core.ChannelAdapter.WeChat.Constants;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.WeChat;

/// <summary>
/// WeChat channel adapter (SPEC-003 §6): a sign-in entered by a QR code of a WeChat Official
/// Account and confirmed in the chat with it. The entry is a temporary QR code of the platform
/// whose scene names the transaction; scanning it delivers an event that starts the sign-in, the
/// user is asked with a menu message of two options, and the outcome arrives as a message of its own.
/// </summary>
/// <remarks>
/// The adapter holds no state of an account (SPEC-003 CA-014, CA-164): the client of the Official
/// Account API — with the access token and the QR codes it remembers — and the token of the inbound
/// signature are obtained per operation for the tenant of the current flow.
/// </remarks>
internal sealed class WeChatChannelAdapter : IChannelAdapter
{
    /// <summary>
    /// Facts WeChat declares about itself (SPEC-003 §6.1): the options of a menu message let the
    /// user confirm inside the channel; a message is plain text, a link to the claim completion form
    /// (SPEC-041 C76) or the buttons of a post-sign-in action (SPEC-012 §4.9) — links as markup in a
    /// text message, callbacks as the options of a menu message — so every action after a sign-in is
    /// supported; a delivered message cannot be edited, so the outcome is a new message; an inbound
    /// message names no language of the user, so the recipient locale is the one of the transaction.
    /// </summary>
    private static readonly ChannelCapabilities DeclaredCapabilities = new()
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
        ProvidesRecipientLocale = false,
        RendersEmoji = true,
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
    /// What WeChat lets the adapter draw as buttons: links (as markup in the text) and callbacks (as
    /// menu options). The sendCustomMessage documentation names no limit of a caption, of an option
    /// identifier or of the number of options, so none is checked beforehand — the platform is the
    /// judge (SPEC-003 §4.9.3).
    /// </summary>
    private static readonly ActionButtonLimits DrawableButtonLimits = new();

    /// <summary>
    /// Channel client factory — the source of the Official Account API client of a tenant.
    /// </summary>
    private readonly IChannelClientFactory _clientFactory;

    /// <summary>
    /// Canonical layer resolver — the source of the tenant's token of the inbound signature. It is
    /// resolved per request for the tenant of that request and never captured in the constructor.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Localizer of channel messages into the recipient's language (SPEC-017 §7.2).
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// The accessor this adapter takes the templates of its channel messages from: the core resolves
    /// them through the canonical resolver (SPEC-036 TPL-001). Singleton, like this adapter.
    /// </summary>
    private readonly IChannelMessageTemplates _messageTemplates;

    /// <summary>
    /// Clock behind the freshness check of an inbound request, the capture timestamps and the
    /// health-check stamps.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<WeChatChannelAdapter> _logger;

    /// <summary>
    /// Creates an instance of <see cref="WeChatChannelAdapter"/>.
    /// </summary>
    /// <param name="clientFactory">Channel client factory.</param>
    /// <param name="resolver">Canonical layer resolver (source of the tenant's credentials).</param>
    /// <param name="promptLocalizer">Channel message localizer.</param>
    /// <param name="messageTemplates">Accessor of the channel message templates of this adapter.</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public WeChatChannelAdapter(
        IChannelClientFactory clientFactory,
        IConfigurationResolver resolver,
        IConfirmationPromptLocalizer promptLocalizer,
        IChannelMessageTemplates messageTemplates,
        TimeProvider timeProvider,
        ILogger<WeChatChannelAdapter> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ChannelType => ChannelTypes.WeChat;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities => DeclaredCapabilities;

    /// <inheritdoc />
    public async Task<Result<bool>> ValidateWebhookAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // The method proves a request comes from the platform and is fresh. The platform signs
        // both the address check (GET) and a delivery (POST) the same way: SHA-1 over the token of
        // the account, the timestamp and the nonce, sorted and joined. The token is the tenant's,
        // so it is resolved for the tenant of this very request — a signature made with the token
        // of another tenant does not match.
        if (!request.Query.TryGetValue(WeChatAdapterConstants.SignatureQueryKey, out var signature)
            || !request.Query.TryGetValue(WeChatAdapterConstants.TimestampQueryKey, out var timestamp)
            || !request.Query.TryGetValue(WeChatAdapterConstants.NonceQueryKey, out var nonce)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var signedAtSeconds))
        {
            return Result<bool>.Success(false);
        }

        // In plaintext mode the body is not part of the signature, so a captured request could be
        // sent again as it is. The timestamp is signed, and a stale one is refused.
        var ageSeconds = _timeProvider.GetUtcNow().ToUnixTimeSeconds() - signedAtSeconds;
        if (ageSeconds is > WeChatAdapterConstants.MaxInboundAgeSeconds or < -WeChatAdapterConstants.MaxInboundAgeSeconds)
        {
            return Result<bool>.Success(false);
        }

        var credentialsResult = await _resolver.ResolveCredentialsAsync(
            WeChatConfigKeys.Credentials,
            ChannelCredentialContext.ForCurrentTenant(ChannelTypes.WeChat),
            _logger,
            cancellationToken);
        if (credentialsResult.IsFailure || string.IsNullOrWhiteSpace(credentialsResult.Value.Token))
        {
            _logger.LogError("WeChat webhook rejected: the signature token is unavailable.");
            return Result<bool>.Success(false);
        }

        var signedParts = new[] { credentialsResult.Value.Token, timestamp, nonce };
        Array.Sort(signedParts, StringComparer.Ordinal);
        var expectedSignature = Convert.ToHexStringLower(
            SHA1.HashData(Encoding.UTF8.GetBytes(string.Concat(signedParts))));

        return Result<bool>.Success(CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedSignature),
            Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant())));
    }

    /// <inheritdoc />
    public Task<Result<ChannelInboundResult>> ProcessInboundEventAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // The method reads one inbound message down to its meaning for the core: a scanned QR code
        // starts the sign-in its scene names, a pressed menu option decides the transaction its
        // identifier names or reports an item of the post-sign-in menu, any other text of a user
        // names no transaction.
        var message = WeChatInboundMessage.TryParse(request.Body);
        if (message?.FromUserName is not { Length: > 0 } openId)
        {
            return UnrelatedAsync();
        }

        if (string.Equals(message.MsgType, WeChatAdapterConstants.InboundTypeEvent, StringComparison.Ordinal))
        {
            return TryReadTransactionId(ReadScannedScene(message), CallbackDataPrefixes.Auth, out var startedId)
                ? RecognizedAsync(new ChannelAuthStartResult
                {
                    TransactionId = startedId,
                    Identity = BuildIdentitySnapshot(openId)
                })
                : UnrelatedAsync();
        }

        if (!string.Equals(message.MsgType, WeChatAdapterConstants.InboundTypeText, StringComparison.Ordinal))
        {
            return UnrelatedAsync();
        }

        // A pressed option of a menu message arrives as a text message that carries the identifier
        // of the option next to its text; a typed message carries none.
        if (TryReadTransactionId(message.BizMsgMenuId, CallbackDataPrefixes.Confirm, out var confirmedId))
        {
            return RecognizedAsync(new ChannelAuthConfirmResult
            {
                TransactionId = confirmedId,
                Identity = BuildIdentitySnapshot(openId)
            });
        }

        if (TryReadTransactionId(message.BizMsgMenuId, CallbackDataPrefixes.Decline, out var declinedId))
        {
            return RecognizedAsync(new ChannelAuthDeclineResult
            {
                TransactionId = declinedId,
                Identity = BuildIdentitySnapshot(openId)
            });
        }

        // A pressed callback option of the post-sign-in menu (SPEC-012 §4.9) names no transaction,
        // but it is a press, not a message addressed to nothing. The marker alone names no item and
        // leaves the event unrelated. The value is the operator's data and is not logged.
        if (MenuItemPayload.TryRead(message.BizMsgMenuId, out var menuItemValue))
        {
            return RecognizedAsync(new ChannelMenuItemPressedResult
            {
                Identity = BuildIdentitySnapshot(openId),
                Value = menuItemValue,
                PressId = string.IsNullOrEmpty(message.MsgId) ? null : message.MsgId
            });
        }

        if (MenuItemPayload.HasMarker(message.BizMsgMenuId))
        {
            return UnrelatedAsync();
        }

        return RecognizedAsync(new ChannelUnaddressedResult { Identity = BuildIdentitySnapshot(openId) });
    }

    /// <inheritdoc />
    public async Task<Result<ChannelMessageRef?>> SendConfirmationPromptAsync(
        TransactionId transactionId,
        string channelUserId,
        ConfirmationPromptContext promptContext,
        CancellationToken cancellationToken = default)
    {
        // The method sends one menu message: the question and the two options under it. The
        // recipient locale travels with the context and nowhere else (SPEC-017 §7.2); the channel
        // names no language of the user, so it is the locale of the transaction.
        ArgumentNullException.ThrowIfNull(promptContext);

        var recipientLocale = promptContext.RecipientLocale;

        // The template is resolved by the core, which takes the address and the owners itself — the
        // in-channel surface, this channel and the levels the transaction states — so a variant
        // declared by a tenant or an application answers instead of the shipped one (SPEC-036
        // TPL-116); a template it cannot resolve sends nothing. A context without details yields the
        // default text (ICC-042).
        var promptTemplate = await _messageTemplates.ResolveAsync(
            this, MessageKinds.ConfirmationPrompt, transactionId, cancellationToken);
        if (promptTemplate.IsFailure)
        {
            return Result<ChannelMessageRef?>.Failure(promptTemplate.Error);
        }

        var messageText = ConfirmationPromptRenderer.Render(
            promptContext,
            MessageTemplateNaturalKeys.PromptDefault,
            promptTemplate.Value.Message,
            _promptLocalizer,
            _logger);

        // The option captions are Natural Keys localized into the recipient's language (ICC-050);
        // a missing translation falls back to the base text of the key.
        var confirmOptionText = _promptLocalizer.ResolveOrBaseText(
            WeChatAdapterConstants.ConfirmButtonText, recipientLocale);
        var declineOptionText = _promptLocalizer.ResolveOrBaseText(
            WeChatAdapterConstants.DeclineButtonText, recipientLocale);

        var transactionIdString = transactionId.ToString();

        return await SendMenuAsync(
            channelUserId,
            messageText,
            [
                new MenuOption(CallbackDataPrefixes.Confirm + transactionIdString, confirmOptionText),
                new MenuOption(CallbackDataPrefixes.Decline + transactionIdString, declineOptionText)
            ],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<ChannelMessageRef?>> SendMessageAsync(
        ChannelMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // A link to the claim completion form (SPEC-041 C76) is drawn as a link button of an action
        // message would be: one link in the text under the message.
        if (message.ClaimFormLink is { } formLink)
        {
            return SendActionButtonsAsync(
                message.ChannelUserId,
                message.Text,
                new ActionButtonsContent
                {
                    Buttons = [new ChannelActionButton { Label = formLink.ButtonLabel, Url = formLink.Url }]
                },
                cancellationToken);
        }

        if (message.ActionButtons is { } actionButtons)
        {
            return SendActionButtonsAsync(message.ChannelUserId, message.Text, actionButtons, cancellationToken);
        }

        return SendTextAsync(message.ChannelUserId, message.Text, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<ChannelHealthStatus>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        // The method reports whether the channel is configured, without a network call: the
        // platform counts the access-token requests of an account against a daily quota, and a
        // probe must not spend it. The probe runs outside any request, and the installation's own
        // credentials (the default implicit tenant) are the answer it wants: a health endpoint
        // reports the state of this deployment, not of one of its tenants. That is said
        // explicitly, so the read is not mistaken for a request path that forgot to state its
        // tenant (CA-164).
        var startedAt = _timeProvider.GetUtcNow();

        using var tenantScope = ChannelTenantContext.BeginScope(tenantId: null);
        var clientResult = await GetClientAsync(cancellationToken);

        var checkedAt = _timeProvider.GetUtcNow();

        return Result<ChannelHealthStatus>.Success(new ChannelHealthStatus(
            IsHealthy: clientResult.IsSuccess,
            ChannelType: ChannelTypes.WeChat,
            ResponseTime: checkedAt - startedAt,
            Details: clientResult.IsSuccess ? null : clientResult.Error.Code,
            CheckedAt: checkedAt));
    }

    /// <inheritdoc />
    public async Task<Result<string>> GetDeepLinkAsync(
        DeepLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The link of this channel is the content of a QR code the platform creates for the
        // transaction. The client of the tenant's account remembers the code it created, so a
        // repeated call for the same transaction returns the same link without a second call to
        // the platform (SPEC-025 HOP-011a). The link does not depend on the purpose of the request:
        // the code is remembered per transaction, so asking for another purpose costs no platform call.
        var clientResult = await GetClientAsync(cancellationToken);
        if (clientResult.IsFailure)
        {
            _logger.LogError("WeChat credentials are unavailable for the sign-in QR code.");
            return Result<string>.Failure(
                ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                "WeChat credentials are unavailable for the sign-in QR code.");
        }

        return await clientResult.Value.GetSignInQrCodeLinkAsync(request.TransactionId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ReportOutcomeAsync(
        TransactionOutcomeNotice notice,
        CancellationToken cancellationToken = default)
    {
        // WeChat has neither callback answers nor an editable delivered message: the only way to
        // show a terminal state is a new message. The status text is already localized by the core.
        ArgumentNullException.ThrowIfNull(notice);

        var sendResult = await SendTextAsync(notice.ChannelUserId, notice.StatusText, cancellationToken);

        return sendResult.IsSuccess
            ? Result<bool>.Success(true)
            : Result<bool>.Failure(sendResult.Error);
    }

    /// <summary>
    /// Obtains the Official Account API client of the tenant of the current flow. The tenant comes
    /// from the ambient context; null is the default implicit tenant (a self-hosted installation).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client, or a failure when no layer supplies the credentials of the account.</returns>
    private ValueTask<Result<WeChatApiClient>> GetClientAsync(CancellationToken cancellationToken) =>
        _clientFactory.GetOrCreateClientAsync<WeChatApiClient>(
            ChannelCredentialContext.ForCurrentTenant(ChannelTypes.WeChat),
            cancellationToken);

    /// <summary>
    /// Sends a plain-text customer service message.
    /// </summary>
    /// <param name="openId">OpenID of the recipient.</param>
    /// <param name="text">Text of the message, already worded for the recipient.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No message reference (the platform names none), or a failure.</returns>
    private Task<Result<ChannelMessageRef?>> SendTextAsync(
        string openId,
        string text,
        CancellationToken cancellationToken) =>
        SendAsync(
            openId,
            new
            {
                touser = openId,
                msgtype = WeChatAdapterConstants.MessageTypeText,
                text = new { content = text }
            },
            cancellationToken);

    /// <summary>
    /// Sends a menu message: the text and the options under it, in the order they are listed.
    /// </summary>
    /// <param name="openId">OpenID of the recipient.</param>
    /// <param name="headContent">Text above the options, already worded for the recipient.</param>
    /// <param name="options">Options of the menu.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No message reference (the platform names none), or a failure.</returns>
    private Task<Result<ChannelMessageRef?>> SendMenuAsync(
        string openId,
        string headContent,
        IReadOnlyList<MenuOption> options,
        CancellationToken cancellationToken) =>
        SendAsync(
            openId,
            new
            {
                touser = openId,
                msgtype = WeChatAdapterConstants.MessageTypeMenu,
                msgmenu = new
                {
                    head_content = headContent,
                    list = options.Select(option => new { id = option.Id, content = option.Content }).ToArray(),
                    tail_content = string.Empty
                }
            },
            cancellationToken);

    /// <summary>
    /// Sends the buttons of a message the way WeChat can show them (SPEC-012 §4.9): the links as
    /// markup in a text message, the callbacks as the options of a menu message. A message with both
    /// is two messages — the menu with the text first, then the links on their own — because the
    /// platform promises no markup inside a menu message. A message none of whose buttons can be drawn
    /// is not sent at all: its text only introduces the buttons.
    /// </summary>
    /// <param name="openId">OpenID of the recipient.</param>
    /// <param name="text">Text above the buttons, already worded for the recipient.</param>
    /// <param name="content">Buttons of the message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// No message reference (the platform names none), or a failure — also when the menu went out and
    /// the message with the links was refused.
    /// </returns>
    private async Task<Result<ChannelMessageRef?>> SendActionButtonsAsync(
        string openId,
        string text,
        ActionButtonsContent content,
        CancellationToken cancellationToken)
    {
        var buttons = ActionButtonSelection.SelectDrawable(content, DrawableButtonLimits, ChannelType, _logger);
        if (buttons.Count == 0)
        {
            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "None of the action buttons of the message can be drawn in WeChat");
        }

        // A drawable button carries exactly one of the two, so a button without a link is a callback.
        var links = buttons.Where(button => !string.IsNullOrEmpty(button.Url)).ToList();
        var options = buttons
            .Where(button => string.IsNullOrEmpty(button.Url))
            .Select(button => new MenuOption(button.CallbackData!, button.Label))
            .ToList();

        if (options.Count == 0)
        {
            return await SendTextAsync(openId, ComposeTextWithLinks(text, links), cancellationToken);
        }

        // The payload of an option goes to the platform as is, the ownership marker included; it is
        // the operator's data and is not logged.
        var menuResult = await SendMenuAsync(openId, text, options, cancellationToken);
        if (menuResult.IsFailure || links.Count == 0)
        {
            return menuResult;
        }

        return await SendTextAsync(openId, ComposeTextWithLinks(text: null, links), cancellationToken);
    }

    /// <summary>
    /// Composes the text of a message with links: the text, if any, then one link per line. The
    /// address goes into the markup as the caller gave it — the core builds the address of the claim
    /// form from escaped parts — and the caption is made safe for the markup around it.
    /// </summary>
    /// <param name="text">Text above the links; null or empty — the links alone.</param>
    /// <param name="links">Link buttons to show, in the order they are listed.</param>
    /// <returns>The text of the message. It carries the addresses, so it is never logged.</returns>
    private static string ComposeTextWithLinks(string? text, IReadOnlyList<ChannelActionButton> links)
    {
        var lines = new List<string>(links.Count + 1);
        if (!string.IsNullOrEmpty(text))
        {
            lines.Add(text);
        }

        foreach (var link in links)
        {
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                WeChatAdapterConstants.TextLinkMarkupFormat,
                link.Url,
                ToMarkupSafeCaption(link.Label)));
        }

        return string.Join(WeChatAdapterConstants.TextLineBreak, lines);
    }

    /// <summary>
    /// Replaces each character of a link caption that would break the markup around it with its
    /// full-width look-alike; every other character stays as it is.
    /// </summary>
    /// <param name="caption">Caption of the link, already worded for the recipient.</param>
    /// <returns>The caption safe to put inside the markup of a link.</returns>
    private static string ToMarkupSafeCaption(string caption)
    {
        var safe = new StringBuilder(caption.Length);

        foreach (var character in caption)
        {
            safe.Append(WeChatAdapterConstants.LinkCaptionMarkupLookAlikes.TryGetValue(character, out var lookAlike)
                ? lookAlike
                : character);
        }

        return safe.ToString();
    }

    /// <summary>
    /// Sends one customer service message on behalf of the account of the current tenant. The
    /// adapter makes one attempt: a message the platform refuses — one outside the interaction
    /// window it allows included — is a failed send, not a reason to retry.
    /// </summary>
    /// <param name="openId">OpenID of the recipient.</param>
    /// <param name="message">The request body of the customer service message call, in its wire form.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No message reference (the platform names none), or a failure.</returns>
    private async Task<Result<ChannelMessageRef?>> SendAsync(
        string openId,
        object message,
        CancellationToken cancellationToken)
    {
        var clientResult = await GetClientAsync(cancellationToken);
        if (clientResult.IsFailure)
        {
            _logger.LogError(
                "Cannot send a WeChat message: credentials are unavailable. ChannelUserIdHash={ChannelUserIdHash}.",
                LogMasking.Fingerprint(openId));

            return Result<ChannelMessageRef?>.Failure(clientResult.Error);
        }

        return await clientResult.Value.SendCustomerServiceMessageAsync(openId, message, cancellationToken);
    }

    /// <summary>
    /// Reads the scene of a scanned QR code off an event. A follower who scans gets the scan event
    /// with the bare scene; a user who follows the account by scanning gets the subscribe event
    /// with the scene behind a prefix of the platform.
    /// </summary>
    /// <param name="message">Inbound message of the event type.</param>
    /// <returns>The scene, or null for an event that is not a scan of a QR code with a scene.</returns>
    private static string? ReadScannedScene(WeChatInboundMessage message)
    {
        if (string.Equals(message.Event, WeChatAdapterConstants.EventScan, StringComparison.Ordinal))
        {
            return message.EventKey;
        }

        var eventKey = message.EventKey;

        return string.Equals(message.Event, WeChatAdapterConstants.EventSubscribe, StringComparison.Ordinal)
            && eventKey is not null
            && eventKey.StartsWith(WeChatAdapterConstants.SubscribeScenePrefix, StringComparison.Ordinal)
                ? eventKey[WeChatAdapterConstants.SubscribeScenePrefix.Length..]
                : null;
    }

    /// <summary>
    /// Builds the identity snapshot of a WeChat user. The platform hands an Official Account
    /// nothing of the user but the OpenID — neither a name nor an avatar — so the OpenID is the
    /// display name as well.
    /// </summary>
    /// <param name="openId">OpenID of the user, scoped to the account.</param>
    /// <returns>Channel identity snapshot.</returns>
    private ChannelIdentitySnapshot BuildIdentitySnapshot(string openId) => new()
    {
        // Tenant of the inbound request, taken from the ambient scope the host opened for it —
        // the {tenant} segment of the webhook route. The identity of a channel user belongs to
        // the tenant whose channel received the event.
        TenantId = ChannelTenantContext.CurrentTenantId,
        ChannelType = ChannelTypes.WeChat,
        IsBot = false,
        ChannelUserId = openId,
        DisplayName = openId,
        CapturedAt = _timeProvider.GetUtcNow(),
        AdapterVersion = WeChatAdapterConstants.AdapterVersion
    };

    /// <summary>
    /// Reads the transaction a marked value names: the prefix followed by the identifier and
    /// nothing else.
    /// </summary>
    /// <param name="value">Value as it arrived in the message; null when the message carries none.</param>
    /// <param name="prefix">Marker the value has to start with.</param>
    /// <param name="transactionId">Parsed identifier on success.</param>
    /// <returns>true — the value carries the marker and a well-formed identifier.</returns>
    private static bool TryReadTransactionId(string? value, string prefix, out TransactionId transactionId)
    {
        transactionId = default;

        return value is not null
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && TransactionId.TryParse(value[prefix.Length..], out transactionId);
    }

    /// <summary>
    /// The answer to a message read down to an intent.
    /// </summary>
    /// <param name="result">Inbound result typed by intent.</param>
    /// <returns>Completed successful result.</returns>
    private static Task<Result<ChannelInboundResult>> RecognizedAsync(ChannelInboundResult result) =>
        Task.FromResult(Result<ChannelInboundResult>.Success(result));

    /// <summary>
    /// The answer to a request that carries nothing about a sign-in.
    /// </summary>
    /// <returns>Completed successful result with <see cref="ChannelUnrelatedResult"/>.</returns>
    private static Task<Result<ChannelInboundResult>> UnrelatedAsync() =>
        RecognizedAsync(new ChannelUnrelatedResult());

    /// <summary>
    /// One option of a menu message.
    /// </summary>
    /// <param name="Id">Identifier the platform hands back when the option is pressed.</param>
    /// <param name="Content">Caption of the option, already worded for the recipient.</param>
    private sealed record MenuOption(string Id, string Content);
}
