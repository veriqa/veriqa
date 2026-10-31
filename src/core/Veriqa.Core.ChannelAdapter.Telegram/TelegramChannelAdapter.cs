// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Telegram.Configuration;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.ChannelAdapter.Telegram.Constants;
using Veriqa.Core.ChannelAdapter.Telegram.Services;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Telegram;

/// <summary>
/// Telegram channel adapter (SPEC-003 §6, §21).
/// Handles inbound events, sends confirmation prompts,
/// notifications, and manages the deep link.
/// </summary>
internal sealed class TelegramChannelAdapter : IChannelAdapter, IChannelBotName
{
    /// <summary>
    /// Per-tenant channel client factory (CA-164/CA-166).
    /// The adapter stays a stateless singleton (CA-014): it does NOT capture the client in the constructor,
    /// but obtains the per-tenant client at processing time by <see cref="ChannelTenantContext.CurrentTenantId"/>.
    /// This way the factory's per-tenant cache actually works; self-hosted (tenant=null) is the same default N=1 path.
    /// </summary>
    private readonly IChannelClientFactory _clientFactory;

    /// <summary>
    /// Bot information provider.
    /// </summary>
    private readonly IBotInfoProvider _botInfoProvider;

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
    private readonly ILogger<TelegramChannelAdapter> _logger;

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
    /// Clock behind the health-check stamps and the capture timestamps of the adapter.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an instance of <see cref="TelegramChannelAdapter"/>.
    /// </summary>
    /// <param name="clientFactory">Per-tenant channel client factory.</param>
    /// <param name="botInfoProvider">Bot information provider.</param>
    /// <param name="resolver">Canonical layer resolver (source of the tenant's credentials).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="promptLocalizer">Channel message localizer.</param>
    /// <param name="messageTemplates">Accessor of the channel message templates of this adapter.</param>
    /// <param name="timeProvider">Time provider.</param>
    public TelegramChannelAdapter(
        IChannelClientFactory clientFactory,
        IBotInfoProvider botInfoProvider,
        IConfigurationResolver resolver,
        ILogger<TelegramChannelAdapter> logger,
        IConfirmationPromptLocalizer promptLocalizer,
        IChannelMessageTemplates messageTemplates,
        TimeProvider timeProvider)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _botInfoProvider = botInfoProvider ?? throw new ArgumentNullException(nameof(botInfoProvider));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public string ChannelType => ChannelTypes.Telegram;

    /// <summary>
    /// Telegram.Bot v22 serialization options — support polymorphic types
    /// (MaybeInaccessibleMessage and others), which deserialize as null with default settings.
    /// Reused as-is from the library instead of being guessed anew.
    /// </summary>
    private static readonly JsonSerializerOptions TelegramJsonOptions = global::Telegram.Bot.JsonBotAPI.Options;

    /// <summary>
    /// Facts Telegram declares about itself (SPEC-003 §6.1, SPEC-012 §4.4.1): cheap inline buttons —
    /// native UX; the Bot API can edit the bot's own message; the recipient locale arrives with the
    /// user's language_code. The phone number is asked for with a <c>request_contact</c> keyboard button
    /// and is proven to be the sender's when the shared contact names the sender and is not forwarded
    /// (SPEC-041 L45). A link to the claim completion form is drawn as an inline URL button (SPEC-041
    /// C76). After a completed sign-in the chat can be sent a welcome message, a menu or a link to
    /// continue with (SPEC-012 §4.9); the last two are a message with inline buttons under its text —
    /// URL buttons and callback buttons. Truly immutable sets.
    /// </summary>
    private static readonly ChannelCapabilities DeclaredCapabilities = new()
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
    public ChannelCapabilities Capabilities => DeclaredCapabilities;

    /// <inheritdoc />
    /// <remarks>
    /// Besides the markers of Veriqa buttons and deep links, a shared contact is claimed: it answers the
    /// phone number request of claim completion and carries no marker of its own. The label of the
    /// secondary keyboard button of that request arrives as plain text and is NOT recognized here — a
    /// router that owns the bot hands it to its own bot.
    /// </remarks>
    public bool OwnsInboundEvent(ChannelInboundRequest request)
    {
        var body = request.Body.Span;

        return CallbackDataPrefixes.ContainsAnyMarker(body)
            || body.IndexOf(TelegramAdapterConstants.ContactFieldMarker) >= 0;
    }

    /// <summary>
    /// Resolves the Telegram Bot client of the current tenant: the client is built/cached
    /// by the factory keyed on <c>(tenant, ChannelType)</c> from the tenant's credentials. The tenant is taken from the
    /// request's ambient context (<see cref="ChannelTenantContext.CurrentTenantId"/>); null — the default implicit tenant
    /// (self-hosted N=1, 1:1 behavior). If credentials are missing — a graceful <c>Result.Failure</c>, without an exception.
    /// </summary>
    /// <returns>The Telegram Bot API client of the current tenant, or an error for missing credentials/client.</returns>
    private ValueTask<Result<ITelegramBotClient>> ResolveClientAsync(CancellationToken cancellationToken = default)
    {
        var context = ChannelCredentialContext.ForCurrentTenant(ChannelTypes.Telegram);

        return _clientFactory.GetOrCreateClientAsync<ITelegramBotClient>(context, cancellationToken);
    }

    /// <summary>
    /// Resolves the current tenant's Telegram credential group (CA-165): the tenant comes from the
    /// request's ambient context (<see cref="ChannelTenantContext.CurrentTenantId"/>); null — the default implicit
    /// tenant (self-hosted N=1, 1:1 behavior). When credentials are missing — a graceful <c>Result.Failure</c>.
    /// </summary>
    /// <returns>The current tenant's Telegram credentials, or an error when no layer supplies them.</returns>
    private ValueTask<Result<TelegramTenantCredentials>> ResolveCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var context = ChannelCredentialContext.ForCurrentTenant(ChannelTypes.Telegram);

        return _resolver.ResolveCredentialsAsync(TelegramConfigKeys.Credentials, context, _logger, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<ChannelInboundResult>> ProcessInboundEventAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // The wire format is the adapter's business: the core hands over the raw bytes and knows
        // nothing about the Telegram Update shape. The polling loop, which already has a
        // deserialized Update, enters through ProcessUpdateAsync instead — the two paths meet
        // there, so there is exactly one branch of parsing logic.
        Update? update;
        try
        {
            update = JsonSerializer.Deserialize<Update>(request.Body.Span, TelegramJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize the Telegram Update");
            return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult());
        }

        if (update is null)
        {
            return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult());
        }

