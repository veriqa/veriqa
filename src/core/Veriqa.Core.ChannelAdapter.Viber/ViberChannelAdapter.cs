// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.Viber.Configuration;
using Veriqa.Core.ChannelAdapter.Viber.Constants;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Viber;

/// <summary>
/// Viber channel adapter (SPEC-003 §6): a sign-in started by a <c>viber://pa</c> link and confirmed
/// in the conversation with a bot. The link carries the transaction in its <c>context</c>
/// parameter; opening it delivers a conversation-started event that starts the sign-in, the user
/// is asked with a text and a keyboard of two reply buttons, and the outcome arrives as a message
/// of its own.
/// </summary>
internal sealed class ViberChannelAdapter : IChannelAdapter
{
    /// <summary>
    /// Facts Viber declares about itself (SPEC-003 §6.1): reply buttons of a keyboard let the user
    /// confirm inside the channel; a message is plain text; a sent message cannot be edited, so the
    /// outcome is a new message; the recipient locale is the language of the user's device, which
    /// every event names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The phone number comes on request, by a keyboard button of the platform that sends the
    /// number of the user's account as a contact (SPEC-041 R6). A contact a user picked from the
    /// address book arrives in the very same shape, and the platform names no difference between
    /// the two, so the number is not proven to belong to the sender (SPEC-041 L45).
    /// </para>
    /// <para>
    /// A link to the claim completion form (SPEC-041 C76) and the buttons of a post-sign-in action
    /// (SPEC-012 §4.9) are a keyboard under the text: a link is an <c>open-url</c> button, a callback
    /// item of the menu is a reply button. After a completed sign-in the chat can be sent a welcome
    /// message, a menu or a link to continue with.
    /// </para>
    /// </remarks>
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
        SupportsMessageUpdate = false,
        DeliversOutcomeNotice = true,
        ProvidesRecipientLocale = true,
        RendersEmoji = true,
        PhoneNumber = new PhoneNumberCapability
        {
            Acquisition = PhoneNumberAcquisition.OnRequest,
            Proof = PhoneNumberProof.NotProven
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

    /// <summary>
    /// What the keyboard of Viber lets the adapter draw: links and callbacks alike, the caption within
    /// the limit of the platform. The platform names no limit for the action body nor for the number
    /// of buttons, so neither is checked beforehand: the platform is the judge (SPEC-003 §4.9.3).
    /// </summary>
    private static readonly ActionButtonLimits KeyboardButtonLimits = new()
    {
        MaxLabelLength = ViberAdapterConstants.KeyboardButtonTextMaxLength
    };

    /// <summary>
    /// Bytes a contact message carries in its body — the type of the message and the name of the
    /// member holding the contact, both spelled this way.
    /// </summary>
    private static ReadOnlySpan<byte> ContactMarker => "\"contact\""u8;

    /// <summary>
    /// Bytes every message event carries — the name of the event and of the member holding the
    /// message. The callbacks about the bot's own messages (<c>delivered</c>, <c>seen</c>,
    /// <c>failed</c>) and the subscription events name only <c>"message_token"</c>, so they lack it.
    /// </summary>
    private static ReadOnlySpan<byte> MessageMarker => "\"message\""u8;

    /// <summary>
    /// Scheme letters every web link begins with, matched ignoring case as <see cref="Uri"/> reads
    /// the scheme.
    /// </summary>
    private static ReadOnlySpan<byte> WebSchemeLetters => "http"u8;

    /// <summary>
    /// The first of <see cref="WebSchemeLetters"/> in both cases.
    /// </summary>
    private static ReadOnlySpan<byte> WebSchemeFirstLetters => "hH"u8;

    /// <summary>
    /// Outbound calls of the channel.
    /// </summary>
    private readonly ViberBotApiClient _botApi;

    /// <summary>
    /// Canonical layer resolver — the source of the tenant's credentials. They are resolved per
    /// operation for the tenant of the current request and never captured in the constructor.
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
    /// Clock behind the capture timestamps and the health-check stamps.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ViberChannelAdapter> _logger;

    /// <summary>
    /// Creates an instance of <see cref="ViberChannelAdapter"/>.
    /// </summary>
    /// <param name="botApi">Outbound calls of the channel.</param>
    /// <param name="resolver">Canonical layer resolver (source of the tenant's credentials).</param>
    /// <param name="promptLocalizer">Channel message localizer.</param>
    /// <param name="messageTemplates">Accessor of the channel message templates of this adapter.</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public ViberChannelAdapter(
        ViberBotApiClient botApi,
        IConfigurationResolver resolver,
        IConfirmationPromptLocalizer promptLocalizer,
        IChannelMessageTemplates messageTemplates,
        TimeProvider timeProvider,
        ILogger<ViberChannelAdapter> logger)
    {
        _botApi = botApi ?? throw new ArgumentNullException(nameof(botApi));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ChannelType => ChannelTypes.Viber;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities => DeclaredCapabilities;

    /// <inheritdoc />
    /// <remarks>
    /// Besides the markers of Veriqa buttons and links, two more events are claimed. A contact
    /// message answers the phone number request of claim completion and carries the number alone,
    /// with no marker of its own. The echo of a pressed link button carries the address alone: it may
    /// be the link to the claim completion form, whose token must not reach the bot of the
    /// integrator (SPEC-041 N78), so every message that is one link and nothing else is claimed.
    /// </remarks>
    public bool OwnsInboundEvent(ChannelInboundRequest request)
    {
        var body = request.Body.Span;

        return CallbackDataPrefixes.ContainsAnyMarker(body)
            || body.IndexOf(ContactMarker) >= 0
            || IsLinkEcho(body);
    }

    /// <inheritdoc />
    public Task<Result<ChannelInboundResult>> ProcessInboundEventAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // The method maps one event of the platform to one intent. A delivery carries exactly one
        // event, and everything a sign-in needs to know about its sender is in the event itself,
        // so no call to the platform is made here.
        ViberWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ViberWebhookPayload>(request.Body.Span);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize the Viber webhook payload.");
            return Task.FromResult(Result<ChannelInboundResult>.Failure(
                ChannelAdapterErrorCodes.UnsupportedEventType,
                "The Viber webhook body is not valid JSON."));
        }

