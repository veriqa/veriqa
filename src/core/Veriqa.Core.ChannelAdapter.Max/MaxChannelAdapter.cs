// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Max.BotClient;
using Max.BotClient.Types;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Max.Configuration;
using Veriqa.Core.ChannelAdapter.Max.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.ChannelAdapter.Max.Services;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Max;

/// <summary>
/// MAX channel adapter (SPEC-003 §11).
/// Handles inbound events, sends confirmation prompts,
/// notifications and manages deep links via the MAX Bot API.
/// </summary>
internal sealed class MaxChannelAdapter : IChannelAdapter, IChannelBotName
{
    /// <summary>
    /// Per-tenant channel client factory (CA-164/CA-166).
    /// The adapter stays a stateless singleton (CA-014): it does NOT capture the client in the constructor,
    /// but obtains a per-tenant client at processing time by <see cref="ChannelTenantContext.CurrentTenantId"/>.
    /// This way the factory's per-tenant cache actually works; self-hosted (tenant=null) — the same default N=1 path.
    /// </summary>
    private readonly IChannelClientFactory _clientFactory;

    /// <summary>
    /// Bot information provider.
    /// </summary>
    private readonly IMaxBotInfoProvider _botInfoProvider;

    /// <summary>
    /// Canonical layer resolver — the standard mechanism by which this in-process adapter obtains its
    /// own configuration. The tenant-credential fields consumed here (webhook secret token, deep link
    /// base URL) are resolved per operation by the ambient tenant, never captured in the constructor —
    /// self-hosted (tenant=null) stays the default N=1 path (1:1 behavior).
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<MaxChannelAdapter> _logger;

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
    /// HTTP client factory for the direct keyboard-clearing edit (attachments: []) — the Max.BotClient
    /// SDK builder cannot emit an empty attachments array, so that one edit is issued as a raw PUT. Resolved
    /// per operation (no captive dependency in this singleton adapter).
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// MAX Bot API address of this installation (<see cref="MaxOptions.ApiBaseUrl"/>) the direct HTTP
    /// calls of the adapter are resolved against — the same address the platform client is built with.
    /// </summary>
    private readonly Uri _apiBaseUri;

    /// <summary>
    /// Clock behind the health-check stamps and the capture timestamps of the adapter.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an instance of <see cref="MaxChannelAdapter"/>.
    /// </summary>
    /// <param name="clientFactory">Per-tenant channel client factory.</param>
    /// <param name="botInfoProvider">Bot information provider.</param>
    /// <param name="resolver">Canonical layer resolver (source of the tenant's credentials).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="promptLocalizer">Channel message localizer.</param>
    /// <param name="messageTemplates">Accessor of the channel message templates of this adapter.</param>
    /// <param name="httpClientFactory">HTTP client factory for the direct keyboard-clearing edit.</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <param name="options">MAX adapter settings (the Bot API address).</param>
    public MaxChannelAdapter(
        IChannelClientFactory clientFactory,
        IMaxBotInfoProvider botInfoProvider,
        IConfigurationResolver resolver,
        ILogger<MaxChannelAdapter> logger,
        IConfirmationPromptLocalizer promptLocalizer,
        IChannelMessageTemplates messageTemplates,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        IOptions<MaxOptions> options)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _botInfoProvider = botInfoProvider ?? throw new ArgumentNullException(nameof(botInfoProvider));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(options);