        return await ProcessUpdateAsync(update, cancellationToken);
    }

    /// <summary>
    /// Processes an already deserialized Telegram update — the entry point of the polling loop,
    /// which receives the object from the platform library and must not serialize it back just to
    /// pass through the neutral envelope.
    /// </summary>
    /// <param name="update">Telegram update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of processing the incoming event.</returns>
    internal async Task<Result<ChannelInboundResult>> ProcessUpdateAsync(
        Update update,
        CancellationToken cancellationToken = default)
    {
        // Handle CallbackQuery (confirm/decline buttons)
        if (update.CallbackQuery is not null)
        {
            // SPEC-003 §4.3 stage 2: validate CallbackQuery.From
            if (!ValidateCallbackQuerySender(update.CallbackQuery))
            {
                return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult { RawEvent = update });
            }

            return await ProcessCallbackQueryAsync(update.CallbackQuery, cancellationToken);
        }

        // Handle the message
        if (update.Message is not null)
        {
            // SPEC-003 §4.3 stage 2: validate the update content
            if (!ValidateMessageContext(update.Message))
            {
                return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult { RawEvent = update });
            }

            return await ProcessMessageAsync(update.Message, cancellationToken);
        }

        // Other update types are unrelated to authentication
        _logger.LogDebug("Received an irrelevant Telegram update, UpdateId = {UpdateId}", update.Id);

        return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult { RawEvent = update });
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
            // Parse the chat identifier
            if (!long.TryParse(channelUserId, out var chatId))
            {
                _logger.LogError(
                    "Cannot convert channelUserId to a chatId: {ChannelUserIdHash}",
                    LogMasking.Fingerprint(channelUserId));

                return Result<ChannelMessageRef?>.Failure(
                    ChannelAdapterErrorCodes.MessageSendFailed,
                    "Invalid channel user identifier");
            }

            // Resolve the tenant client (per-tenant, CA-164); if credentials are missing — graceful Failure.
            var clientResult = await ResolveClientAsync(cancellationToken);
            if (clientResult.IsFailure)
            {
                _logger.LogError(
                    "Failed to resolve the tenant Telegram client to send the confirmation prompt. Error: {ErrorCode}",
                    clientResult.Error.Code);

                return Result<ChannelMessageRef?>.Failure(clientResult.Error);
            }

            var botClient = clientResult.Value;

            // Build the keyboard with confirm and decline buttons. The button captions are Natural Keys
            // localized into the recipient's language (ICC-050); a missing translation falls back to the
            // key's base text.
            var transactionIdStr = transactionId.ToString();
            var confirmButtonText = _promptLocalizer.ResolveOrBaseText(
                TelegramAdapterConstants.ConfirmButtonText, recipientLocale);
            var declineButtonText = _promptLocalizer.ResolveOrBaseText(
                TelegramAdapterConstants.DeclineButtonText, recipientLocale);
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        confirmButtonText,
                        CallbackDataPrefixes.Confirm + transactionIdStr),
                    InlineKeyboardButton.WithCallbackData(
                        declineButtonText,
                        CallbackDataPrefixes.Decline + transactionIdStr)
                }
            });

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

            // Send the message with the keyboard
            var sentMessage = await botClient.SendMessage(
                chatId: chatId,
                text: messageText,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "Confirmation prompt sent to user {ChannelUserIdHash}, transaction {TransactionId}",
                LogMasking.Fingerprint(channelUserId),
                transactionIdStr);

            // Report the coordinates back to the core, which stores them: Message.Id is the exact
            // identifier the edit path parses back via int.TryParse.
            return Result<ChannelMessageRef?>.Success(new ChannelMessageRef
            {
                ChatId = chatId.ToString(CultureInfo.InvariantCulture),
                MessageId = sentMessage.Id.ToString(CultureInfo.InvariantCulture)
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
                "Error sending the confirmation prompt to Telegram");
        }
    }

    /// <inheritdoc />
    public Task<Result<ChannelMessageRef?>> SendMessageAsync(
        ChannelMessage message,
        CancellationToken cancellationToken = default)
    {
        // A phone number request carries a keyboard: the button that shares the contact and a plain
        // button with the label of the secondary action (SPEC-041 L73). The keyboard hides itself once a
        // button of it is pressed, and is taken down for good by the terminal receipt of the sign-in
        // (ReportOutcomeAsync, the new-message step) — the expiry of the transaction included. Until
        // that receipt it stays in place: when the step ends without a press — the window of an
        // Optional number runs out — and, when a web step follows the phone step, for as long as that
        // step lasts. A contact shared from it after the step is over finds no waiting request and is
        // not taken.
        if (message.PhoneNumberRequest is { } phoneRequest)
        {
            var keyboard = new ReplyKeyboardMarkup(new[]
            {
                new[] { KeyboardButton.WithRequestContact(phoneRequest.ShareButtonLabel) },
                new[] { new KeyboardButton(phoneRequest.SecondaryButtonLabel) }
            })
            {
                OneTimeKeyboard = true,
                ResizeKeyboard = true
            };

            return SendTextAsync(message.ChannelUserId, message.Text, keyboard, cancellationToken);
        }

        // A link to the claim completion form is one inline URL button under the text (SPEC-041 C76):
        // the messenger opens the page in its own or the system browser, and the bot never sees the press.
        if (message.ClaimFormLink is { } formLink)
        {
            var linkKeyboard = new InlineKeyboardMarkup(
                InlineKeyboardButton.WithUrl(formLink.ButtonLabel, formLink.Url));

            return SendTextAsync(message.ChannelUserId, message.Text, linkKeyboard, cancellationToken);
        }

        // Buttons of a post-sign-in action are an inline keyboard under the text, one button in a row
        // (SPEC-012 §4.9). A message none of whose buttons can be drawn is not sent at all: its text
        // only introduces the buttons.
        if (message.ActionButtons is { } actionButtons)
        {
            var rows = BuildActionButtonRows(actionButtons);
            if (rows.Count == 0)
            {
                return Task.FromResult(Result<ChannelMessageRef?>.Failure(
                    ChannelAdapterErrorCodes.MessageSendFailed,
                    "None of the action buttons of the message can be drawn in Telegram"));
            }

            return SendTextAsync(message.ChannelUserId, message.Text, new InlineKeyboardMarkup(rows), cancellationToken);
        }

        return SendTextAsync(message.ChannelUserId, message.Text, replyMarkup: null, cancellationToken);
    }

    /// <summary>
    /// Turns the buttons of a message into rows of an inline keyboard, one button in a row, in the order
    /// they are listed. A button that is malformed — neither a link nor a callback, or both — and a
    /// callback button whose payload exceeds the budget the platform names are left out with a warning;
    /// the rest are drawn.
    /// </summary>
    /// <param name="content">Buttons of the message.</param>
    /// <returns>Rows of the keyboard; empty when no button can be drawn.</returns>
    private List<IEnumerable<InlineKeyboardButton>> BuildActionButtonRows(ActionButtonsContent content)
    {
        var rows = new List<IEnumerable<InlineKeyboardButton>>(content.Buttons.Count);

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

            if (hasUrl)
            {
                rows.Add([InlineKeyboardButton.WithUrl(button.Label, button.Url!)]);

                continue;
            }

            // The payload goes to the platform as is, the ownership marker included. Its value comes
            // from the operator's settings and is not logged.
            var payloadBytes = Encoding.UTF8.GetByteCount(button.CallbackData!);
            if (payloadBytes > TelegramAdapterConstants.CallbackDataMaxBytes)
            {
                _logger.LogWarning(
                    "An action button is not drawn in {ChannelType}: its callback payload is {PayloadBytes} bytes, the platform limit is {MaxBytes}",
                    ChannelType,
                    payloadBytes,
                    TelegramAdapterConstants.CallbackDataMaxBytes);

                continue;
            }

            rows.Add([InlineKeyboardButton.WithCallbackData(button.Label, button.CallbackData!)]);
        }

        return rows;
    }

    /// <summary>
    /// Sends a text message to the user and reports back the coordinates of what was sent.
    /// </summary>
    /// <param name="channelUserId">Recipient within the channel.</param>
    /// <param name="text">Message text.</param>
    /// <param name="replyMarkup">Keyboard shown with the message; null — none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reference to the sent message, or a failure.</returns>
    private async Task<Result<ChannelMessageRef?>> SendTextAsync(
        string channelUserId,
        string text,
        ReplyMarkup? replyMarkup,
        CancellationToken cancellationToken)
    {
        try
        {
            // Parse the chat identifier
            if (!long.TryParse(channelUserId, out var chatId))
            {
                _logger.LogError(
                    "Cannot convert channelUserId to a chatId: {ChannelUserIdHash}",
                    LogMasking.Fingerprint(channelUserId));

                return Result<ChannelMessageRef?>.Failure(
                    ChannelAdapterErrorCodes.MessageSendFailed,
                    "Invalid channel user identifier");
            }

            // Resolve the tenant client (per-tenant, CA-164); if credentials are missing — graceful Failure.
            var clientResult = await ResolveClientAsync(cancellationToken);
            if (clientResult.IsFailure)
            {
                _logger.LogError(
                    "Failed to resolve the tenant Telegram client to send the message. Error: {ErrorCode}",
                    clientResult.Error.Code);

                return Result<ChannelMessageRef?>.Failure(clientResult.Error);
            }

            // Send the text message
            var sentMessage = await clientResult.Value.SendMessage(
                chatId: chatId,
                text: text,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);

            _logger.LogDebug(
                "Message sent to user {ChannelUserId}",
                channelUserId);

            return Result<ChannelMessageRef?>.Success(new ChannelMessageRef
            {
                ChatId = chatId.ToString(CultureInfo.InvariantCulture),
                MessageId = sentMessage.Id.ToString(CultureInfo.InvariantCulture)
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
                "Error sending the message to Telegram");
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
            request.Headers.TryGetValue(TelegramAdapterConstants.WebhookSecretHeader, out var headerValue);

            // Resolve the tenant's webhook secret via the seam (one resolve per operation).
            // A resolution failure means the secret is unavailable ⇒ validation is impossible ⇒ reject.
            var credentialsResult = await ResolveCredentialsAsync(cancellationToken);
            if (credentialsResult.IsFailure)
            {
                _logger.LogError(
                    "Webhook secret is unavailable (credentials could not be resolved). Request rejected. " +
                    "WebhookSecretToken must be set for header {Header}",
                    TelegramAdapterConstants.WebhookSecretHeader);

                return Result<bool>.Success(false);
            }

            var configuredSecret = credentialsResult.Value.WebhookSecretToken;

            // SPEC-003 §4.3: If the secret is not configured — reject the request to protect the webhook
            if (string.IsNullOrEmpty(configuredSecret))
            {
                _logger.LogError(
                    "Webhook secret is not configured. Request rejected. " +
                    "WebhookSecretToken must be set for header {Header}",
                    TelegramAdapterConstants.WebhookSecretHeader);

                return Result<bool>.Success(false);
            }

            // If the secret is configured but the header is missing — invalid request
            if (string.IsNullOrEmpty(headerValue))
            {
                _logger.LogWarning(
                    "Header {Header} is missing while the webhook secret is configured",
                    TelegramAdapterConstants.WebhookSecretHeader);

                return Result<bool>.Success(false);
            }

            // Compare fixed-length SHA-256 hashes (32 bytes) via FixedTimeEquals (CA-033):
            // the comparison is always constant-length, which removes the leak of the secret length via an early
            // return on a length mismatch and preserves constant time.
            Span<byte> expectedHash = stackalloc byte[32];
            Span<byte> actualHash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret), expectedHash);
            SHA256.HashData(Encoding.UTF8.GetBytes(headerValue), actualHash);

            var isValid = CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);

            if (!isValid)
            {
                _logger.LogWarning("Invalid webhook secret token");
            }

            return Result<bool>.Success(isValid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook request validation error");

            return Result<bool>.Failure(
                ChannelAdapterErrorCodes.WebhookValidationFailed,
                "Webhook request validation error");
        }
    }

    /// <inheritdoc />
    public async Task<Result<ChannelHealthStatus>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        // Measure the Telegram Bot API response time
        var stopwatch = Stopwatch.StartNew();

        // The probe runs outside any request, so no host path has stated whose credentials it should
        // read — and the installation's own (the default implicit tenant) is the answer it wants: a
        // health endpoint reports the state of THIS deployment, not of one of its tenants. That is
        // said explicitly rather than by omission, so that the read is not mistaken for a request
        // path that forgot to state its tenant (CA-164).
        using var tenantScope = ChannelTenantContext.BeginScope(tenantId: null);

        // Resolve the tenant client; if credentials are missing — an unhealthy status without an exception.
        var clientResult = await ResolveClientAsync(cancellationToken);
        if (clientResult.IsFailure)
        {
            stopwatch.Stop();

            return Result<ChannelHealthStatus>.Success(
                new ChannelHealthStatus(
                    IsHealthy: false,
                    ChannelType: ChannelTypes.Telegram,
                    ResponseTime: stopwatch.Elapsed,
                    Details: clientResult.Error.Code,
                    CheckedAt: _timeProvider.GetUtcNow()));
        }

        try
        {
            // Call GetMe to check API availability
            var botUser = await clientResult.Value.GetMe(cancellationToken);
            stopwatch.Stop();

            _logger.LogDebug(
                "Telegram health check: bot {BotUsername}, response time {ResponseTimeMs} ms",
                botUser.Username,
                stopwatch.ElapsedMilliseconds);

            return Result<ChannelHealthStatus>.Success(
                new ChannelHealthStatus(
                    IsHealthy: true,
                    ChannelType: ChannelTypes.Telegram,
                    ResponseTime: stopwatch.Elapsed,
                    Details: null,
                    CheckedAt: _timeProvider.GetUtcNow()));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            _logger.LogError(
                ex,
                "Telegram health check: error, response time {ResponseTimeMs} ms",
                stopwatch.ElapsedMilliseconds);

            return Result<ChannelHealthStatus>.Success(
                new ChannelHealthStatus(
                    IsHealthy: false,
                    ChannelType: ChannelTypes.Telegram,
                    ResponseTime: stopwatch.Elapsed,
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
                    "Failed to resolve Telegram credentials to build the deep link. Error: {ErrorCode}",
                    credentialsResult.Error.Code);

                return Result<string>.Failure(credentialsResult.Error);
            }

            // Get the bot username
            var botUsername = await _botInfoProvider.GetBotUsernameAsync(cancellationToken);

            // Build the deep link URL
            var deepLink = string.Concat(
                credentialsResult.Value.DeepLinkBaseUrl,
                botUsername,
                "?start=",
                TelegramAdapterConstants.DeepLinkPrefix,
                request.TransactionId.ToString());

            _logger.LogDebug(
                "Deep link generated for transaction {TransactionId}",
                request.TransactionId.ToString());

            return Result<string>.Success(deepLink);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error generating the deep link for transaction {TransactionId}",
                request.TransactionId.ToString());

            return Result<string>.Failure(
                ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                "Error generating the deep link for Telegram");
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
            TelegramAdapterConstants.BotUsernamePrefix,
            ex => _logger.LogWarning(ex, "Failed to obtain the Telegram bot username for the chat name"),
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<bool>> ReportOutcomeAsync(
        TransactionOutcomeNotice notice,
        CancellationToken cancellationToken = default)
    {
        // Telegram decides for itself how to show the reported state — the end of the transaction or
        // the claims completion it is waiting on; the core only states the intent.
        // The two obligations of a live press are independent and are discharged separately: the
        // callback is owed an answer whenever one was pressed, while the message the button belongs
        // to is editable only when the event carried its coordinates. Order of addressing: token
        // coordinates → stored prompt reference (TTL and the coordinate-less press alike) → a new
        // message. An unknown future outcome value changes nothing here: the status text is already
        // localized by the core.
        //
        // The intent picks how far down that ladder the receipt is allowed to start. ReplacePrompt
        // — the shipped value — keeps every step. NewMessage takes neither: the receipt arrives as a
        // new message, and the question is edited only to take its buttons off, so it stays in the
        // conversation with its own text as the record of what the outcome answered, but without an
        // offer the user can still press. Which message that is follows the same order of addressing
        // — token coordinates, then the stored prompt reference — and the first addressable one wins:
        // the second is not tried. This holds for the whole operation — a confirmation, a decline and
        // an expiry alike — because the ladder below is the only path any of them takes.
        //
        // A terminal notice that names a phone number request skips both replacing steps whatever the
        // intent says (SPEC-041 L73): the keyboard of the request is a reply keyboard, and only a new
        // message can take it down. The question is left as it is — by then the state of claims
        // completion stands in its place — and the receipt arrives as a new message. This is what an
        // expiry with a request still waiting in the chat goes through, and the receipt that ends
        // claims completion.
        var intendsNewMessage = notice.DisplayIntent is not OutcomeNoticeDisplayIntent.ReplacePrompt;
        var takesDownPhoneRequest = notice.IsTerminal && notice.PhoneRequestMessage is not null;
        var mayReplacePrompt = !intendsNewMessage && !takesDownPhoneRequest;

        if (TryUnpackInboundToken(notice.InboundToken, out var callbackQueryId, out var tokenMessage))
        {
            // The loading indicator must go even if the edit later fails, or never happens at all —
            // and whatever the intent says. Stopping the indicator is not showing the receipt, so
            // the answer stays outside the intent check below.
            var answerResult = await AnswerCallbackQueryAsync(callbackQueryId, cancellationToken);
            if (answerResult.IsFailure)
            {
                _logger.LogWarning(
                    "Failed to answer the Telegram CallbackQuery. Error: {ErrorCode}",
                    answerResult.Error.Code);
            }

            if (mayReplacePrompt && tokenMessage is not null)
            {
                var editResult = await EditMessageTextAsync(
                    tokenMessage.ChatId, tokenMessage.MessageId, notice.StatusText, cancellationToken);

                // The notice was shown (Success(true)) or could not be delivered — the "the channel
                // deliberately showed nothing" state (Success(false)) does not arise on this path.
                return ToOutcomeResult(editResult);
            }
        }

        if (mayReplacePrompt && notice.PromptMessage is not null)
        {
            var promptEditResult = await EditMessageTextAsync(
                notice.PromptMessage.ChatId, notice.PromptMessage.MessageId, notice.StatusText, cancellationToken);

            return ToOutcomeResult(promptEditResult);
        }

        if (intendsNewMessage)
        {
            // NewMessage: the question keeps its text and loses its keyboard. Addressed by the same
            // order as an edit above — token coordinates first, the stored reference second — and
            // skipped entirely when neither is known (a press on an inline message, polling without
            // a stored prompt, a restarted process). Taking the buttons off is not showing the
            // receipt, so its outcome stays out of the result of this operation: a refusal by the
            // platform (a message older than 48 hours, already edited, no rights) leaves the user
            // with a stale button, which is a worse conversation but not an undelivered notice.
            var promptToDisarm = tokenMessage ?? notice.PromptMessage;
            if (promptToDisarm is not null)
            {
                var markupResult = await RemoveMessageKeyboardAsync(
                    promptToDisarm.ChatId, promptToDisarm.MessageId, cancellationToken);

                if (markupResult.IsFailure)
                {
                    _logger.LogWarning(
                        "Failed to take the inline keyboard off the Telegram prompt. Error: {ErrorCode}",
                        markupResult.Error.Code);
                }
            }
        }

        // Nothing addressable left (polling without a stored prompt, a restarted process on a path
        // that carried no token), nothing the intent allows to replace, or a phone number request to
        // take down: the status still reaches the user, as a new message.
        //
        // A terminal receipt also takes down the keyboard of a phone number request (SPEC-041 L73):
        // a reply keyboard can only be removed by a new message — the edits above accept inline
        // markup alone. A chat that shows no such keyboard loses nothing by it. The state of claims
        // completion leaves the keyboard alone: it arrives after the request it would take away.
        ReplyMarkup? replyMarkup = notice.IsTerminal ? new ReplyKeyboardRemove() : null;
        var sendResult = await SendTextAsync(
            notice.ChannelUserId, notice.StatusText, replyMarkup, cancellationToken);
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
    /// Packs the callback query id and the coordinates of the message the button belongs to into the
    /// opaque inbound token. The layout is the adapter's own business — the core never parses it.
    /// </summary>
    /// <param name="callbackQueryId">CallbackQuery identifier (ephemeral, exists only in this event).</param>
    /// <param name="chatId">Chat identifier of the original message (null — the event carried none).</param>
    /// <param name="messageId">Identifier of the original message (null — the event carried none).</param>
    /// <returns>The token, or null when there is no callback query to answer.</returns>
    /// <remarks>
    /// The callback query id is what makes the token exist: it arrives with every button press and is
    /// the only way to stop the loading indicator. The message coordinates are optional — a press on
    /// an inline message, or on one older than 48 hours, carries none — so their absence empties two
    /// token slots instead of dropping the token, which would cost the user an answer that is always due.
    /// </remarks>
    private static ChannelInboundToken? PackInboundToken(string callbackQueryId, string? chatId, string? messageId)
    {
        if (string.IsNullOrEmpty(callbackQueryId))
        {
            return null;
        }

        return new ChannelInboundToken(string.Join(
            TelegramAdapterConstants.InboundTokenSeparator,
            callbackQueryId,
            chatId ?? string.Empty,
            messageId ?? string.Empty));
    }

    /// <summary>
    /// Unpacks a token produced by <see cref="PackInboundToken"/>. Survives a foreign or malformed
    /// value without throwing: the caller then falls back to the other addressing options.
    /// </summary>
    /// <param name="token">Opaque token handed back by the core (null — no live interaction).</param>
    /// <param name="callbackQueryId">CallbackQuery identifier on success.</param>
    /// <param name="message">Coordinates of the original message, or null when the event carried none.</param>
    /// <returns>true — the token was produced by this adapter and names a callback query to answer.</returns>
    private static bool TryUnpackInboundToken(
        ChannelInboundToken? token,
        out string callbackQueryId,
        out ChannelMessageRef? message)
    {
        callbackQueryId = string.Empty;
        message = null;

        var value = token?.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var parts = value.Split(TelegramAdapterConstants.InboundTokenSeparator);
        if (parts.Length != TelegramAdapterConstants.InboundTokenPartCount
            || string.IsNullOrEmpty(parts[0]))
        {
            return false;
        }

        callbackQueryId = parts[0];

        // The coordinates are a pair — an edit needs both — so a half-filled pair reads as "absent".
        if (!string.IsNullOrEmpty(parts[1]) && !string.IsNullOrEmpty(parts[2]))
        {
            message = new ChannelMessageRef { ChatId = parts[1], MessageId = parts[2] };
        }

        return true;
    }

    /// <summary>
    /// Answers a callback query, removing the loading indicator.
    /// </summary>
    /// <param name="callbackQueryId">CallbackQuery identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the call succeeded.</returns>
    private async Task<Result> AnswerCallbackQueryAsync(
        string callbackQueryId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the tenant client (per-tenant, CA-164); if credentials are missing — graceful Failure.
            var clientResult = await ResolveClientAsync(cancellationToken);
            if (clientResult.IsFailure)
            {
                return Result.Failure(clientResult.Error);
            }

            // Answer the callback query, removing the loading indicator
            await clientResult.Value.AnswerCallbackQuery(
                callbackQueryId: callbackQueryId,
                cancellationToken: cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            // The identifier itself is not logged: it comes out of the opaque inbound token, whose
            // contents may carry platform identifiers (PII class, CA-121).
            _logger.LogWarning(ex, "Failed to answer the Telegram callback query");

            return Result.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "answerCallbackQuery error in Telegram");
        }
    }

    /// <summary>
    /// Replaces the text of the bot's own message and removes its inline keyboard (CA-142).
    /// </summary>
    /// <param name="chatId">Chat identifier.</param>
    /// <param name="messageId">Message identifier.</param>
    /// <param name="newText">New message text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the edit succeeded.</returns>
    private async Task<Result> EditMessageTextAsync(
        string chatId,
        string messageId,
        string newText,
        CancellationToken cancellationToken)
    {
        try
        {
            // Parse the chat identifier
            if (!long.TryParse(chatId, out var chatIdLong))
            {
                _logger.LogError(
                    "Cannot convert chatId for EditMessage: {ChatIdHash}",
                    LogMasking.Fingerprint(chatId));

                return Result.Failure(
                    ChannelAdapterErrorCodes.InvalidMessageId,
                    "Invalid chat identifier");
            }

            // The Telegram Bot API accepts an int for messageId; convert the string back
            if (!int.TryParse(messageId, out var messageIdInt))
            {
                _logger.LogError(
                    "Cannot convert messageId for EditMessage: {MessageId}",
                    messageId);

                return Result.Failure(
                    ChannelAdapterErrorCodes.InvalidMessageId,
                    "Invalid message identifier");
            }

            // Resolve the tenant client (per-tenant, CA-164); if credentials are missing — graceful Failure.
            var clientResult = await ResolveClientAsync(cancellationToken);
            if (clientResult.IsFailure)
            {
                return Result.Failure(clientResult.Error);
            }

            // Edit the message text and remove the inline keyboard (CA-142)
            await clientResult.Value.EditMessageText(
                chatId: chatIdLong,
                messageId: messageIdInt,
                text: newText,
                replyMarkup: null,
                cancellationToken: cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to edit the message. ChatIdHash: {ChatIdHash}, MessageId: {MessageId}",
                LogMasking.Fingerprint(chatId),
                messageId);

            return Result.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "editMessageText error in Telegram");
        }
    }

    /// <summary>
    /// Takes the inline keyboard off the bot's own message, leaving its text as it is.
    /// </summary>
    /// <param name="chatId">Chat identifier.</param>
    /// <param name="messageId">Message identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the edit succeeded.</returns>
    /// <remarks>
    /// Uses editMessageReplyMarkup — the Bot API operation for changing only the markup — because the
    /// text of an answered question is the record of what the outcome answered and must survive. A
    /// refusal is reported back rather than thrown or logged as an error: the caller treats a prompt
    /// that kept its keyboard as a cosmetic loss, not as an undelivered notice, so nothing on this
    /// path is allowed to speak louder than Warning.
    /// </remarks>
    private async Task<Result> RemoveMessageKeyboardAsync(
        string chatId,
        string messageId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Both coordinates are opaque strings in the contract and platform numbers here; a pair
            // that does not convert names no message to edit.
            if (!long.TryParse(chatId, out var chatIdLong) || !int.TryParse(messageId, out var messageIdInt))
            {
                _logger.LogWarning(
                    "Cannot address the Telegram message to take its keyboard off. ChatIdHash: {ChatIdHash}, MessageId: {MessageId}",
                    LogMasking.Fingerprint(chatId),
                    messageId);

                return Result.Failure(
                    ChannelAdapterErrorCodes.InvalidMessageId,
                    "Invalid message coordinates");
            }

            // Resolve the tenant client (per-tenant, CA-164); if credentials are missing — graceful Failure.
            var clientResult = await ResolveClientAsync(cancellationToken);
            if (clientResult.IsFailure)
            {
                return Result.Failure(clientResult.Error);
            }

            // A null markup is how the Bot API says "no keyboard at all"; the text is not a parameter
            // of this method, so it cannot be touched by mistake.
            await clientResult.Value.EditMessageReplyMarkup(
                chatId: chatIdLong,
                messageId: messageIdInt,
                replyMarkup: null,
                cancellationToken: cancellationToken);

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to edit the message reply markup. ChatIdHash: {ChatIdHash}, MessageId: {MessageId}",
                LogMasking.Fingerprint(chatId),
                messageId);

            return Result.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "editMessageReplyMarkup error in Telegram");
        }
    }

    /// <summary>
    /// Handles a callback query (an inline-button press).
    /// Requests the user's avatar for auth intents.
    /// </summary>
    /// <param name="callbackQuery">Callback query from Telegram.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The processing result.</returns>
    private async Task<Result<ChannelInboundResult>> ProcessCallbackQueryAsync(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        // Check that callback data is present
        var data = callbackQuery.Data;
        if (string.IsNullOrEmpty(data))
        {
            return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult { RawEvent = callbackQuery });
        }

        // Check that user information is present
        var user = callbackQuery.From;

        // Everything needed to address the interaction later goes into one opaque token: the core
        // carries it back untouched, so the platform terms stay inside this adapter.
        var inboundToken = PackInboundToken(
            callbackQuery.Id,
            callbackQuery.Message?.Chat?.Id.ToString(),
            callbackQuery.Message?.Id.ToString());

        // Determine the intent by the callback data prefix
        if (data.StartsWith(CallbackDataPrefixes.Confirm, StringComparison.Ordinal))
        {
            // Extract the transaction identifier
            var transactionIdStr = data[CallbackDataPrefixes.Confirm.Length..];

            // Request the avatar for the auth intent
            var avatarUrl = await TryGetAvatarUrlAsync(user.Id, cancellationToken);
            var identity = ExtractIdentitySnapshotFromUser(user, _timeProvider.GetUtcNow(), avatarUrl);

            _logger.LogInformation(
                "Confirmation received from user {UserIdHash}, transaction {TransactionId}",
                LogMasking.Fingerprint(user.Id.ToString(CultureInfo.InvariantCulture)),
                transactionIdStr);

            if (!TryParseEventTransactionId(transactionIdStr, out var confirmTransactionId))
            {
                return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult { RawEvent = callbackQuery });
            }

            return Result<ChannelInboundResult>.Success(new ChannelAuthConfirmResult
            {
                TransactionId = confirmTransactionId,
                Identity = identity,
                InboundToken = inboundToken,
                RawEvent = callbackQuery
            });
        }

        if (data.StartsWith(CallbackDataPrefixes.Decline, StringComparison.Ordinal))
        {
            // Extract the transaction identifier
            var transactionIdStr = data[CallbackDataPrefixes.Decline.Length..];
            var identity = ExtractIdentitySnapshotFromUser(user, _timeProvider.GetUtcNow());

            _logger.LogInformation(
                "Decline received from user {UserIdHash}, transaction {TransactionId}",
                LogMasking.Fingerprint(user.Id.ToString(CultureInfo.InvariantCulture)),
                transactionIdStr);

            if (!TryParseEventTransactionId(transactionIdStr, out var declineTransactionId))
            {
                return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult { RawEvent = callbackQuery });
            }

            return Result<ChannelInboundResult>.Success(new ChannelAuthDeclineResult
            {
                TransactionId = declineTransactionId,
                Identity = identity,
                InboundToken = inboundToken,
                RawEvent = callbackQuery
            });
        }

        // A press of a callback item of the post-sign-in menu (SPEC-012 §4.9). The marker alone, with
        // no value after it, names no item: such a payload is left unrelated and is not answered.
        if (data.StartsWith(CallbackDataPrefixes.MenuItem, StringComparison.Ordinal)
            && data.Length > CallbackDataPrefixes.MenuItem.Length)
        {
            return await ProcessMenuItemPressAsync(
                callbackQuery, data[CallbackDataPrefixes.MenuItem.Length..], cancellationToken);
        }

        // Unknown prefix — an irrelevant event
        return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult { RawEvent = callbackQuery });
    }

    /// <summary>
    /// Handles a press of a callback item of the post-sign-in menu: answers the press and reports the
    /// value of the item with the identity of the user who pressed it.
    /// </summary>
    /// <param name="callbackQuery">Callback query from Telegram.</param>
    /// <param name="value">Value of the item — what follows the marker in the payload; not empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The processing result.</returns>
    private async Task<Result<ChannelInboundResult>> ProcessMenuItemPressAsync(
        CallbackQuery callbackQuery,
        string value,
        CancellationToken cancellationToken)
    {
        var user = callbackQuery.From;

        _logger.LogInformation(
            "Menu item press received from user {UserIdHash}",
            LogMasking.Fingerprint(user.Id.ToString(CultureInfo.InvariantCulture)));

        // The press is answered right here: the result carries no interaction token for the core to
        // hand back, and a press left unanswered keeps its loading indicator.
        var answerResult = await AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken);
        if (answerResult.IsFailure)
        {
            _logger.LogWarning(
                "Failed to answer the Telegram callback query of a menu item. Error: {ErrorCode}",
                answerResult.Error.Code);
        }

        return Result<ChannelInboundResult>.Success(new ChannelMenuItemPressedResult
        {
            Identity = ExtractIdentitySnapshotFromUser(user, _timeProvider.GetUtcNow()),
            Value = value,
            PressId = callbackQuery.Id,
            RawEvent = callbackQuery
        });
    }

    /// <summary>
    /// Handles an inbound message.
    /// </summary>
    /// <param name="message">Message from Telegram.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The processing result.</returns>
    private async Task<Result<ChannelInboundResult>> ProcessMessageAsync(Message message, CancellationToken cancellationToken)
    {
        // Handle a contact (phone number)
        if (message.Contact is not null)
        {
            return ProcessContactMessage(message);
        }

        // Handle the /start text command
        if (IsStartCommand(message))
        {
            return await ProcessStartCommandAsync(message, cancellationToken);
        }

        // Any other message is a direct message to the bot that names no transaction. The sender is
        // carried along, so the core can answer such a message when the registration declares a text
        // for it (SPEC-003 CA-201); From is non-null here because ValidateMessageContext — the single
        // caller's gate — refuses a message without one. No avatar is requested: a network call per
        // arbitrary message buys nothing here. The text travels too: the secondary button of the phone
        // number request is a plain keyboard button, and its label is all its press sends (SPEC-041 L73).
        return Result<ChannelInboundResult>.Success(new ChannelUnaddressedResult
        {
            Identity = ExtractIdentitySnapshotFromUser(message.From!, _timeProvider.GetUtcNow()),
            Text = message.Text,
            RawEvent = message
        });
    }

    /// <summary>
    /// Handles the /start command with a possible deep link parameter.
    /// Requests the user's avatar for the auth intent.
    /// </summary>
    /// <param name="message">Message with the /start command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The processing result.</returns>
    private async Task<Result<ChannelInboundResult>> ProcessStartCommandAsync(Message message, CancellationToken cancellationToken)
    {
        var user = message.From;
        if (user is null)
        {
            return Result<ChannelInboundResult>.Failure(
                ChannelAdapterErrorCodes.IdentityExtractionFailed,
                "Sender info is missing in the /start message");
        }

        // Extract the deep link parameter from the command text
        var text = message.Text!;
        string? transactionIdStr = null;

        // Format: "/start auth_{transactionId}"
        if (text.Length > TelegramAdapterConstants.BotCommandStart.Length + 1)
        {
            var parameter = text[(TelegramAdapterConstants.BotCommandStart.Length + 1)..];

            if (parameter.StartsWith(TelegramAdapterConstants.DeepLinkPrefix, StringComparison.Ordinal))
            {
                transactionIdStr = parameter[TelegramAdapterConstants.DeepLinkPrefix.Length..];
            }
        }

        _logger.LogInformation(
            "/start command received from user {UserIdHash}, transaction {TransactionId}",
            LogMasking.Fingerprint(user.Id.ToString(CultureInfo.InvariantCulture)),
            transactionIdStr ?? "missing");

        // A /start without our deep link parameter is not a sign-in attempt — nothing to correlate —
        // but it IS a direct message from a vetted sender, so the sender travels with it and the core
        // may answer (SPEC-003 CA-201). No avatar is requested on this branch: it is the sign-in
        // intent below that shows one, and a network call per bare /start buys nothing.
        if (transactionIdStr is null)
        {
            return Result<ChannelInboundResult>.Success(new ChannelUnaddressedResult
            {
                Identity = ExtractIdentitySnapshotFromUser(user, _timeProvider.GetUtcNow()),
                RawEvent = message
            });
        }

        if (!TryParseEventTransactionId(transactionIdStr, out var startTransactionId))
        {
            return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult { RawEvent = message });
        }

        // Request the avatar for the auth intent
        var avatarUrl = await TryGetAvatarUrlAsync(user.Id, cancellationToken);

        return Result<ChannelInboundResult>.Success(new ChannelAuthStartResult
        {
            TransactionId = startTransactionId,
            Identity = ExtractIdentitySnapshotFromUser(user, _timeProvider.GetUtcNow(), avatarUrl),
            RawEvent = message
        });
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
            ChannelTypes.Telegram,
            rawTransactionId);

        return false;
    }

    /// <summary>
    /// Handles a message with a contact (phone number).
    /// Ownership (CA-004, SPEC-041 L45): the contact names the sender (<c>Contact.UserId == From.Id</c>)
    /// and the message is not forwarded. A contact that fails it — somebody else's card, a card from the
    /// address book without a user id, a forwarded one — is still reported, marked as rejected, so the
    /// phone step can ask again (L46).
    /// </summary>
    /// <param name="message">Message with a contact.</param>
    /// <returns>The processing result.</returns>
    private Result<ChannelInboundResult> ProcessContactMessage(Message message)
    {
        var user = message.From;
        var contact = message.Contact!;

        if (user is null)
        {
            return Result<ChannelInboundResult>.Failure(
                ChannelAdapterErrorCodes.PhoneExtractionFailed,
                "Sender info is missing in the contact message");
        }

        var ownershipRejected = contact.UserId is null
            || contact.UserId != user.Id
            || message.ForwardOrigin is not null;

        if (ownershipRejected)
        {
            _logger.LogWarning(
                "Contact does not belong to the sender: Contact.UserIdHash = {ContactUserIdHash}, From.IdHash = {FromIdHash}, Forwarded = {Forwarded}",
                LogMasking.Fingerprint(contact.UserId?.ToString(CultureInfo.InvariantCulture)),
                LogMasking.Fingerprint(user.Id.ToString(CultureInfo.InvariantCulture)),
                message.ForwardOrigin is not null);
        }
        else
        {
            _logger.LogInformation(
                "Phone number received from user {UserIdHash}",
                LogMasking.Fingerprint(user.Id.ToString(CultureInfo.InvariantCulture)));
        }

        // The number is handed on in E.164 by the one rule of the core (SPEC-041 L47, SPEC-003 CA-009):
        // the platform states no format of the field. A number with no E.164 form counts as absent; it
        // does not touch the ownership, which the check above alone decides.
        var phoneNumber = E164PhoneNumber.TryNormalize(
            contact.PhoneNumber,
            out var normalizedPhone)
                ? normalizedPhone
                : string.Empty;

        return Result<ChannelInboundResult>.Success(new ChannelPhoneSharedResult
        {
            PhoneNumber = phoneNumber,
            Identity = ExtractIdentitySnapshotFromUser(user, _timeProvider.GetUtcNow()),
            OwnershipRejected = ownershipRejected,
            RawEvent = message
        });
    }

    /// <summary>
    /// Checks whether the message is the /start command.
    /// </summary>
    /// <param name="message">Message to check.</param>
    /// <returns>true if the message contains the /start command.</returns>
    private static bool IsStartCommand(Message message)
    {
        // Check that text is present and starts with /start
        return message.Text is not null
            && message.Text.StartsWith(TelegramAdapterConstants.BotCommandStart, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts a user identity snapshot from a Telegram User object.
    /// CA-003: ChannelUserId must not contain the ':' character.
    /// </summary>
    /// <param name="user">Telegram user.</param>
    /// <param name="capturedAt">Capture moment (UTC), supplied by the caller's time provider.</param>
    /// <param name="avatarUrl">User avatar URL (may be null).</param>
    /// <returns>The channel user identity snapshot.</returns>
    private static ChannelIdentitySnapshot ExtractIdentitySnapshotFromUser(
        User user,
        DateTimeOffset capturedAt,
        string? avatarUrl = null)
    {
        // Build the display name from FirstName and LastName
        var displayName = string.IsNullOrEmpty(user.LastName)
            ? user.FirstName
            : $"{user.FirstName} {user.LastName}";

        // CA-003: the identifier must not contain ':'
        var channelUserId = user.Id.ToString();

        // Build the raw metadata with size validation (CA-005: max 4 KB)
        var rawMetadata = BuildRawMetadata(user);

        return new ChannelIdentitySnapshot
        {
            // Tenant of the inbound request, taken from the ambient scope the host opened for it —
            // the {tenant} segment of the webhook route or the polling iteration of that tenant.
            // The identity of a channel user belongs to the tenant whose channel received the event,
            // so the scope is the authoritative source here and the transaction is not consulted.
            TenantId = ChannelTenantContext.CurrentTenantId,
            ChannelType = ChannelTypes.Telegram,
            ChannelUserId = channelUserId,
            IsBot = user.IsBot,
            DisplayName = displayName,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Username = user.Username,
            Locale = user.LanguageCode,
            CapturedAt = capturedAt,
            AdapterVersion = TelegramAdapterConstants.AdapterVersion,
            AvatarUrl = avatarUrl,
            RawMetadata = rawMetadata
        };
    }

    /// <summary>
    /// Builds the raw metadata of a Telegram user with size validation (CA-005).
    /// If the size exceeds the limit, returns a minimal set of data.
    /// </summary>
    /// <param name="user">Telegram user.</param>
    /// <returns>A JSON element with metadata, or null if the limit is exceeded.</returns>
    private static JsonElement? BuildRawMetadata(User user)
    {
        // Build the full set of metadata
        var metadata = new
        {
            is_premium = user.IsPremium,
            telegram_id = user.Id
        };

        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata);

        // CA-005: check the size (maximum 4 KB)
        if (metadataBytes.Length > TelegramAdapterConstants.MaxRawMetadataSize)
        {
            // If the limit is exceeded — do not include the metadata
            return null;
        }

        return JsonSerializer.SerializeToElement(metadata);
    }

    /// <summary>
    /// Fetches the user's latest avatar through the Telegram Bot API (GetUserProfilePhotos, GetFile,
    /// file download) and returns the image itself as an avatar data URI (<see cref="AvatarDataUri"/>).
    /// The value carries no Bot API link, so the secret bot token and the Telegram file reference
    /// stay inside the adapter. Returns null when the user has no photo, the file is larger than
    /// <see cref="AvatarDataUri.MaxImageBytes"/>, the bytes are not an accepted image type, or the
    /// Bot API call fails. Cancellation of the request propagates.
    /// </summary>
    /// <param name="userId">Telegram user identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The avatar data URI, or null.</returns>
    private async Task<string?> TryGetAvatarUrlAsync(long userId, CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the tenant client; if credentials are missing — the avatar is unavailable (return null).
            var clientResult = await ResolveClientAsync(cancellationToken);
            if (clientResult.IsFailure)
            {
                return null;
            }

            var botClient = clientResult.Value;

            // Request profile photos (limit=1 — only the latest avatar)
            var photos = await botClient.GetUserProfilePhotos(
                userId: userId,
                offset: 0,
                limit: 1,
                cancellationToken: cancellationToken);

            if (photos.Photos.Length is 0)
            {
                return null;
            }

            // Take the smallest photo variant to save traffic
            var photoSizes = photos.Photos[0];
            if (photoSizes.Length is 0)
            {
                return null;
            }

            // Get the File with file_path via GetFile
            var smallestPhoto = photoSizes[0];
            var file = await botClient.GetFile(
                fileId: smallestPhoto.FileId,
                cancellationToken: cancellationToken);

            if (string.IsNullOrEmpty(file.FilePath))
            {
                return null;
            }

            // The size declared by Bot API rejects an oversized photo without downloading it
            if (file.FileSize > AvatarDataUri.MaxImageBytes)
            {
                _logger.LogDebug(
                    "Avatar of user {UserIdHash} skipped: the file exceeds the size limit",
                    LogMasking.Fingerprint(userId.ToString(CultureInfo.InvariantCulture)));

                return null;
            }

            // A non-resizable buffer of exactly the limit: the write that would overflow it fails,
            // so a stream longer than declared stops the download instead of being read to the end.
            var image = new byte[AvatarDataUri.MaxImageBytes];
            using var destination = new MemoryStream(image, writable: true);
            try
            {
                await botClient.DownloadFile(file.FilePath, destination, cancellationToken);
            }
            catch (Exception ex) when (ex is NotSupportedException || ex.InnerException is NotSupportedException)
            {
                _logger.LogDebug(
                    "Avatar of user {UserIdHash} skipped: the downloaded file exceeds the size limit",
                    LogMasking.Fingerprint(userId.ToString(CultureInfo.InvariantCulture)));

                return null;
            }

            // The image type is taken from the bytes; an unrecognized signature yields no avatar
            if (!AvatarDataUri.TryCreate(image.AsSpan(0, (int)destination.Position), out var avatarDataUri))
            {
                _logger.LogDebug(
                    "Avatar of user {UserIdHash} skipped: the file is not an accepted image",
                    LogMasking.Fingerprint(userId.ToString(CultureInfo.InvariantCulture)));

                return null;
            }

            _logger.LogDebug(
                "Avatar of user {UserIdHash} received",
                LogMasking.Fingerprint(userId.ToString(CultureInfo.InvariantCulture)));

            return avatarDataUri;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to get the avatar of user {UserIdHash}",
                LogMasking.Fingerprint(userId.ToString(CultureInfo.InvariantCulture)));

            return null;
        }
    }

    /// <summary>
    /// Validates the context of an inbound message (SPEC-003 §4.3 stage 2).
    /// Checks: From is not null, From.IsBot == false, Chat.Type == Private.
    /// </summary>
    /// <param name="message">Message to validate.</param>
    /// <returns>true if the message passed validation.</returns>
    private bool ValidateMessageContext(Message message)
    {
        // Check that the sender is present
        if (message.From is null)
        {
            _logger.LogWarning("Update rejected: Message.From is missing");
            return false;
        }

        // Protection against bots
        if (message.From.IsBot)
        {
            _logger.LogWarning(
                "Update rejected: the sender is a bot. UserIdHash: {UserIdHash}",
                LogMasking.Fingerprint(message.From.Id.ToString(CultureInfo.InvariantCulture)));
            return false;
        }

        // Private chats only
        if (message.Chat.Type is not global::Telegram.Bot.Types.Enums.ChatType.Private)
        {
            _logger.LogWarning(
                "Update rejected: chat type is not Private. ChatType: {ChatType}, ChatIdHash: {ChatIdHash}",
                message.Chat.Type,
                LogMasking.Fingerprint(message.Chat.Id.ToString(CultureInfo.InvariantCulture)));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates the sender of a CallbackQuery (SPEC-003 §4.3 stage 2).
    /// Checks: From.IsBot == false.
    /// </summary>
    /// <param name="callbackQuery">CallbackQuery to validate.</param>
    /// <returns>true if the sender passed validation.</returns>
    private bool ValidateCallbackQuerySender(CallbackQuery callbackQuery)
    {
        // CallbackQuery.From is always present (guaranteed by the Telegram API)
        // Protection against bots
        if (callbackQuery.From.IsBot)
        {
            _logger.LogWarning(
                "CallbackQuery rejected: the sender is a bot. UserIdHash: {UserIdHash}",
                LogMasking.Fingerprint(callbackQuery.From.Id.ToString(CultureInfo.InvariantCulture)));
            return false;
        }

        // Chat type check (for a CallbackQuery from inline buttons in a message)
        if (callbackQuery.Message is { Chat: not null }
            && callbackQuery.Message.Chat.Type is not global::Telegram.Bot.Types.Enums.ChatType.Private)
        {
            _logger.LogWarning(
                "CallbackQuery rejected: chat type is not Private. ChatType: {ChatType}",
                callbackQuery.Message.Chat.Type);
            return false;
        }

        return true;
    }
}