        return Task.FromResult(Result<ChannelInboundResult>.Success(Recognize(payload)));
    }

    /// <inheritdoc />
    public async Task<Result<ChannelMessageRef?>> SendConfirmationPromptAsync(
        TransactionId transactionId,
        string channelUserId,
        ConfirmationPromptContext promptContext,
        CancellationToken cancellationToken = default)
    {
        // The method sends one message: the question and a keyboard of two reply buttons under it.
        // The recipient locale travels with the context and nowhere else (SPEC-017 §7.2).
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

        // The button captions are Natural Keys localized into the recipient's language (ICC-050);
        // a missing translation falls back to the base text of the key.
        var confirmButtonText = _promptLocalizer.ResolveOrBaseText(
            ViberAdapterConstants.ConfirmButtonText, recipientLocale);
        var declineButtonText = _promptLocalizer.ResolveOrBaseText(
            ViberAdapterConstants.DeclineButtonText, recipientLocale);

        // A press of a reply button comes back as a text message whose text is the action body.
        var transactionIdString = transactionId.ToString();
        var keyboard = BuildKeyboard(
            ReplyButton(confirmButtonText, CallbackDataPrefixes.Confirm + transactionIdString),
            ReplyButton(declineButtonText, CallbackDataPrefixes.Decline + transactionIdString));

        return await _botApi.SendTextAsync(
            channelUserId,
            messageText,
            keyboard,
            minApiVersion: null,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<ChannelMessageRef?>> SendMessageAsync(
        ChannelMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // A link to the claim completion form (SPEC-041 C76) is drawn as one link button, along the
        // same path as the buttons of a post-sign-in action (SPEC-012 §4.9).
        if (message.ClaimFormLink is { } formLink)
        {
            return SendWithActionButtonsAsync(
                message,
                new ActionButtonsContent
                {
                    Buttons = [new ChannelActionButton { Label = formLink.ButtonLabel, Url = formLink.Url }]
                },
                cancellationToken);
        }

        if (message.ActionButtons is { } actionButtons)
        {
            return SendWithActionButtonsAsync(message, actionButtons, cancellationToken);
        }

        // The phone number request (SPEC-041 R6) is the text with two buttons under it: the
        // button of the platform that shares the number of the user's account, and the reply
        // button of the secondary action of the step. Both captions arrive already worded.
        if (message.PhoneNumberRequest is not { } phoneRequest)
        {
            return _botApi.SendTextAsync(
                message.ChannelUserId,
                message.Text,
                keyboard: null,
                minApiVersion: null,
                cancellationToken);
        }

        var keyboard = BuildKeyboard(
            new ViberKeyboardButton
            {
                ActionType = ViberAdapterConstants.ActionTypeSharePhone,
                ActionBody = ViberAdapterConstants.SharePhoneActionBody,
                Text = phoneRequest.ShareButtonLabel
            },
            ReplyButton(
                phoneRequest.SecondaryButtonLabel,
                PhoneStepButtonPayload.Of(phoneRequest.SecondaryAction, phoneRequest.TransactionId)));

        return _botApi.SendTextAsync(
            message.ChannelUserId,
            message.Text,
            keyboard,
            ViberAdapterConstants.SharePhoneMinApiVersion,
            cancellationToken);
    }

    /// <summary>
    /// Sends the text with a keyboard of action buttons under it, one button in a row: a link is an
    /// <c>open-url</c> button, a callback is a reply button whose press sends its payload back as a
    /// text message. A message none of whose buttons can be drawn is not sent at all: its text only
    /// introduces the buttons.
    /// </summary>
    /// <param name="message">Message to send — the recipient and the text.</param>
    /// <param name="content">Buttons to draw under the text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reference to the sent message, or a failure.</returns>
    private Task<Result<ChannelMessageRef?>> SendWithActionButtonsAsync(
        ChannelMessage message,
        ActionButtonsContent content,
        CancellationToken cancellationToken)
    {
        var buttons = ActionButtonSelection.SelectDrawable(content, KeyboardButtonLimits, ChannelType, _logger);
        if (buttons.Count == 0)
        {
            return Task.FromResult(Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "None of the action buttons of the message can be drawn in Viber"));
        }

        var keyboardButtons = new ViberKeyboardButton[buttons.Count];
        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            // The same test the shared selection draws a link by: an empty address is no link.
            keyboardButtons[i] = !string.IsNullOrEmpty(button.Url)
                ? LinkButton(button.Label, button.Url)
                : ReplyButton(button.Label, button.CallbackData!);
        }

        return _botApi.SendTextAsync(
            message.ChannelUserId,
            message.Text,
            BuildKeyboard(keyboardButtons),
            minApiVersion: null,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ValidateWebhookAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // The method proves a request comes from Viber: the platform signs the body of every
        // event with the token of the bot. The token is the tenant's, so the credentials are
        // resolved for the tenant of this very request.
        var credentialsResult = await ViberCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure || string.IsNullOrWhiteSpace(credentialsResult.Value.AuthToken))
        {
            _logger.LogError("Viber webhook rejected: credentials are unavailable.");
            return Result<bool>.Success(false);
        }

        if (!request.Headers.TryGetValue(ViberAdapterConstants.SignatureHeader, out var signatureHeader)
            || !TryReadSignature(signatureHeader, out var supplied))
        {
            _logger.LogWarning(
                "Viber webhook rejected: header {SignatureHeader} is missing or is not a SHA-256 value.",
                ViberAdapterConstants.SignatureHeader);
            return Result<bool>.Success(false);
        }

        // The envelope carries the body byte for byte, so the HMAC is computed over exactly what
        // arrived; the two signatures are compared in constant time (CA-033).
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(credentialsResult.Value.AuthToken),
            request.Body.Span);

        var isValid = CryptographicOperations.FixedTimeEquals(expected, supplied);
        if (!isValid)
        {
            _logger.LogWarning("Viber webhook rejected: invalid signature.");
        }

        return Result<bool>.Success(isValid);
    }

    /// <inheritdoc />
    public async Task<Result<ChannelHealthStatus>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        // The method reports whether the channel is configured, without a network call. The probe
        // runs outside any request, and the installation's own credentials (the default implicit
        // tenant) are the answer it wants: a health endpoint reports the state of this deployment,
        // not of one of its tenants. That is said explicitly, so the read is not mistaken for a
        // request path that forgot to state its tenant (CA-164).
        var startedAt = _timeProvider.GetUtcNow();

        using var tenantScope = ChannelTenantContext.BeginScope(tenantId: null);
        var credentialsResult = await ViberCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);

        var isHealthy = credentialsResult.TryGetValue(out var credentials)
            && !string.IsNullOrWhiteSpace(credentials.AuthToken)
            && !string.IsNullOrWhiteSpace(credentials.BotUri)
            && !string.IsNullOrWhiteSpace(credentials.SenderName);

        var checkedAt = _timeProvider.GetUtcNow();

        return Result<ChannelHealthStatus>.Success(new ChannelHealthStatus(
            IsHealthy: isHealthy,
            ChannelType: ChannelTypes.Viber,
            ResponseTime: checkedAt - startedAt,
            Details: isHealthy ? null : "Viber credentials are unavailable or incomplete.",
            CheckedAt: checkedAt));
    }

    /// <inheritdoc />
    public async Task<Result<string>> GetDeepLinkAsync(
        DeepLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The method builds the link that opens the conversation with the tenant's bot. Its
        // context parameter names the transaction; Viber hands it back in the conversation-started
        // event.
        var credentialsResult = await ViberCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogError("Viber credentials are unavailable for the deep link.");
            return Result<string>.Failure(
                ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                "Viber credentials are unavailable for the deep link.");
        }

        var botUri = credentialsResult.Value.BotUri?.Trim();
        if (string.IsNullOrEmpty(botUri))
        {
            return Result<string>.Failure(
                ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                "BotUri is not configured for the Viber deep link.");
        }

        var deepLink = string.Concat(
            ViberAdapterConstants.DeepLinkBase,
            "?",
            ViberAdapterConstants.DeepLinkChatUriQueryKey,
            "=",
            Uri.EscapeDataString(botUri),
            "&",
            ViberAdapterConstants.DeepLinkContextQueryKey,
            "=",
            Uri.EscapeDataString(CallbackDataPrefixes.Auth + request.TransactionId.ToString()));

        return Result<string>.Success(deepLink);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ReportOutcomeAsync(
        TransactionOutcomeNotice notice,
        CancellationToken cancellationToken = default)
    {
        // Viber has neither callback answers nor an editable sent message: the only way to show
        // where the transaction stands is a new message. It carries no keyboard, which also takes
        // down the buttons of the question. The status text is already localized by the core.
        ArgumentNullException.ThrowIfNull(notice);

        var sendResult = await _botApi.SendTextAsync(
            notice.ChannelUserId,
            notice.StatusText,
            keyboard: null,
            minApiVersion: null,
            cancellationToken);

        return sendResult.IsSuccess
            ? Result<bool>.Success(true)
            : Result<bool>.Failure(sendResult.Error);
    }

    /// <summary>
    /// Reads one event down to its meaning for the core. A conversation opened by a Veriqa link
    /// starts the sign-in; a message is a pressed button, a shared contact or a text that names
    /// no transaction.
    /// </summary>
    /// <param name="payload">Envelope of the event; null for a body that is a JSON <c>null</c>.</param>
    /// <returns>Inbound result typed by intent.</returns>
    private ChannelInboundResult Recognize(ViberWebhookPayload? payload)
    {
        if (payload is null)
        {
            return new ChannelUnrelatedResult();
        }

        if (string.Equals(payload.Event, ViberAdapterConstants.EventConversationStarted, StringComparison.Ordinal))
        {
            // A conversation opened in any other way — from the bot's page, by a link of somebody
            // else — carries no transaction marker and is not about a sign-in.
            if (payload.User is { } user
                && !string.IsNullOrWhiteSpace(user.Id)
                && TryReadTransactionId(payload.Context, CallbackDataPrefixes.Auth, out var startedTransactionId))
            {
                return new ChannelAuthStartResult
                {
                    TransactionId = startedTransactionId,
                    Identity = BuildIdentitySnapshot(user)
                };
            }

            return new ChannelUnrelatedResult();
        }

        if (!string.Equals(payload.Event, ViberAdapterConstants.EventMessage, StringComparison.Ordinal)
            || payload.Sender is not { } sender
            || string.IsNullOrWhiteSpace(sender.Id))
        {
            return new ChannelUnrelatedResult();
        }

        var message = payload.Message;

        // The echo of a pressed link button is dropped before anything else reads the message: it
        // may carry the form token (SPEC-041 N78), so it is neither kept with the result nor logged.
        if (IsLinkEcho(message))
        {
            return new ChannelUnrelatedResult();
        }

        var identity = BuildIdentitySnapshot(sender);

        // A contact is told from a text by the type of the message, never by what a text says:
        // a typed message is not read as a phone number, whatever it contains (SPEC-041 R5).
        if (message is not null
            && string.Equals(message.Type, ViberAdapterConstants.MessageTypeContact, StringComparison.Ordinal))
        {
            return new ChannelPhoneSharedResult
            {
                // The number crosses this boundary in E.164 or not at all (SPEC-041 L47): a value
                // with no E.164 form is handed on empty, which the core counts as absent. The
                // platform names no difference between the number of the user's account and a
                // contact of their address book, so the channel never rejects the ownership.
                PhoneNumber = E164PhoneNumber.TryNormalize(message.Contact?.PhoneNumber, out var phoneNumber)
                    ? phoneNumber
                    : string.Empty,
                Identity = identity
            };
        }

        var text = message?.Text;

        if (TryReadTransactionId(text, CallbackDataPrefixes.Confirm, out var transactionId))
        {
            return new ChannelAuthConfirmResult
            {
                TransactionId = transactionId,
                Identity = identity
            };
        }

        if (TryReadTransactionId(text, CallbackDataPrefixes.Decline, out transactionId))
        {
            return new ChannelAuthDeclineResult
            {
                TransactionId = transactionId,
                Identity = identity
            };
        }

        // The button of the secondary action of the phone step; one whose transaction cannot be
        // read is not an event of a sign-in.
        if (text is not null
            && PhoneStepButtonPayload.TryRead(text, out var phoneStepAction, out var rawTransactionId))
        {
            if (!TransactionId.TryParse(rawTransactionId, out transactionId))
            {
                return new ChannelUnrelatedResult();
            }

            return new ChannelPhoneStepActionResult
            {
                TransactionId = transactionId,
                Identity = identity,
                Action = phoneStepAction
            };
        }

        // A press of a callback item of the post-sign-in menu (SPEC-012 §4.9): the reply button sends
        // its payload back as the text of the message. The marker alone names no item.
        if (MenuItemPayload.TryRead(text, out var menuItemValue))
        {
            return new ChannelMenuItemPressedResult
            {
                Identity = identity,
                Value = menuItemValue,
                PressId = payload.MessageToken?.ToString(CultureInfo.InvariantCulture)
            };
        }

        if (MenuItemPayload.HasMarker(text))
        {
            return new ChannelUnrelatedResult();
        }

        return new ChannelUnaddressedResult
        {
            Identity = identity,
            Text = text
        };
    }

    /// <summary>
    /// Tells whether a raw body is the echo of a pressed link button — see
    /// <see cref="IsLinkEcho(ViberInboundMessage?)"/>. A body that is not an event of the platform is
    /// no echo. The body is parsed only when its bytes can hold an echo at all — a message event with
    /// the letters of a web scheme in it — so the callbacks about the bot's own messages, which come
    /// for every message it sends, and the subscription events cost no parse here. A message event is
    /// parsed whenever a web scheme appears anywhere in it, the avatar address of the sender included,
    /// so most messages of users are parsed even when their text holds no link.
    /// </summary>
    /// <param name="body">Raw body of the inbound request.</param>
    /// <returns>true — the body is a message that is one link and nothing else.</returns>
    private static bool IsLinkEcho(ReadOnlySpan<byte> body)
    {
        if (body.IndexOf(MessageMarker) < 0 || !ContainsWebScheme(body))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ViberWebhookPayload>(body);

            return payload is not null
                && string.Equals(payload.Event, ViberAdapterConstants.EventMessage, StringComparison.Ordinal)
                && IsLinkEcho(payload.Message);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tells whether raw bytes hold the letters of a web scheme, in any case.
    /// </summary>
    /// <param name="body">Raw body of the inbound request.</param>
    /// <returns>true — <c>http</c> occurs in the bytes, ignoring case.</returns>
    private static bool ContainsWebScheme(ReadOnlySpan<byte> body)
    {
        var rest = body;

        while (true)
        {
            var index = rest.IndexOfAny(WebSchemeFirstLetters);
            if (index < 0 || rest.Length - index < WebSchemeLetters.Length)
            {
                return false;
            }

            if (Ascii.EqualsIgnoreCase(rest.Slice(index, WebSchemeLetters.Length), WebSchemeLetters))
            {
                return true;
            }

            rest = rest[(index + 1)..];
        }
    }

    /// <summary>
    /// Tells whether a message is the echo of a pressed link button. The platform sends the address of
    /// an <c>open-url</c> button to the bot as a reply of the user, and the echo is told without any
    /// state of the adapter (SPEC-003 CA-014): all the content of the message, its edges trimmed, is
    /// one absolute <c>http</c> or <c>https</c> address. The content is the text of a text message and
    /// the media of a message of type <c>url</c>, whose text, if any, says nothing else. A message the
    /// user typed that is one link and nothing else is told the same way.
    /// </summary>
    /// <param name="message">Message of the event; null when the event carries none.</param>
    /// <returns>true — the message is one link and nothing else.</returns>
    private static bool IsLinkEcho(ViberInboundMessage? message)
    {
        if (message is null)
        {
            return false;
        }

        if (string.Equals(message.Type, ViberAdapterConstants.MessageTypeUrl, StringComparison.Ordinal))
        {
            return IsSingleWebLink(message.Media)
                && (string.IsNullOrWhiteSpace(message.Text)
                    || string.Equals(message.Text.Trim(), message.Media.Trim(), StringComparison.Ordinal));
        }

        return string.Equals(message.Type, ViberAdapterConstants.MessageTypeText, StringComparison.Ordinal)
            && IsSingleWebLink(message.Text);
    }

    /// <summary>
    /// Tells whether a value, its edges trimmed, is one absolute <c>http</c> or <c>https</c> address
    /// with no other text around or inside it.
    /// </summary>
    /// <param name="value">Value as it arrived; null when the message carries none.</param>
    /// <returns>true — the value is one web link.</returns>
    private static bool IsSingleWebLink([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        // A space or a line break inside means more than the address: text typed around a link.
        foreach (var character in trimmed)
        {
            if (char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds the identity snapshot of a Viber user from what an event says about them.
    /// </summary>
    /// <param name="user">User named by the event; the identifier is known to be present.</param>
    /// <returns>Channel identity snapshot.</returns>
    private ChannelIdentitySnapshot BuildIdentitySnapshot(ViberUser user)
    {
        var userId = user.Id ?? string.Empty;

        return new ChannelIdentitySnapshot
        {
            // Tenant of the inbound request, taken from the ambient scope the host opened for it —
            // the {tenant} segment of the webhook route. The identity of a channel user belongs to
            // the tenant whose channel received the event.
            TenantId = ChannelTenantContext.CurrentTenantId,
            ChannelType = ChannelTypes.Viber,
            IsBot = false,
            ChannelUserId = userId,
            DisplayName = TrimOrNull(user.Name) ?? userId,
            AvatarUrl = TrimOrNull(user.Avatar),

            // The language of the user's device: the source of the locale claim and the head of
            // the recipient-locale chain at once.
            Locale = TrimOrNull(user.Language),
            CapturedAt = _timeProvider.GetUtcNow(),
            AdapterVersion = ViberAdapterConstants.AdapterVersion
        };
    }

    /// <summary>
    /// Builds a keyboard out of its buttons.
    /// </summary>
    /// <param name="buttons">Buttons, in the order they are shown.</param>
    /// <returns>The keyboard.</returns>
    private static ViberKeyboard BuildKeyboard(params ViberKeyboardButton[] buttons) =>
        new()
        {
            Type = ViberAdapterConstants.KeyboardType,
            Buttons = buttons
        };

    /// <summary>
    /// Builds a reply button: its press sends the payload to the bot as a text message.
    /// </summary>
    /// <param name="caption">Caption of the button.</param>
    /// <param name="payload">Text the press sends to the bot.</param>
    /// <returns>The button.</returns>
    private static ViberKeyboardButton ReplyButton(string caption, string payload) =>
        new()
        {
            ActionType = ViberAdapterConstants.ActionTypeReply,
            ActionBody = payload,
            Text = caption
        };

    /// <summary>
    /// Builds a link button: its press opens the address. The press is kept out of the conversation
    /// (<c>Silent</c>) so the address — which may carry the form token (SPEC-041 N78) — is not shown in
    /// the chat; a client older than 6.7 shows it anyway, and the echo it sends to the bot is dropped
    /// by <see cref="IsLinkEcho(ViberInboundMessage?)"/>.
    /// </summary>
    /// <param name="caption">Caption of the button.</param>
    /// <param name="url">Address the button opens.</param>
    /// <returns>The button.</returns>
    private static ViberKeyboardButton LinkButton(string caption, string url) =>
        new()
        {
            ActionType = ViberAdapterConstants.ActionTypeOpenUrl,
            ActionBody = url,
            Text = caption,
            Silent = true
        };

    /// <summary>
    /// Reads the signature bytes out of the value of the signature header.
    /// </summary>
    /// <param name="signatureHeader">Header value as it arrived.</param>
    /// <param name="signature">Signature bytes on success.</param>
    /// <returns>true — the value is the hexadecimal form of a SHA-256 signature.</returns>
    private static bool TryReadSignature(string? signatureHeader, out byte[] signature)
    {
        try
        {
            signature = Convert.FromHexString((signatureHeader ?? string.Empty).AsSpan().Trim());
        }
        catch (FormatException)
        {
            signature = [];
        }

        return signature.Length == ViberAdapterConstants.SignatureSizeBytes;
    }

    /// <summary>
    /// Reads the transaction a marked value names: the prefix followed by the identifier and
    /// nothing else.
    /// </summary>
    /// <param name="value">Value as it arrived in the event; null when the event carries none.</param>
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
    /// Trims a value of the user's profile.
    /// </summary>
    /// <param name="value">Value as received.</param>
    /// <returns>The trimmed value, or null when it is absent or blank.</returns>
    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