        _apiBaseUri = new Uri(options.Value.ApiBaseUrl, UriKind.Absolute);
    }

    /// <inheritdoc />
    public string ChannelType => ChannelTypes.Max;

    /// <summary>
    /// MAX Bot API serialization options — ensure correct resolution of Update properties
    /// (snake_case and polymorphic types). Reused as-is from the library.
    /// </summary>
    private static readonly JsonSerializerOptions MaxJsonOptions = global::Max.BotClient.BotClientJsonOptions.Default;

    /// <summary>
    /// Facts MAX declares about itself, the same in both update modes (SPEC-003 §6.1, SPEC-012 §4.4.1): like
    /// Telegram — inline buttons, an editable own message, and a recipient locale in the update's
    /// user_locale field (MAX Bot API, IETF BCP 47). The phone number is asked for with a
    /// <c>request_contact</c> button and is proven by the signature of the shared contact (SPEC-041 L45).
    /// A link to the claim completion form is drawn as a <c>link</c> button (SPEC-041 C76). After a
    /// completed sign-in the chat can be sent a welcome message, a menu or a link to continue with
    /// (SPEC-012 §4.9); the last two are a message with buttons under its text — <c>link</c> and
    /// <c>callback</c> buttons. The polling loop reads every update off its raw body through the same
    /// entry as the webhook, so the signature of a shared contact reaches the phone step in either mode.
    /// Truly immutable sets.
    /// </summary>
    private static readonly ChannelCapabilities MaxCapabilities = new()
    {
        SupportsInChannelConfirmation = true,
        SupportedMessageKinds = new[]
        {
            ChannelMessageKind.PlainText,
            ChannelMessageKind.PhoneNumberRequest,
            ChannelMessageKind.ClaimFormLink,
            ChannelMessageKind.ActionButtons
        }.ToFrozenSet(),
        SupportsMessageUpdate = true,
        DeliversOutcomeNotice = true,
        ProvidesRecipientLocale = true,
        RendersEmoji = true,
        PhoneNumber = new PhoneNumberCapability
        {
            Acquisition = PhoneNumberAcquisition.OnRequest,
            Proof = PhoneNumberProof.Proven
        },
        SupportedCompletionBehaviorActions = new[]
        {
            CompletionBehaviorAction.None,
            CompletionBehaviorAction.WelcomeMessage,
            CompletionBehaviorAction.Menu,
            CompletionBehaviorAction.Redirect
        }.ToFrozenSet(),
        DefaultCompletionBehaviorAction = CompletionBehaviorAction.None
    };

    /// <inheritdoc />
    public ChannelCapabilities Capabilities => MaxCapabilities;

    /// <inheritdoc />
    /// <remarks>
    /// Besides the markers of Veriqa buttons and deep links, a shared contact is claimed: it answers the
    /// phone number request of claim completion and carries no marker of its own.
    /// </remarks>
    public bool OwnsInboundEvent(ChannelInboundRequest request)
    {
        var body = request.Body.Span;

        return CallbackDataPrefixes.ContainsAnyMarker(body)
            || body.IndexOf(MaxAdapterConstants.ContactAttachmentMarker) >= 0;
    }

    /// <summary>
    /// Resolves the current tenant's MAX Bot client: the client is built/cached
    /// by the factory by <c>(tenant, ChannelType)</c> from the tenant's credentials. The tenant is taken from the
    /// request's ambient context (<see cref="ChannelTenantContext.CurrentTenantId"/>); null — the default implicit tenant
    /// (self-hosted N=1, 1:1 behavior). When credentials are missing — graceful <c>Result.Failure</c>, without an exception.
    /// </summary>
    /// <returns>The current tenant's MAX Bot API client, or an error for missing credentials/client.</returns>
    private ValueTask<Result<IBotClient>> ResolveClientAsync(CancellationToken cancellationToken = default)
    {
        var context = ChannelCredentialContext.ForCurrentTenant(ChannelTypes.Max);

        return _clientFactory.GetOrCreateClientAsync<IBotClient>(context, cancellationToken);
    }

    /// <summary>
    /// Resolves the current tenant's MAX credential group (CA-165): the tenant comes from the
    /// request's ambient context (<see cref="ChannelTenantContext.CurrentTenantId"/>); null — the default implicit
    /// tenant (self-hosted N=1, 1:1 behavior). When credentials are missing — a graceful <c>Result.Failure</c>.
    /// </summary>
    /// <returns>The current tenant's MAX credentials, or an error when no layer supplies them.</returns>
    private ValueTask<Result<MaxTenantCredentials>> ResolveCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var context = ChannelCredentialContext.ForCurrentTenant(ChannelTypes.Max);

        return _resolver.ResolveCredentialsAsync(MaxConfigKeys.Credentials, context, _logger, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<ChannelInboundResult>> ProcessInboundEventAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
        => ProcessRawUpdateAsync(request.Body, cancellationToken);

    /// <summary>
    /// Processes one MAX update from its raw body — the one reception point of both update modes: the
    /// webhook hands over the body of the request, the polling loop each element of the <c>updates</c>
    /// array of its own <c>GET /updates</c>. The wire format is the adapter's business, and the raw body is
    /// read where the client library model falls short: a shared contact, whose message the model leaves
    /// empty and whose signature it has no field for, and the message a pressed button belongs to.
    /// </summary>
    /// <param name="body">Raw body of one update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of processing the incoming event.</returns>
    internal async Task<Result<ChannelInboundResult>> ProcessRawUpdateAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        Update? update;
        try
        {
            update = JsonSerializer.Deserialize<Update>(body.Span, MaxJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize the MAX Update");
            return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult());
        }

        if (update is null)
        {
            return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult());
        }

        // Handle the callback (inline button press)
        if (update.UpdateType is UpdateType.MessageCallback && update.Callback is not null)
        {
            return await ProcessCallbackAsync(update, ReadCallbackMessageId(body), cancellationToken);
        }

        // Handle the bot start event (deep link navigation)
        if (update.UpdateType is UpdateType.BotStarted)
        {
            return await ProcessBotStartedAsync(update, cancellationToken);
        }

        // Handle a shared contact — the answer to the phone number request (SPEC-041 L44), read off the
        // raw body with its signature, where the rules of a shared contact live.
        if (update.UpdateType is UpdateType.MessageCreated
            && MaxSharedContact.TryRead(body, MaxJsonOptions, out var sharedContact))
        {
            return await ProcessContactMessageAsync(update, sharedContact, cancellationToken);
        }

        // Other update types are unrelated to authentication
        _logger.LogDebug(
            "Received an irrelevant MAX update, UpdateType = {UpdateType}",
            update.UpdateType);

        return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult { RawEvent = update });
    }

    /// <summary>
    /// Reads the id of the message a pressed button belongs to off the raw body of a
    /// <c>message_callback</c> update (<c>message.body.mid</c>). The client library model has no public
    /// setter for it, so the deserialized update never carries it.
    /// </summary>
    /// <param name="body">Raw update body.</param>
    /// <returns>The message id; null — the update names no message, or names it in an unreadable form.</returns>
    private static string? ReadCallbackMessageId(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            return root.ValueKind is JsonValueKind.Object
                && root.TryGetProperty(MaxAdapterConstants.UpdateMessageField, out var message)
                && message.ValueKind is JsonValueKind.Object
                && message.TryGetProperty(MaxAdapterConstants.MessageBodyField, out var messageBody)
                && messageBody.ValueKind is JsonValueKind.Object
                && messageBody.TryGetProperty(MaxAdapterConstants.MessageBodyIdField, out var messageId)
                && messageId.ValueKind is JsonValueKind.String
                    ? messageId.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<Result<ChannelMessageRef?>> SendConfirmationPromptAsync(
        TransactionId transactionId,
        string channelUserId,
        ConfirmationPromptContext promptContext,
        CancellationToken cancellationToken = default)
    {
        // The recipient locale travels with the context and nowhere else (SPEC-017 §7.2).
        var recipientLocale = promptContext.RecipientLocale;

        try
        {
            // Parse the user identifier
            if (!long.TryParse(channelUserId, out var userId))
            {
                _logger.LogError(
                    "Cannot convert channelUserId to a MAX userId: {ChannelUserIdHash}",
                    LogMasking.Fingerprint(channelUserId));

                return Result<ChannelMessageRef?>.Failure(
                    ChannelAdapterErrorCodes.MessageSendFailed,
                    "Invalid channel user identifier");
            }

            // Resolve the tenant client (per-tenant, CA-164); on missing credentials — graceful Failure.
            var clientResult = await ResolveClientAsync(cancellationToken);
            if (clientResult.IsFailure)
            {
                _logger.LogError(
                    "Failed to resolve the tenant MAX client to send the confirmation prompt. Error: {ErrorCode}",
                    clientResult.Error.Code);

                return Result<ChannelMessageRef?>.Failure(clientResult.Error);
            }

            var botClient = clientResult.Value;

            // Render the text from the confirmation context (SPEC-017 §7.2) in the recipient's language;
            // on a context without details — the default text (ICC-042), also localized by the passed
            // locale (ICC-050). The template is resolved by the core, which takes the address and the
            // owners itself — the in-channel surface, this channel and the levels the transaction states —
            // so a variant declared by a tenant, an application or a ui_config record answers instead of
            // the shipped one (SPEC-036 TPL-116); a template it cannot resolve sends nothing.
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

            // Build the payload for the confirm and decline buttons
            var transactionIdStr = transactionId.ToString();
            var confirmPayload = CallbackDataPrefixes.Confirm + transactionIdStr;
            var declinePayload = CallbackDataPrefixes.Decline + transactionIdStr;

            // Build the message with an inline keyboard. The button captions are Natural Keys localized
            // into the recipient's language (ICC-050); a missing translation falls back to the key's
            // base text.
            var confirmButtonText = _promptLocalizer.ResolveOrBaseText(
                MaxAdapterConstants.ConfirmButtonText, recipientLocale);
            var declineButtonText = _promptLocalizer.ResolveOrBaseText(
                MaxAdapterConstants.DeclineButtonText, recipientLocale);
            var message = new Message(messageText)
                .WithKeyboard(kb => kb
                    .AddRow()
                    .AddCallbackButton(confirmButtonText, confirmPayload)
                    .AddCallbackButton(declineButtonText, declinePayload));

            // Send the message to the user
            var sentMessage = await botClient.SendMessage(
                id: userId,
                message: message,
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Confirmation prompt sent to user {ChannelUserIdHash}, transaction {TransactionId}",
                LogMasking.Fingerprint(channelUserId),
                transactionIdStr);

            // Report the coordinates back to the core, which stores them. MAX addresses an edit by
            // the message Mid alone; the user id is reported as ChatId for completeness. A missing
            // Mid means there is nothing addressable — a success without a reference.
            var sentMid = sentMessage?.Mid;
            if (string.IsNullOrEmpty(sentMid))
            {
                return Result<ChannelMessageRef?>.Success(null);
            }

            return Result<ChannelMessageRef?>.Success(new ChannelMessageRef
            {
                ChatId = userId.ToString(CultureInfo.InvariantCulture),
                MessageId = sentMid
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error sending the confirmation prompt to user {ChannelUserIdHash}, transaction {TransactionId}",
                LogMasking.Fingerprint(channelUserId),
                transactionId.ToString());

            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "Error sending the confirmation prompt to MAX");
        }
    }

    /// <inheritdoc />
    public Task<Result<ChannelMessageRef?>> SendMessageAsync(
        ChannelMessage message,
        CancellationToken cancellationToken = default)
    {
        // A phone number request carries a keyboard: the button that shares the contact and a callback
        // button of the secondary action, whose payload names the transaction (SPEC-041 L73).
        if (message.PhoneNumberRequest is { } phoneRequest)
        {
            var secondaryPayload = PhoneStepButtonPayload.Of(phoneRequest.SecondaryAction, phoneRequest.TransactionId);
            var request = new Message(message.Text)
                .WithKeyboard(kb => kb
                    .AddRow()
                    .AddRequestContactButton(phoneRequest.ShareButtonLabel)
                    .AddRow()
                    .AddCallbackButton(phoneRequest.SecondaryButtonLabel, secondaryPayload));

            return SendAsync(message.ChannelUserId, request, cancellationToken);
        }

        // A link to the claim completion form is one link button under the text (SPEC-041 C76): the
        // messenger opens the page in its own or the system browser, and the bot never sees the press.
        if (message.ClaimFormLink is { } formLink)
        {
            var link = new Message(message.Text)
                .WithKeyboard(kb => kb
                    .AddRow()
                    .AddLinkButton(formLink.ButtonLabel, formLink.Url));

            return SendAsync(message.ChannelUserId, link, cancellationToken);
        }

        // Buttons of a post-sign-in action are a keyboard under the text, one button in a row
        // (SPEC-012 §4.9). A message none of whose buttons can be drawn is not sent at all: its text
        // only introduces the buttons.
        if (message.ActionButtons is { } actionButtons)
        {
            var buttons = SelectDrawableActionButtons(actionButtons);
            if (buttons.Count == 0)
            {
                return Task.FromResult(Result<ChannelMessageRef?>.Failure(
                    ChannelAdapterErrorCodes.MessageSendFailed,
                    "None of the action buttons of the message can be drawn in MAX"));
            }

            var withButtons = new Message(message.Text)
                .WithKeyboard(kb =>
                {
                    foreach (var button in buttons)
                    {
                        // The same test the selection above draws a link by: an empty address is no link.
                        if (!string.IsNullOrEmpty(button.Url))
                        {
                            kb.AddRow().AddLinkButton(button.Label, button.Url);
                        }
                        else
                        {
                            // The payload goes to the platform as is, the ownership marker included. The
                            // MAX Bot API documentation names no limit for the payload of a callback
                            // button today, so nothing is checked here beforehand: the platform is the
                            // judge, and its refusal fails the send.
                            kb.AddRow().AddCallbackButton(button.Label, button.CallbackData!);
                        }
                    }
                });

            return SendAsync(message.ChannelUserId, withButtons, cancellationToken);
        }

        return SendAsync(message.ChannelUserId, new Message(message.Text), cancellationToken);
    }

    /// <summary>
    /// Picks the buttons of a message that can be drawn, in the order they are listed. A button that is
    /// malformed — neither a link nor a callback, or both — and a link button whose address exceeds the
    /// length the platform names are left out with a warning; the rest are drawn.
    /// </summary>
    /// <param name="content">Buttons of the message.</param>
    /// <returns>Buttons to draw; empty when none can be drawn.</returns>
    private List<ChannelActionButton> SelectDrawableActionButtons(ActionButtonsContent content)
    {
        var drawable = new List<ChannelActionButton>(content.Buttons.Count);

        foreach (var button in content.Buttons)
        {
            var hasUrl = !string.IsNullOrEmpty(button.Url);
            var hasCallbackData = !string.IsNullOrEmpty(button.CallbackData);

            if (hasUrl == hasCallbackData)
            {
                _logger.LogWarning(
                    "An action button is not drawn in {ChannelType}: it must carry either a link or callback data, and only one of them",
                    ChannelType);

                continue;
            }

            // The address comes from the operator's settings and is not logged.
            if (hasUrl && button.Url!.Length > MaxAdapterConstants.LinkButtonUrlMaxLength)
            {
                _logger.LogWarning(
                    "An action button is not drawn in {ChannelType}: its link is {UrlLength} characters long, the platform limit is {MaxLength}",
                    ChannelType,
                    button.Url.Length,
                    MaxAdapterConstants.LinkButtonUrlMaxLength);

                continue;
            }

            drawable.Add(button);
        }

        return drawable;
    }

    /// <summary>
    /// Sends a message to the user and reports back the coordinates of what was sent.
    /// </summary>
    /// <param name="channelUserId">Recipient within the channel.</param>
    /// <param name="outgoing">Message to send — its text and keyboard.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reference to the sent message, or a failure.</returns>
    private async Task<Result<ChannelMessageRef?>> SendAsync(
        string channelUserId,
        Message outgoing,
        CancellationToken cancellationToken)
    {
        try
        {
            // Parse the user identifier
            if (!long.TryParse(channelUserId, out var userId))
            {
                _logger.LogError(
                    "Cannot convert channelUserId to a MAX userId: {ChannelUserIdHash}",
                    LogMasking.Fingerprint(channelUserId));

                return Result<ChannelMessageRef?>.Failure(
                    ChannelAdapterErrorCodes.MessageSendFailed,
                    "Invalid channel user identifier");
            }

            // Resolve the tenant client (per-tenant, CA-164); on missing credentials — graceful Failure.
            var clientResult = await ResolveClientAsync(cancellationToken);
            if (clientResult.IsFailure)
            {
                _logger.LogError(
                    "Failed to resolve the tenant MAX client to send the message. Error: {ErrorCode}",
                    clientResult.Error.Code);

                return Result<ChannelMessageRef?>.Failure(clientResult.Error);
            }

            // Send the message
            var sentMessage = await clientResult.Value.SendMessage(
                id: userId,
                message: outgoing,
                cancellationToken: cancellationToken);

            _logger.LogDebug(
                "Message sent to user {ChannelUserId}",
                channelUserId);

            var sentMid = sentMessage?.Mid;
            if (string.IsNullOrEmpty(sentMid))
            {
                return Result<ChannelMessageRef?>.Success(null);
            }

            return Result<ChannelMessageRef?>.Success(new ChannelMessageRef
            {
                ChatId = userId.ToString(CultureInfo.InvariantCulture),
                MessageId = sentMid
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error sending the message to user {ChannelUserIdHash}",
                LogMasking.Fingerprint(channelUserId));

            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "Error sending the message to MAX");
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ValidateWebhookAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Extract the secret token from the request header
            request.Headers.TryGetValue(MaxAdapterConstants.WebhookSecretHeader, out var headerValue);

            // Resolve the tenant's webhook secret via the seam (one resolve per operation).
            // A resolution failure means the secret is unavailable ⇒ validation is impossible ⇒ reject.
            var credentialsResult = await ResolveCredentialsAsync(cancellationToken);
            if (credentialsResult.IsFailure)
            {
                _logger.LogError(
                    "MAX webhook secret is unavailable (credentials could not be resolved). Request rejected. " +
                    "WebhookSecretToken must be set for header {Header}",
                    MaxAdapterConstants.WebhookSecretHeader);

                return Result<bool>.Success(false);
            }

            var configuredSecret = credentialsResult.Value.WebhookSecretToken;

            // If the secret is not configured — reject the request to protect the webhook
            if (string.IsNullOrEmpty(configuredSecret))
            {
                _logger.LogError(
                    "MAX webhook secret is not configured. Request rejected. " +
                    "WebhookSecretToken must be set for header {Header}",
                    MaxAdapterConstants.WebhookSecretHeader);

                return Result<bool>.Success(false);
            }

            // If the secret is configured but the header is missing — an invalid request
            if (string.IsNullOrEmpty(headerValue))
            {
                _logger.LogWarning(
                    "Header {Header} is missing while the MAX webhook secret is configured",
                    MaxAdapterConstants.WebhookSecretHeader);

                return Result<bool>.Success(false);
            }

            // Compare fixed-length SHA-256 hashes (32 bytes) via FixedTimeEquals (CA-033):
            // the comparison is always constant-length, which eliminates the secret-length leak through an early
            // return on length mismatch and preserves constant time.
            Span<byte> expectedHash = stackalloc byte[32];
            Span<byte> actualHash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret), expectedHash);
            SHA256.HashData(Encoding.UTF8.GetBytes(headerValue), actualHash);

            var isValid = CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);

            if (!isValid)
            {
                _logger.LogWarning("Invalid MAX webhook secret token");
            }

            return Result<bool>.Success(isValid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MAX webhook request validation error");

            return Result<bool>.Failure(
                ChannelAdapterErrorCodes.WebhookValidationFailed,
                "MAX webhook request validation error");
        }
    }

    /// <inheritdoc />
    public async Task<Result<ChannelHealthStatus>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        // Measure the MAX Bot API response time
        var startTime = _timeProvider.GetUtcNow();

        // The probe runs outside any request, so no host path has stated whose credentials it should
        // read — and the installation's own (the default implicit tenant) is the answer it wants: a
        // health endpoint reports the state of THIS deployment, not of one of its tenants. That is
        // said explicitly rather than by omission, so that the read is not mistaken for a request
        // path that forgot to state its tenant (CA-164).
        using var tenantScope = ChannelTenantContext.BeginScope(tenantId: null);

        // Resolve the tenant client; on missing credentials — unhealthy status without an exception.
        var clientResult = await ResolveClientAsync(cancellationToken);
        if (clientResult.IsFailure)
        {
            return Result<ChannelHealthStatus>.Success(
                new ChannelHealthStatus(
                    IsHealthy: false,
                    ChannelType: ChannelTypes.Max,
                    ResponseTime: _timeProvider.GetUtcNow() - startTime,
                    Details: clientResult.Error.Code,
                    CheckedAt: _timeProvider.GetUtcNow()));
        }

        try
        {
            // Call GetMe to check API availability
            var botInfo = await clientResult.Value.GetMe(cancellationToken);
            var elapsed = _timeProvider.GetUtcNow() - startTime;

            _logger.LogDebug(
                "MAX health check: bot {BotUsername}, response time {ResponseTimeMs} ms",
                botInfo.Username,
                elapsed.TotalMilliseconds);

            return Result<ChannelHealthStatus>.Success(
                new ChannelHealthStatus(
                    IsHealthy: true,
                    ChannelType: ChannelTypes.Max,
                    ResponseTime: elapsed,
                    Details: null,
                    CheckedAt: _timeProvider.GetUtcNow()));
        }
        catch (Exception ex)
        {
            var elapsed = _timeProvider.GetUtcNow() - startTime;

            _logger.LogError(
                ex,
                "MAX health check: error, response time {ResponseTimeMs} ms",
                elapsed.TotalMilliseconds);

            return Result<ChannelHealthStatus>.Success(
                new ChannelHealthStatus(
                    IsHealthy: false,
                    ChannelType: ChannelTypes.Max,
                    ResponseTime: elapsed,
                    Details: ex.Message,
                    CheckedAt: _timeProvider.GetUtcNow()));
        }
    }

    /// <inheritdoc />
    public async Task<Result<string>> GetDeepLinkAsync(
        DeepLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            // Resolve the tenant's deep link base URL via the seam (one resolve per operation).
            var credentialsResult = await ResolveCredentialsAsync(cancellationToken);
            if (credentialsResult.IsFailure)
            {
                _logger.LogError(
                    "Failed to resolve MAX credentials to build the deep link. Error: {ErrorCode}",
                    credentialsResult.Error.Code);

                return Result<string>.Failure(credentialsResult.Error);
            }

            // Get the bot username
            var botUsername = await _botInfoProvider.GetBotUsernameAsync(cancellationToken);

            // Build the deep link URL for MAX
            // Format: {DeepLinkBaseUrl}{botUsername}?start={prefix}{transactionId}
            var deepLink = string.Concat(
                credentialsResult.Value.DeepLinkBaseUrl,
                botUsername,
                MaxAdapterConstants.DeepLinkStartParam,
                MaxAdapterConstants.DeepLinkPrefix,
                request.TransactionId.ToString());

            _logger.LogDebug(
                "MAX deep link generated for transaction {TransactionId}",
                request.TransactionId.ToString());

            return Result<string>.Success(deepLink);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error generating the MAX deep link for transaction {TransactionId}",
                request.TransactionId.ToString());

            return Result<string>.Failure(
                ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                "Error generating the deep link for MAX");
        }
    }

    /// <inheritdoc />
    public ValueTask<string?> GetBotNameAsync(CancellationToken cancellationToken = default) =>
        // The method names the chat the way the user sees it: the bot username behind an at-sign.
        // The name only decorates a text, so a failure to obtain it — unresolved credentials, a
        // refused Bot API call — is answered with no name and never thrown: the text then goes
        // without the name. A cancelled request is not such a failure: the cancellation goes on to
        // the caller.
        ChannelBotNameReader.ReadAsync(
            async token => await _botInfoProvider.GetBotUsernameAsync(token),
            MaxAdapterConstants.BotUsernamePrefix,
            ex => _logger.LogWarning(ex, "Failed to obtain the MAX bot username for the chat name"),
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<bool>> ReportOutcomeAsync(
        TransactionOutcomeNotice notice,
        CancellationToken cancellationToken = default)
    {
        // MAX decides for itself how to show the reported state — the end of the transaction or the
        // claims completion it is waiting on; the core only states the intent.
        // The two obligations of a live press are independent and are discharged separately: the
        // callback is owed an answer whenever one was pressed, while the message the button belongs
        // to is editable only when the event carried its id. Order of addressing: token message id →
        // stored prompt reference (TTL and the id-less press alike) → a new message.
        //
        // The intent picks how far down that ladder the receipt is allowed to start. ReplacePrompt
        // — the shipped value — keeps every step. NewMessage takes neither: the receipt arrives as a
        // new message, and the question is edited only to take its buttons off, so it stays in the
        // conversation with its own text as the record of what the outcome answered, but without an
        // offer the user can still press. Which message that is follows the same order of addressing
        // — token message id, then the stored prompt reference — and the first addressable one wins:
        // the second is not tried. This holds for the whole operation — a confirmation, a decline and
        // an expiry alike — because the ladder below is the only path any of them takes.
        //
        // The keyboard lives in the `attachments` of the message, and an edit is a PUT /messages
        // body. The vendor describes what such a body does to attachments — an empty array drops
        // them all — but not what it does to the text when `text` is omitted (dev.max.ru, PUT
        // /messages and NewMessageBody, read 2026-09-21). So the edit that takes the buttons off
        // leans on nothing undescribed: it reads the question back and sends both fields
        // explicitly — the question's own text and an empty `attachments`.
        //
        // A terminal notice that names a phone number request skips both replacing steps whatever the
        // intent says: the request is the last message in the chat, and a receipt written over the
        // question would sit above it, out of order. The question is left as it is — by then the
        // state of claims completion stands in its place and it has no buttons to take off — and
        // the receipt arrives as a new message, below the request.
        var intendsNewMessage = notice.DisplayIntent is not OutcomeNoticeDisplayIntent.ReplacePrompt;
        var endsPhoneRequest = notice.IsTerminal && notice.PhoneRequestMessage is not null;
        var mayReplacePrompt = !intendsNewMessage && !endsPhoneRequest;

        // The end of the sign-in ends the phone number request too (SPEC-041 L73): its buttons are an
        // attachment of the request message and come off by the reference the notice carries. This is
        // independent of the intent and of how the receipt is shown, and — like taking the buttons off
        // the question — stays out of the result of this operation. The state of claims completion
        // leaves the request alone: the step it belongs to is still running.
        if (notice.IsTerminal && notice.PhoneRequestMessage is { } phoneRequestMessage)
        {
            var phoneKeyboardResult = await RemovePromptKeyboardAsync(
                phoneRequestMessage.MessageId, cancellationToken);
            if (phoneKeyboardResult.IsFailure)
            {
                _logger.LogWarning(
                    "Failed to take the keyboard off the MAX phone number request. Error: {ErrorCode}",
                    phoneKeyboardResult.Error.Code);
            }
        }

        if (TryUnpackInboundToken(notice.InboundToken, out var callbackId, out var tokenMessageId))
        {
            // The loading indicator must go even if the edit later fails, or never happens at all —
            // and whatever the intent says. Stopping the indicator is not showing the receipt, so
            // the answer stays outside the intent check below.
            var answerResult = await AnswerCallbackAsync(callbackId, cancellationToken);
            if (answerResult.IsFailure)
            {
                _logger.LogWarning(
                    "Failed to answer the MAX callback. Error: {ErrorCode}",
                    answerResult.Error.Code);
            }

            if (mayReplacePrompt && tokenMessageId is not null)
            {
                var editResult = await EditMessageTextAsync(tokenMessageId, notice.StatusText, cancellationToken);

                // The notice was shown (Success(true)) or could not be delivered — the "the channel
                // deliberately showed nothing" state (Success(false)) does not arise on this path.
                return ToOutcomeResult(editResult);
            }
        }

        if (mayReplacePrompt && notice.PromptMessage is not null)
        {
            var promptEditResult = await EditMessageTextAsync(
                notice.PromptMessage.MessageId, notice.StatusText, cancellationToken);

            return ToOutcomeResult(promptEditResult);
        }

        if (intendsNewMessage)
        {
            // NewMessage: the question keeps its text and loses its keyboard. Skipped entirely when
            // neither address is known (an id-less press without a stored prompt, a restarted
            // process). Taking the buttons off is not showing the receipt, so its outcome stays out
            // of the result of this operation: a question that could not be read back or edited
            // leaves the user with a stale button, which is a worse conversation but not an
            // undelivered notice.
            var promptToDisarm = tokenMessageId ?? notice.PromptMessage?.MessageId;
            if (promptToDisarm is not null)
            {
                var keyboardResult = await RemovePromptKeyboardAsync(promptToDisarm, cancellationToken);
                if (keyboardResult.IsFailure)
                {
                    _logger.LogWarning(
                        "Failed to take the inline keyboard off the MAX prompt. Error: {ErrorCode}",
                        keyboardResult.Error.Code);
                }
            }
        }

        var sendResult = await SendAsync(notice.ChannelUserId, new Message(notice.StatusText), cancellationToken);
        return sendResult.IsSuccess
            ? Result<bool>.Success(true)
            : Result<bool>.Failure(sendResult.Error);
    }

    /// <summary>
    /// Lifts the result of a helper that only reports success into the outcome-notice contract
    /// (SPEC-003 §6.2), where success additionally states whether anything was shown to the user.
    /// </summary>
    /// <param name="result">Result of the helper call.</param>
    /// <returns><c>Success(true)</c> — the notice was shown; otherwise the helper's error.</returns>
    private static Result<bool> ToOutcomeResult(Result result) =>
        result.IsSuccess
            ? Result<bool>.Success(true)
            : Result<bool>.Failure(result.Error);

    /// <summary>
    /// Packs the callback id and the id of the message the button belongs to into the opaque inbound
    /// token. The layout is the adapter's own business — the core never parses it.
    /// </summary>
    /// <param name="callbackId">Callback identifier (ephemeral, exists only in this event).</param>
    /// <param name="messageId">Identifier of the original message (null — the event carried none).</param>
    /// <returns>The token, or null when there is no callback to answer.</returns>
    /// <remarks>
    /// The callback identifier is what makes the token exist: it arrives with every button press and
    /// is the only way to stop the loading indicator. The message id is optional — the callback may
    /// arrive without the source message — so its absence empties a token slot instead of dropping
    /// the token, which would cost the user an answer that is always due.
    /// </remarks>
    private static ChannelInboundToken? PackInboundToken(string? callbackId, string? messageId)
    {
        if (string.IsNullOrEmpty(callbackId))
        {
            return null;
        }

        return new ChannelInboundToken(string.Join(
            MaxAdapterConstants.InboundTokenSeparator, callbackId, messageId ?? string.Empty));
    }

    /// <summary>
    /// Unpacks a token produced by <see cref="PackInboundToken"/>. Survives a foreign or malformed
    /// value without throwing: the caller then falls back to the other addressing options.
    /// </summary>
    /// <param name="token">Opaque token handed back by the core (null — no live interaction).</param>
    /// <param name="callbackId">Callback identifier on success.</param>
    /// <param name="messageId">Id of the original message, or null when the event carried none.</param>
    /// <returns>true — the token was produced by this adapter and names a callback to answer.</returns>
    private static bool TryUnpackInboundToken(
        ChannelInboundToken? token,
        out string callbackId,
        out string? messageId)
    {
        callbackId = string.Empty;
        messageId = null;

        var value = token?.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split(MaxAdapterConstants.InboundTokenSeparator);
        if (parts.Length != MaxAdapterConstants.InboundTokenPartCount
            || string.IsNullOrEmpty(parts[0]))
        {
            return false;
        }

        callbackId = parts[0];

        if (!string.IsNullOrEmpty(parts[1]))
        {
            messageId = parts[1];
        }

        return true;
    }

    /// <summary>
    /// Answers a callback, removing the loading indicator.
    /// </summary>
    /// <param name="callbackQueryId">Callback identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the call succeeded.</returns>
    private async Task<Result> AnswerCallbackAsync(
        string callbackQueryId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the tenant client (per-tenant, CA-164); on missing credentials — graceful Failure.
            var clientResult = await ResolveClientAsync(cancellationToken);
            if (clientResult.IsFailure)
            {
                return Result.Failure(clientResult.Error);
            }

            // Answer the callback query, removing the loading indicator. The API requires either a
            // message or a notification; an empty notification is the silent answer.
            await clientResult.Value.AnswerCallback(
                callbackId: callbackQueryId,
                message: null,
                notification: MaxAdapterConstants.SilentCallbackNotification,
                cancellationToken: cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to answer the MAX callback. CallbackId: {CallbackId}",
                callbackQueryId);

            return Result.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "AnswerCallback error in MAX");
        }
    }

    /// <summary>
    /// Takes the inline keyboard off the bot's own question while keeping its text: reads the message
    /// back and re-sends its current text together with an empty <c>attachments</c>.
    /// </summary>
    /// <param name="messageId">Identifier of the question message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the keyboard was taken off; on a failure the question is left untouched.</returns>
    /// <remarks>
    /// Re-sending the text leaves the question as it was because in this delivery the question is sent
    /// as plain text, without a <c>format</c>: the text read back is the text that was sent. A question
    /// sent with a format would need its markup carried over too, and this edit revisited.
    /// </remarks>
    private async Task<Result> RemovePromptKeyboardAsync(
        string messageId,
        CancellationToken cancellationToken)
    {
        string? promptText;
        try
        {
            // Resolve the tenant client (per-tenant, CA-164); on missing credentials — graceful Failure.
            var clientResult = await ResolveClientAsync(cancellationToken);
            if (clientResult.IsFailure)
            {
                return Result.Failure(clientResult.Error);
            }

            var promptMessage = await clientResult.Value.GetMessage(messageId, cancellationToken);
            promptText = promptMessage?.Text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read back the MAX prompt. MessageId: {MessageId}",
                messageId);

            return Result.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "GetMessage error in MAX");
        }

        // Without the text there is nothing to re-send that would keep the question as it is, and an
        // edit carrying anything else would rewrite it — the question is left alone instead.
        if (string.IsNullOrEmpty(promptText))
        {
            return Result.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "The MAX prompt has no text to keep");
        }

        return await EditMessageTextAsync(messageId, promptText, cancellationToken);
    }

    /// <summary>
    /// Replaces the text of the bot's own message and removes its inline keyboard (CA-142).
    /// </summary>
    /// <param name="messageId">Message identifier (MAX addresses an edit by the Mid alone).</param>
    /// <param name="newText">New message text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the edit succeeded.</returns>
    private async Task<Result> EditMessageTextAsync(
        string messageId,
        string newText,
        CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the tenant credentials (per-tenant, CA-165) for the bot token; on missing — graceful Failure.
            var credentialsResult = await ResolveCredentialsAsync(cancellationToken);
            if (credentialsResult.IsFailure)
            {
                return Result.Failure(credentialsResult.Error);
            }

            // CA-142: replace the text AND remove the confirm/decline inline keyboard.
            // The MAX Bot API removes attachments only when the edit body sends an explicit empty array
            // ("attachments": []); "attachments": null / omitted leaves the existing keyboard in place
            // (PUT /messages, dev.max.ru). The Max.BotClient SDK builder cannot emit an empty array —
            // Message.ToMessageBody() collapses an empty builder to "attachments": null and ClearKeyboard()
            // only nulls the local field — so this one edit is issued as a raw PUT with "attachments": [].
            // Unlike Telegram, whose editMessageText clears the keyboard via replyMarkup: null.
            // The method path is absolute and is resolved against the installation's address exactly as
            // the SDK resolves its own, so both kinds of call reach the same host.
            var requestUri = new Uri(
                _apiBaseUri,
                $"{MaxAdapterConstants.EditMessagePath}"
                + $"?{MaxAdapterConstants.MessageIdQueryParam}={Uri.EscapeDataString(messageId)}");

            var payloadJson = JsonSerializer.Serialize(new { text = newText, attachments = Array.Empty<object?>() });

            using var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, MaxAdapterConstants.JsonMediaType),
            };

            // MAX auth: the raw bot token in the Authorization header (same as the SDK), not a query parameter.
            request.Headers.TryAddWithoutValidation(
                MaxAdapterConstants.AuthorizationHeaderName,
                credentialsResult.Value.BotToken);

            var httpClient = _httpClientFactory.CreateClient(MaxAdapterConstants.HttpClientName);
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to edit the MAX message (clear keyboard). MessageId: {MessageId}, Status: {StatusCode}",
                    messageId,
                    (int)response.StatusCode);

                return Result.Failure(
                    ChannelAdapterErrorCodes.MessageSendFailed,
                    "EditMessage error in MAX");
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to edit the MAX message. MessageId: {MessageId}",
                messageId);

            return Result.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "EditMessage error in MAX");
        }
    }

    /// <summary>
    /// Handles a callback event (inline button press).
    /// </summary>
    /// <param name="update">MAX update of type MessageCallback.</param>
    /// <param name="messageId">Id of the message the pressed button belongs to; null — the update names none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Processing result.</returns>
    private Task<Result<ChannelInboundResult>> ProcessCallbackAsync(
        Update update,
        string? messageId,
        CancellationToken cancellationToken)
    {
        var callback = update.Callback!;

        // Check that a payload is present
        var payload = callback.Payload;
        if (string.IsNullOrEmpty(payload))
        {
            return Task.FromResult(Result<ChannelInboundResult>.Success(
                new ChannelUnrelatedResult { RawEvent = update }));
        }

        // Check that user information is present
        var user = callback.User;
        if (user is null || user.UserId is null)
        {
            return Task.FromResult(Result<ChannelInboundResult>.Failure(
                ChannelAdapterErrorCodes.IdentityExtractionFailed,
                "User info is missing in the MAX callback"));
        }

        // Everything needed to address the interaction later goes into one opaque token: the core
        // carries it back untouched, so the platform terms stay inside this adapter. A MAX edit is
        // addressed by the message Mid alone, so the chat is not packed at all.
        var inboundToken = PackInboundToken(callback.CallbackId, messageId);

        // Determine the intent by the payload prefix
        if (payload.StartsWith(CallbackDataPrefixes.Confirm, StringComparison.Ordinal))
        {
            // Authentication confirmation
            var transactionIdStr = payload[CallbackDataPrefixes.Confirm.Length..];
            var identity = ExtractIdentitySnapshotFromUser(user, update.UserLocale, _timeProvider.GetUtcNow());

            _logger.LogInformation(
                "MAX confirmation received from user {UserIdHash}, transaction {TransactionId}",
                LogMasking.Fingerprint(user.UserId?.ToString(CultureInfo.InvariantCulture)),
                transactionIdStr);

            if (!TryParseEventTransactionId(transactionIdStr, out var confirmTransactionId))
            {
                return Task.FromResult(Result<ChannelInboundResult>.Success(
                    new ChannelUnrelatedResult { RawEvent = update }));
            }

            return Task.FromResult(Result<ChannelInboundResult>.Success(new ChannelAuthConfirmResult
            {
                TransactionId = confirmTransactionId,
                Identity = identity,
                InboundToken = inboundToken,
                RawEvent = update
            }));
        }

        if (payload.StartsWith(CallbackDataPrefixes.Decline, StringComparison.Ordinal))
        {
            // Authentication decline
            var transactionIdStr = payload[CallbackDataPrefixes.Decline.Length..];
            var identity = ExtractIdentitySnapshotFromUser(user, update.UserLocale, _timeProvider.GetUtcNow());

            _logger.LogInformation(
                "MAX decline received from user {UserIdHash}, transaction {TransactionId}",
                LogMasking.Fingerprint(user.UserId?.ToString(CultureInfo.InvariantCulture)),
                transactionIdStr);

            if (!TryParseEventTransactionId(transactionIdStr, out var declineTransactionId))
            {
                return Task.FromResult(Result<ChannelInboundResult>.Success(
                    new ChannelUnrelatedResult { RawEvent = update }));
            }

            return Task.FromResult(Result<ChannelInboundResult>.Success(new ChannelAuthDeclineResult
            {
                TransactionId = declineTransactionId,
                Identity = identity,
                InboundToken = inboundToken,
                RawEvent = update
            }));
        }

        if (PhoneStepButtonPayload.TryRead(payload, out var phoneStepAction, out var phoneStepTransactionIdStr))
        {
            return ProcessPhoneStepActionAsync(
                update, user, phoneStepAction, phoneStepTransactionIdStr, cancellationToken);
        }

        // A press of a callback item of the post-sign-in menu (SPEC-012 §4.9). The marker alone, with
        // no value after it, names no item: such a payload is left unrelated and is not answered.
        if (payload.StartsWith(CallbackDataPrefixes.MenuItem, StringComparison.Ordinal)
            && payload.Length > CallbackDataPrefixes.MenuItem.Length)
        {
            return ProcessMenuItemPressAsync(
                update, user, payload[CallbackDataPrefixes.MenuItem.Length..], cancellationToken);
        }

        // Unknown payload — an unrelated event
        return Task.FromResult(Result<ChannelInboundResult>.Success(
            new ChannelUnrelatedResult { RawEvent = update }));
    }

    /// <summary>
    /// Handles a press of a callback item of the post-sign-in menu: answers the press and reports the
    /// value of the item with the identity of the user who pressed it.
    /// </summary>
    /// <param name="update">MAX update of type MessageCallback.</param>
    /// <param name="user">The user who pressed the button.</param>
    /// <param name="value">Value of the item — what follows the marker in the payload; not empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Processing result.</returns>
    private async Task<Result<ChannelInboundResult>> ProcessMenuItemPressAsync(
        Update update,
        User user,
        string value,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "MAX menu item press received from user {UserIdHash}",
            LogMasking.Fingerprint(user.UserId?.ToString(CultureInfo.InvariantCulture)));

        // The press is answered right here, as a press of the phone step is: the result carries no
        // interaction token for the core to hand back, and a press left unanswered keeps its loading
        // indicator.
        var callbackId = update.Callback?.CallbackId;
        if (!string.IsNullOrEmpty(callbackId))
        {
            var answerResult = await AnswerCallbackAsync(callbackId, cancellationToken);
            if (answerResult.IsFailure)
            {
                _logger.LogWarning(
                    "Failed to answer the MAX callback of a menu item. Error: {ErrorCode}",
                    answerResult.Error.Code);
            }
        }

        return Result<ChannelInboundResult>.Success(new ChannelMenuItemPressedResult
        {
            Identity = ExtractIdentitySnapshotFromUser(user, update.UserLocale, _timeProvider.GetUtcNow()),
            Value = value,
            PressId = string.IsNullOrEmpty(callbackId) ? null : callbackId,
            RawEvent = update
        });
    }

    /// <summary>
    /// Handles a press of the secondary button of the phone step (SPEC-041 L73).
    /// </summary>
    /// <param name="update">MAX update of type MessageCallback.</param>
    /// <param name="user">The user who pressed the button.</param>
    /// <param name="action">The action the button stands for.</param>
    /// <param name="rawTransactionId">The raw transaction identifier of the payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Processing result.</returns>
    private async Task<Result<ChannelInboundResult>> ProcessPhoneStepActionAsync(
        Update update,
        User user,
        PhoneStepAction action,
        string rawTransactionId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "MAX phone step action {Action} received from user {UserIdHash}, transaction {TransactionId}",
            action,
            LogMasking.Fingerprint(user.UserId?.ToString(CultureInfo.InvariantCulture)),
            rawTransactionId);

        if (!TryParseEventTransactionId(rawTransactionId, out var transactionId))
        {
            return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult { RawEvent = update });
        }

        // The press is answered right here, as a confirmation press is answered with its receipt: the
        // result of the phone step carries no interaction token for the core to hand back, and a press
        // left unanswered keeps its loading indicator.
        var callbackId = update.Callback?.CallbackId;
        if (!string.IsNullOrEmpty(callbackId))
        {
            var answerResult = await AnswerCallbackAsync(callbackId, cancellationToken);
            if (answerResult.IsFailure)
            {
                _logger.LogWarning(
                    "Failed to answer the MAX callback of the phone step. Error: {ErrorCode}",
                    answerResult.Error.Code);
            }
        }

        return Result<ChannelInboundResult>.Success(new ChannelPhoneStepActionResult
        {
            TransactionId = transactionId,
            Identity = ExtractIdentitySnapshotFromUser(user, update.UserLocale, _timeProvider.GetUtcNow()),
            Action = action,
            RawEvent = update
        });
    }

    /// <summary>
    /// Handles a message with a shared contact — the answer to the phone number request. Ownership
    /// (SPEC-041 L45): the signature of the contact is HMAC-SHA256 of its vCard keyed with the token of
    /// the bot that received it, and it alone decides. A contact without a matching signature — somebody
    /// else's card, one sent without the button — is still reported,
    /// marked as rejected, so the phone step can ask again (L46). The number comes from the signed
    /// vCard, never from the unsigned <c>vcf_phone</c>, and is handed on in E.164 (L47); one that is
    /// absent or has no E.164 form is handed on empty.
    /// </summary>
    /// <param name="update">MAX update of type MessageCreated.</param>
    /// <param name="contact">The shared contact, read off the raw body of the update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Processing result.</returns>
    private async Task<Result<ChannelInboundResult>> ProcessContactMessageAsync(
        Update update,
        MaxSharedContact contact,
        CancellationToken cancellationToken)
    {
        // The sender is vetted the way every event naming no transaction is (CA-193): present, not a
        // bot, and in a dialog with the bot rather than in a group chat or a channel.
        var sender = contact.Sender;
        if (sender is null
            || sender.UserId is null
            || sender.IsBot is true
            || contact.RecipientChatType is ChatType.Chat or ChatType.Channel)
        {
            _logger.LogDebug("MAX contact message rejected: no eligible sender or not a dialog");

            return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult { RawEvent = update });
        }

        var vcfInfo = contact.VcfInfo;
        var contactHash = contact.Hash;

        // The number is handed on in E.164 by the one rule of the core (SPEC-003 CA-009): the platform
        // writes it without the plus. A number with no E.164 form counts as absent; it does not touch
        // the ownership, which the signature alone decides.
        var phoneNumber = E164PhoneNumber.TryNormalize(
            MaxContactSignature.ReadPhone(vcfInfo),
            out var normalizedPhone)
                ? normalizedPhone
                : null;

        var ownershipProven = false;
        if (!string.IsNullOrEmpty(vcfInfo) && !string.IsNullOrEmpty(contactHash))
        {
            // The token of the bot of the ambient tenant — the one that received this update.
            var credentialsResult = await ResolveCredentialsAsync(cancellationToken);
            if (credentialsResult.IsFailure)
            {
                _logger.LogWarning(
                    "MAX contact signature cannot be checked: credentials are unavailable. Error: {ErrorCode}",
                    credentialsResult.Error.Code);
            }
            else
            {
                ownershipProven = MaxContactSignature.Verify(
                    vcfInfo, contactHash, credentialsResult.Value.BotToken);
            }
        }

        var senderIdHash = LogMasking.Fingerprint(sender.UserId.Value.ToString(CultureInfo.InvariantCulture));
        if (ownershipProven)
        {
            _logger.LogInformation(
                "Contact proven to belong to MAX user {UserIdHash}: HasNumber = {HasNumber}",
                senderIdHash,
                phoneNumber is not null);
        }
        else
        {
            _logger.LogWarning(
                "MAX contact is not proven to belong to the sender {UserIdHash}: HasSignature = {HasSignature}, HasNumber = {HasNumber}",
                senderIdHash,
                !string.IsNullOrEmpty(contactHash),
                phoneNumber is not null);
        }

        return Result<ChannelInboundResult>.Success(new ChannelPhoneSharedResult
        {
            PhoneNumber = phoneNumber ?? string.Empty,
            Identity = ExtractIdentitySnapshotFromUser(sender, contact.UserLocale, _timeProvider.GetUtcNow()),
            OwnershipRejected = !ownershipProven,
            RawEvent = update
        });
    }

    /// <summary>
    /// Handles the bot start event by a user (deep link navigation).
    /// </summary>
    /// <param name="update">MAX update of type BotStarted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Processing result.</returns>
    private Task<Result<ChannelInboundResult>> ProcessBotStartedAsync(Update update, CancellationToken cancellationToken)
    {
        var user = update.User;
        if (user is null || user.UserId is null)
        {
            return Task.FromResult(Result<ChannelInboundResult>.Failure(
                ChannelAdapterErrorCodes.IdentityExtractionFailed,
                "User info is missing in the MAX BotStarted event"));
        }

        // Extract the deep link payload
        var startPayload = update.Payload;
        string? transactionIdStr = null;

        // The payload contains our auth_{transactionId}
        if (!string.IsNullOrEmpty(startPayload)
            && startPayload.StartsWith(MaxAdapterConstants.DeepLinkPrefix, StringComparison.Ordinal))
        {
            transactionIdStr = startPayload[MaxAdapterConstants.DeepLinkPrefix.Length..];
        }

        var identity = ExtractIdentitySnapshotFromUser(user, update.UserLocale, _timeProvider.GetUtcNow());

        _logger.LogInformation(
            "MAX BotStarted received from user {UserIdHash}, transaction {TransactionId}",
            LogMasking.Fingerprint(user.UserId?.ToString(CultureInfo.InvariantCulture)),
            transactionIdStr ?? "missing");

        // A start without our deep link payload is not a sign-in attempt — nothing to correlate — but
        // it IS a direct message, so the sender may travel with it and the core may answer
        // (SPEC-003 CA-201). The sender is vetted right here, because this is the only branch where
        // nothing downstream will do it: an event that names a transaction is checked against that
        // transaction's own policy (the engine answers bot_rejected), and this one names none, so no
        // policy can be resolved for it and the strict reading is the only one available. The private
        // half of "not a bot, private chat" comes with the update type — MAX raises BotStarted for a
        // dialog with the bot and nothing else — so only the bot half is left to ask about.
        // A sender that fails the check is an event the core is not to know the sender of (CA-193).
        if (transactionIdStr is null)
        {
            if (identity.IsBot)
            {
                _logger.LogWarning(
                    "MAX BotStarted naming no transaction rejected: the sender is a bot. UserIdHash: {UserIdHash}",
                    LogMasking.Fingerprint(user.UserId?.ToString(CultureInfo.InvariantCulture)));

                return Task.FromResult(Result<ChannelInboundResult>.Success(
                    new ChannelUnrelatedResult { RawEvent = update }));
            }

            return Task.FromResult(Result<ChannelInboundResult>.Success(
                new ChannelUnaddressedResult { Identity = identity, RawEvent = update }));
        }

        if (!TryParseEventTransactionId(transactionIdStr, out var startTransactionId))
        {
            return Task.FromResult(Result<ChannelInboundResult>.Success(
                new ChannelUnrelatedResult { RawEvent = update }));
        }

        return Task.FromResult(Result<ChannelInboundResult>.Success(new ChannelAuthStartResult
        {
            TransactionId = startTransactionId,
            Identity = identity,
            RawEvent = update
        }));
    }

    /// <summary>
    /// Parses the transaction identifier carried by a channel event. An identifier that does not
    /// parse means the event is not ours: the caller answers with an irrelevant-event result instead
    /// of handing a broken value to the core (SPEC-003 §6.3).
    /// </summary>
    /// <param name="rawTransactionId">Raw identifier as it arrived in the event.</param>
    /// <param name="transactionId">Parsed identifier on success.</param>
    /// <returns>true — the identifier parsed and the intent can be constructed.</returns>
    private bool TryParseEventTransactionId(
        string? rawTransactionId,
        out TransactionId transactionId)
    {
        if (TransactionId.TryParse(rawTransactionId, out transactionId))
        {
            return true;
        }

        _logger.LogWarning(
            "Unparseable transaction identifier in a {ChannelType} event: {RawId}. "
            + "The event is treated as irrelevant",
            ChannelTypes.Max,
            rawTransactionId);

        return false;
    }

    /// <summary>
    /// Extracts a user identity snapshot from the MAX User object.
    /// CA-003: ChannelUserId must not contain the ':' character.
    /// </summary>
    /// <param name="user">MAX user.</param>
    /// <param name="userLocale">Recipient locale reported by the channel (null — unknown).</param>
    /// <param name="capturedAt">Capture moment (UTC), supplied by the caller's time provider.</param>
    /// <returns>Channel user identity snapshot.</returns>
    private static ChannelIdentitySnapshot ExtractIdentitySnapshotFromUser(
        User user,
        string? userLocale,
        DateTimeOffset capturedAt)
    {
        // Build the display name from the available fields
        var displayName = BuildDisplayName(user);

        // CA-003: the identifier must not contain ':'
        var channelUserId = user.UserId!.Value.ToString();

        // Build the raw metadata with size validation (CA-005: max 4 KB)
        var rawMetadata = BuildRawMetadata(user);

        return new ChannelIdentitySnapshot
        {
            // Tenant of the inbound request, taken from the ambient scope the host opened for it —
            // the {tenant} segment of the webhook route or the polling iteration of that tenant.
            // The identity of a channel user belongs to the tenant whose channel received the event,
            // so the scope is the authoritative source here and the transaction is not consulted.
            TenantId = ChannelTenantContext.CurrentTenantId,
            ChannelType = ChannelTypes.Max,
            ChannelUserId = channelUserId,
            IsBot = user.IsBot ?? false,
            DisplayName = displayName,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Username = user.Username,
            AvatarUrl = user.AvatarUrl,
            // Recipient locale from the update's user_locale (MAX Bot API, IETF BCP 47) — B17
            Locale = userLocale,
            CapturedAt = capturedAt,
            AdapterVersion = MaxAdapterConstants.AdapterVersion,
            RawMetadata = rawMetadata
        };
    }

    /// <summary>
    /// Builds the user's display name from the available fields.
    /// </summary>
    /// <param name="user">MAX user.</param>
    /// <returns>Display name.</returns>
    private static string BuildDisplayName(User user)
    {
        // Prefer FirstName + LastName, then Username, then UserId
        if (!string.IsNullOrEmpty(user.FirstName))
        {
            return string.IsNullOrEmpty(user.LastName)
                ? user.FirstName
                : $"{user.FirstName} {user.LastName}";
        }

        return user.Username ?? user.UserId!.Value.ToString();
    }

    /// <summary>
    /// Builds the raw metadata of a MAX user with size validation (CA-005).
    /// If the size exceeds the limit, returns null.
    /// </summary>
    /// <param name="user">MAX user.</param>
    /// <returns>A JSON element with the metadata, or null if the limit is exceeded.</returns>
    private static JsonElement? BuildRawMetadata(User user)
    {
        // Build a minimal set of metadata
        var metadata = new
        {
            max_user_id = user.UserId
        };

        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata);

        // CA-005: check the size (maximum 4 KB)
        if (metadataBytes.Length > MaxAdapterConstants.MaxRawMetadataSize)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(metadata);
    }
}
