// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Instagram.Configuration;
using Veriqa.Core.ChannelAdapter.Instagram.Constants;
using Veriqa.Core.ChannelAdapter.Meta;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Instagram;

/// <summary>
/// Instagram channel adapter (SPEC-003 §6): a sign-in started by an <c>ig.me</c> link and confirmed
/// in Instagram Direct. The link carries the transaction in its <c>ref</c> parameter; opening it
/// delivers a referral event that starts the sign-in, the user is asked with a button template, and
/// the outcome arrives as a message of its own.
/// </summary>
internal sealed class InstagramChannelAdapter : IChannelAdapter, IChannelBotName
{
    /// <summary>
    /// Facts Instagram declares about itself (SPEC-003 §6.1): buttons of a template let the user
    /// confirm inside the channel; a message is plain text; a delivered message cannot be edited,
    /// so the outcome is a new message; the webhook carries no locale of the user.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The phone number comes on request, by a quick reply the platform fills with the number of
    /// the user's profile (SPEC-041 R6). The platform says nothing about having checked that
    /// number, so it is not proven to belong to the sender (SPEC-041 L45).
    /// </para>
    /// <para>
    /// A link to the claim completion form (SPEC-041 C76) and the buttons of a post-sign-in menu or
    /// link to continue with (SPEC-012 §4.9) are a button template: link buttons and postback
    /// buttons under a text. Instagram documents that the button template is not shown in the web
    /// version today, so a user in the browser sees the text of such a message without its buttons.
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
        ProvidesRecipientLocale = false,
        RendersEmoji = true,
        PhoneNumber = new PhoneNumberCapability
        {
            Acquisition = PhoneNumberAcquisition.OnRequest,
            Proof = PhoneNumberProof.NotProven
        },

        // The link carries the transaction, but the platform does not always hand it back: the first
        // press of a new user may arrive with no referral. The code of the sign-in page is the
        // fallback start beside the link, and the user sends it alone, with no command around it.
        CodeStart = new ChannelCodeStart { LinkCarriesTransaction = true },

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
    /// What the button template lets the adapter draw. It holds at most three buttons; Instagram names
    /// no limit of a button title, of a postback payload or of a link, so none is checked beforehand —
    /// the platform is the judge, and its refusal fails the send (SPEC-003 §4.9.3).
    /// </summary>
    private static readonly ActionButtonLimits ButtonTemplateLimits = new()
    {
        MaxButtons = MetaButtonTemplate.MaxButtons
    };

    /// <summary>
    /// Outbound calls of the channel: the Send API and the user profile read.
    /// </summary>
    private readonly InstagramGraphApiClient _graphApi;

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
    private readonly ILogger<InstagramChannelAdapter> _logger;

    /// <summary>
    /// Creates an instance of <see cref="InstagramChannelAdapter"/>.
    /// </summary>
    /// <param name="graphApi">Outbound calls of the channel.</param>
    /// <param name="resolver">Canonical layer resolver (source of the tenant's credentials).</param>
    /// <param name="promptLocalizer">Channel message localizer.</param>
    /// <param name="messageTemplates">Accessor of the channel message templates of this adapter.</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public InstagramChannelAdapter(
        InstagramGraphApiClient graphApi,
        IConfigurationResolver resolver,
        IConfirmationPromptLocalizer promptLocalizer,
        IChannelMessageTemplates messageTemplates,
        TimeProvider timeProvider,
        ILogger<InstagramChannelAdapter> logger)
    {
        _graphApi = graphApi ?? throw new ArgumentNullException(nameof(graphApi));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// What a messaging event asks the core to do.
    /// </summary>
    private enum InboundIntent
    {
        /// <summary>
        /// Start the sign-in of the named transaction.
        /// </summary>
        Start,

        /// <summary>
        /// Start the sign-in of the transaction the sent code stands for.
        /// </summary>
        StartByCode,

        /// <summary>
        /// Confirm the named transaction.
        /// </summary>
        Confirm,

        /// <summary>
        /// Decline the named transaction.
        /// </summary>
        Decline,

        /// <summary>
        /// A pressed button that names no transaction: a user asking to sign in who brought nothing
        /// to sign in by.
        /// </summary>
        Unaddressed,

        /// <summary>
        /// The phone number of the user's profile, shared by the quick reply of the platform.
        /// </summary>
        PhoneShared,

        /// <summary>
        /// The secondary action of the phone step of the named transaction.
        /// </summary>
        PhoneStepAction,

        /// <summary>
        /// A pressed callback item of the post-sign-in menu; names no transaction.
        /// </summary>
        MenuItemPressed
    }

    /// <summary>
    /// A messaging event read down to its meaning for the core.
    /// </summary>
    /// <param name="Intent">What the event asks for.</param>
    /// <param name="SenderId">Instagram-scoped ID of the user the event comes from.</param>
    /// <param name="TransactionId">
    /// Transaction the event names; default for an event that names none — a pressed button without
    /// one, a sent code and a shared phone number.
    /// </param>
    /// <param name="SharedPhoneNumber">
    /// Phone number as the platform sent it — set for <see cref="InboundIntent.PhoneShared"/> only.
    /// </param>
    /// <param name="PhoneStepAction">
    /// Action of the pressed button — meaningful for <see cref="InboundIntent.PhoneStepAction"/> only.
    /// </param>
    /// <param name="StartCode">
    /// The code as the user sent it — set for <see cref="InboundIntent.StartByCode"/> only.
    /// </param>
    /// <param name="MenuItemValue">
    /// Value of the pressed menu item, never empty — set for <see cref="InboundIntent.MenuItemPressed"/> only.
    /// </param>
    /// <param name="PressId">
    /// Identifier of the press as the platform names it, <c>mid</c> of the postback; null when the
    /// event carries none — set for <see cref="InboundIntent.MenuItemPressed"/> only.
    /// </param>
    private readonly record struct RecognizedEvent(
        InboundIntent Intent,
        string SenderId,
        TransactionId TransactionId,
        string? SharedPhoneNumber = null,
        PhoneStepAction PhoneStepAction = default,
        string? StartCode = null,
        string? MenuItemValue = null,
        string? PressId = null);

    /// <inheritdoc />
    public string ChannelType => ChannelTypes.Instagram;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities => DeclaredCapabilities;

    /// <inheritdoc />
    /// <remarks>
    /// Besides the markers of Veriqa buttons and links, one more event is claimed: a pressed quick
    /// reply whose payload is a phone number. It answers the phone number request of claim
    /// completion and carries the number alone, with no marker of its own. A quick reply with any
    /// other payload is left to the code the account is shared with — quick replies are a common
    /// tool of a bot, and the button of the secondary action of the step is claimed by its marker.
    /// </remarks>
    public bool OwnsInboundEvent(ChannelInboundRequest request)
    {
        var body = request.Body.Span;

        return CallbackDataPrefixes.ContainsAnyMarker(body)
            || (body.IndexOf(MetaPhoneNumberQuickReply.QuickReplyMarker) >= 0
                && MetaPhoneNumberQuickReply.CarriesSharedPhoneNumber(body));
    }

    /// <inheritdoc />
    public async Task<Result<ChannelInboundResult>> ProcessInboundEventAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // The method maps one messaging event of a delivery to one intent: the contract answers a
        // request with a single result, so of the events Meta batched together the one that matters
        // most to a sign-in is answered and the others are not.
        if (!string.Equals(request.Method, HttpMethods.Post, StringComparison.Ordinal))
        {
            return Unrelated();
        }

        MetaWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MetaWebhookPayload>(request.Body.Span);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize the Instagram webhook payload.");
            return Unrelated();
        }

        return SelectEvent(payload) is { } recognized
            ? Result<ChannelInboundResult>.Success(await ToInboundResultAsync(recognized, cancellationToken))
            : Unrelated();
    }

    /// <inheritdoc />
    public async Task<Result<ChannelMessageRef?>> SendConfirmationPromptAsync(
        TransactionId transactionId,
        string channelUserId,
        ConfirmationPromptContext promptContext,
        CancellationToken cancellationToken = default)
    {
        // The method sends one button template: the question and the two buttons under it. The
        // recipient locale travels with the context and nowhere else (SPEC-017 §7.2); Instagram
        // supplies none of its own, so it is the locale of the transaction.
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
            InstagramAdapterConstants.ConfirmButtonText, recipientLocale);
        var declineButtonText = _promptLocalizer.ResolveOrBaseText(
            InstagramAdapterConstants.DeclineButtonText, recipientLocale);

        var transactionIdString = transactionId.ToString();
        var message = new
        {
            attachment = new
            {
                type = InstagramAdapterConstants.AttachmentTypeTemplate,
                payload = new
                {
                    template_type = InstagramAdapterConstants.TemplateTypeButton,
                    text = LimitTo(messageText, InstagramAdapterConstants.ButtonTemplateTextLimit),
                    buttons = new[]
                    {
                        new
                        {
                            type = InstagramAdapterConstants.ButtonTypePostback,
                            title = confirmButtonText,
                            payload = CallbackDataPrefixes.Confirm + transactionIdString
                        },
                        new
                        {
                            type = InstagramAdapterConstants.ButtonTypePostback,
                            title = declineButtonText,
                            payload = CallbackDataPrefixes.Decline + transactionIdString
                        }
                    }
                }
            }
        };

        return await _graphApi.SendMessageAsync(channelUserId, message, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<ChannelMessageRef?>> SendMessageAsync(
        ChannelMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The phone number request (SPEC-041 R6) is the text with the quick reply of the platform
        // under it. Instagram shows that button only in the mobile app and only to a user whose
        // profile has a number; saying so is the business of the request text, which arrives here
        // already worded.
        if (message.PhoneNumberRequest is { } phoneRequest)
        {
            return _graphApi.SendMessageAsync(
                message.ChannelUserId,
                MetaPhoneNumberQuickReply.BuildRequestMessage(message.Text, phoneRequest),
                cancellationToken);
        }

        // A link to the claim completion form (SPEC-041 C76) is one link button and goes the way the
        // buttons of a post-sign-in action go (SPEC-012 §4.9).
        var actionButtons = message.ClaimFormLink is { } formLink
            ? new ActionButtonsContent
            {
                Buttons = [new ChannelActionButton { Label = formLink.ButtonLabel, Url = formLink.Url }]
            }
            : message.ActionButtons;

        return actionButtons is null
            ? _graphApi.SendMessageAsync(message.ChannelUserId, new { text = message.Text }, cancellationToken)
            : SendWithButtonsAsync(message.ChannelUserId, message.Text, actionButtons, cancellationToken);
    }

    /// <summary>
    /// Sends a text with buttons under it as a button template. A text that fits the template goes
    /// inside it. A longer one goes first as a text message of its own, and the template that follows
    /// carries the label of its first button as its text — the template refuses a text over its limit,
    /// and cutting the text would lose what the buttons are about. A message none of whose buttons can
    /// be drawn is not sent at all, neither of its parts: its text only introduces the buttons.
    /// </summary>
    /// <remarks>
    /// The two messages of a long text are not one delivery: the text may arrive while the template is
    /// refused, and the send is then answered with the refusal. The adapter keeps no state, so a retry
    /// of the core sends the text again.
    /// </remarks>
    /// <param name="channelUserId">Instagram-scoped ID of the recipient.</param>
    /// <param name="text">Text of the message, already worded.</param>
    /// <param name="content">Buttons of the message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reference to the delivered template, or a failure.</returns>
    private async Task<Result<ChannelMessageRef?>> SendWithButtonsAsync(
        string channelUserId,
        string text,
        ActionButtonsContent content,
        CancellationToken cancellationToken)
    {
        var buttons = ActionButtonSelection.SelectDrawable(
            content, ButtonTemplateLimits, ChannelTypes.Instagram, _logger);
        if (buttons.Count == 0)
        {
            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "None of the action buttons of the message can be drawn in Instagram");
        }

        if (text.Length <= InstagramAdapterConstants.ButtonTemplateTextLimit)
        {
            return await _graphApi.SendMessageAsync(
                channelUserId, MetaButtonTemplate.BuildMessage(text, buttons), cancellationToken);
        }

        var textResult = await _graphApi.SendMessageAsync(
            channelUserId,
            new { text = LimitToUtf8Bytes(text, InstagramAdapterConstants.TextMessageByteLimit) },
            cancellationToken);
        if (textResult.IsFailure)
        {
            return textResult;
        }

        var templateText = LimitTo(buttons[0].Label, InstagramAdapterConstants.ButtonTemplateTextLimit);

        return await _graphApi.SendMessageAsync(
            channelUserId, MetaButtonTemplate.BuildMessage(templateText, buttons), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ValidateWebhookAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // The method proves a request comes from Meta: the GET handshake by the verify token, a
        // POST delivery by the signature of its body. Both secrets are the tenant's, so the
        // credentials are resolved for the tenant of this very request.
        var isHandshake = string.Equals(request.Method, HttpMethods.Get, StringComparison.Ordinal);
        if (!isHandshake && !string.Equals(request.Method, HttpMethods.Post, StringComparison.Ordinal))
        {
            return Result<bool>.Success(false);
        }

        var credentialsResult = await InstagramCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogError("Instagram webhook rejected: credentials are unavailable.");
            return Result<bool>.Success(false);
        }

        return Result<bool>.Success(isHandshake
            ? MetaWebhookVerification.IsValidHandshake(request, credentialsResult.Value.WebhookVerifyToken, _logger)
            : MetaWebhookVerification.IsValidSignature(request, credentialsResult.Value.AppSecret, _logger));
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
        var credentialsResult = await InstagramCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);

        var isHealthy = credentialsResult.TryGetValue(out var credentials)
            && !string.IsNullOrWhiteSpace(credentials.AppSecret)
            && !string.IsNullOrWhiteSpace(credentials.WebhookVerifyToken)
            && !string.IsNullOrWhiteSpace(credentials.AccessToken)
            && !string.IsNullOrWhiteSpace(credentials.AccountId)
            && !string.IsNullOrWhiteSpace(credentials.Username);

        var checkedAt = _timeProvider.GetUtcNow();

        return Result<ChannelHealthStatus>.Success(new ChannelHealthStatus(
            IsHealthy: isHealthy,
            ChannelType: ChannelTypes.Instagram,
            ResponseTime: checkedAt - startedAt,
            Details: isHealthy ? null : "Instagram credentials are unavailable or incomplete.",
            CheckedAt: checkedAt));
    }

    /// <inheritdoc />
    public async Task<Result<string>> GetDeepLinkAsync(
        DeepLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The method builds the ig.me link that opens the Direct thread with the tenant's account.
        // Its ref parameter names the transaction; Instagram hands it back in the referral event.
        var credentialsResult = await InstagramCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogError("Instagram credentials are unavailable for the deep link.");
            return Result<string>.Failure(
                ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                "Instagram credentials are unavailable for the deep link.");
        }

        // A username arriving from an upper configuration layer has not passed the setter of the
        // options, so the at-sign it is commonly written with is dropped here as well.
        var username = credentialsResult.Value.Username?.Trim().TrimStart(InstagramAdapterConstants.UsernamePrefix);
        if (string.IsNullOrEmpty(username))
        {
            return Result<string>.Failure(
                ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                "Username is not configured for the Instagram deep link.");
        }

        var deepLink = string.Concat(
            InstagramAdapterConstants.DeepLinkBaseUrl,
            Uri.EscapeDataString(username),
            "?",
            InstagramAdapterConstants.DeepLinkReferralQueryKey,
            "=",
            Uri.EscapeDataString(CallbackDataPrefixes.Auth + request.TransactionId.ToString()));

        return Result<string>.Success(deepLink);
    }

    /// <inheritdoc />
    public ValueTask<string?> GetBotNameAsync(CancellationToken cancellationToken = default) =>
        // The method names the chat the way the user sees it: the username of the tenant's account
        // behind an at-sign. The name only decorates a text, so a failure to obtain it is answered
        // with no name and never thrown: the text then goes without the name. A cancelled request is
        // not such a failure: the cancellation goes on to the caller.
        // Credentials that do not resolve give no username, and that is not reported as a failure here.
        ChannelBotNameReader.ReadAsync(
            async token =>
            {
                var credentialsResult = await InstagramCredentialsResolver.ResolveAsync(_resolver, _logger, token);

                return credentialsResult.IsFailure ? null : credentialsResult.Value.Username;
            },
            InstagramAdapterConstants.UsernamePrefix,
            ex => _logger.LogWarning(ex, "Failed to resolve the Instagram username for the chat name"),
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<bool>> ReportOutcomeAsync(
        TransactionOutcomeNotice notice,
        CancellationToken cancellationToken = default)
    {
        // Instagram has neither callback answers nor an editable delivered message: the only way to
        // show a terminal state is a new message. The status text is already localized by the core.
        ArgumentNullException.ThrowIfNull(notice);

        var sendResult = await _graphApi.SendMessageAsync(
            notice.ChannelUserId,
            new { text = notice.StatusText },
            cancellationToken);

        return sendResult.IsSuccess
            ? Result<bool>.Success(true)
            : Result<bool>.Failure(sendResult.Error);
    }

    /// <summary>
    /// Picks the one event of a delivery the core is answered about. The contract answers a request
    /// with a single result, while Meta may batch several events into it, so the events are ranked
    /// by what they are worth to a sign-in: an event that names a transaction by its marker comes
    /// first, a pressed item of the post-sign-in menu — marked too, yet about no transaction — second,
    /// a message that can be the code of the sign-in page third, a shared phone number fourth, a
    /// pressed button that names no transaction last. Among equals the earlier wins.
    /// </summary>
    /// <remarks>
    /// The rank is what keeps a marked event from being lost: a delivery is claimed for its marker
    /// (<see cref="OwnsInboundEvent"/>), and an event of another user that names no transaction,
    /// standing before the marked event in the same delivery, must not be answered in its place. A
    /// message of digits names none either until the core finds a transaction by it — it may be a
    /// number of anything — so it starts a sign-in ahead of the rest, yet never in place of an event
    /// whose marker names one.
    /// </remarks>
    /// <param name="payload">Envelope of the delivery; null for a body that is a JSON <c>null</c>.</param>
    /// <returns>The event to answer, or null when the delivery carries nothing about a sign-in.</returns>
    private static RecognizedEvent? SelectEvent(MetaWebhookPayload? payload)
    {
        RecognizedEvent? menuItem = null;
        RecognizedEvent? startByCode = null;
        RecognizedEvent? sharedNumber = null;
        RecognizedEvent? selected = null;

        foreach (var entry in payload?.Entry ?? [])
        {
            foreach (var messagingEvent in entry?.Messaging ?? [])
            {
                if (messagingEvent is null || TryRecognize(messagingEvent) is not { } recognized)
                {
                    continue;
                }

                switch (recognized.Intent)
                {
                    // A press of the menu is answered by the core, not by a transaction: it must
                    // not take the place of an event of another user in the same delivery that
                    // names one.
                    case InboundIntent.MenuItemPressed:
                        menuItem ??= recognized;
                        break;

                    case InboundIntent.StartByCode:
                        startByCode ??= recognized;
                        break;

                    case InboundIntent.Unaddressed:
                        selected ??= recognized;
                        break;

                    // A quick reply ranks as a shared number only when its payload has an E.164
                    // form — the condition the delivery is claimed on. Any other quick reply is a
                    // button of other code and ranks with the events that name no transaction.
                    case InboundIntent.PhoneShared
                        when E164PhoneNumber.TryNormalize(recognized.SharedPhoneNumber, out _):
                        sharedNumber ??= recognized;
                        break;

                    case InboundIntent.PhoneShared:
                        selected ??= recognized;
                        break;

                    default:
                        return recognized;
                }
            }
        }

        return menuItem ?? startByCode ?? sharedNumber ?? selected;
    }

    /// <summary>
    /// Reads one messaging event down to its meaning for the core. The order of the checks is the
    /// order of trust in the carrier: the referral of an opened link, a pressed button, a typed text.
    /// </summary>
    /// <param name="messagingEvent">Messaging event of a delivery.</param>
    /// <returns>
    /// The recognized event, or null for one that is not about a sign-in: an echo of the account's
    /// own message, a reaction, an event without a sender, an event that is neither a message nor a
    /// button, a button of the phone step whose transaction cannot be read, a menu button whose
    /// payload is the marker alone, a typed message that is neither the marker of a sign-in nor a
    /// possible code.
    /// </returns>
    private static RecognizedEvent? TryRecognize(MetaMessagingEvent messagingEvent)
    {
        var senderId = messagingEvent.Sender?.Id;
        if (string.IsNullOrWhiteSpace(senderId)
            || messagingEvent.Reaction is not null
            || messagingEvent.Message is { IsEcho: true })
        {
            return null;
        }

        // A link opened into an existing thread arrives as a referral of its own; a link that opens
        // the thread for the first time arrives inside what the user did first there — the postback
        // of a pressed conversation starter, or the message sent from the composer.
        if (TryReadTransactionId(messagingEvent.Referral?.Ref, CallbackDataPrefixes.Auth, out var transactionId)
            || TryReadTransactionId(messagingEvent.Postback?.Referral?.Ref, CallbackDataPrefixes.Auth, out transactionId)
            || TryReadTransactionId(messagingEvent.Message?.Referral?.Ref, CallbackDataPrefixes.Auth, out transactionId))
        {
            return new RecognizedEvent(InboundIntent.Start, senderId, transactionId);
        }

        if (TryReadTransactionId(messagingEvent.Postback?.Payload, CallbackDataPrefixes.Confirm, out transactionId))
        {
            return new RecognizedEvent(InboundIntent.Confirm, senderId, transactionId);
        }

        if (TryReadTransactionId(messagingEvent.Postback?.Payload, CallbackDataPrefixes.Decline, out transactionId))
        {
            return new RecognizedEvent(InboundIntent.Decline, senderId, transactionId);
        }

        // A pressed item of the post-sign-in menu is told by its marker before a button that names
        // nothing: it is not a user asking to sign in, so it never gets the reply of how to go on. A
        // payload of the marker alone stands for nothing and leaves the event unrelated.
        var postbackPayload = messagingEvent.Postback?.Payload;
        if (MenuItemPayload.TryRead(postbackPayload, out var menuItemValue))
        {
            return new RecognizedEvent(
                InboundIntent.MenuItemPressed,
                senderId,
                default,
                MenuItemValue: menuItemValue,
                PressId: messagingEvent.Postback?.Mid is { Length: > 0 } mid ? mid : null);
        }

        if (MenuItemPayload.HasMarker(postbackPayload))
        {
            return null;
        }

        // A pressed button that is none of the above names no transaction. The first press of the
        // conversation starter of a new user may arrive without the referral of the link that
        // opened the thread, and a user who opened the thread without any link has none to bring. Such
        // a press is a user asking to sign in, so it is handed on with its sender: the reply the
        // registration declares tells them how to go on, unless the deployment switched it off.
        if (messagingEvent.Postback is not null)
        {
            return new RecognizedEvent(InboundIntent.Unaddressed, senderId, default);
        }

        if (messagingEvent.Message is null)
        {
            return null;
        }

        // A quick reply is told from a typed message by the member only a pressed button adds. A
        // typed message never reaches this branch, whatever its text is — a phone number included.
        if (messagingEvent.Message.QuickReply is { } quickReply)
        {
            return RecognizeQuickReply(senderId, quickReply.Payload);
        }

        // The marker typed (or pasted) as a message starts the sign-in as the link does; a message
        // that can be the code of the sign-in page is handed on as one. What a code looks like is the
        // core's to say: digits within the bounds a deployment may state, so a number shorter or
        // longer than any code stays a plain message and costs no read of the profile. Digits within
        // those bounds are handed on as a code whatever the user meant by them — a phone number typed
        // without its plus sign included.
        //
        // Any other typed message is not about a sign-in and is left unanswered: a person writing to
        // the account about something else is not asking to sign in, and the reply to an event that
        // names no transaction is kept for the pressed button above.
        var text = messagingEvent.Message.Text?.Trim();
        if (TryReadTransactionId(text, CallbackDataPrefixes.Auth, out transactionId))
        {
            return new RecognizedEvent(InboundIntent.Start, senderId, transactionId);
        }

        return ChannelStartCodeText.CanBeCode(text)
            ? new RecognizedEvent(InboundIntent.StartByCode, senderId, default, StartCode: text)
            : null;
    }

    /// <summary>
    /// Reads a pressed quick reply. The phone number request is the only message of this adapter
    /// that carries quick replies, and it carries two: the button of the secondary action, whose
    /// payload is the marker of the action followed by the transaction, and the button of the
    /// platform, whose payload is the phone number itself. The event names no kind of the button,
    /// so the payload is what tells them apart.
    /// </summary>
    /// <remarks>
    /// A payload that is neither is read as a shared number with no E.164 form, which the core counts
    /// as absent. <see cref="OwnsInboundEvent"/> does not claim such an event: on an account shared
    /// with other code it is a quick reply of that code.
    /// </remarks>
    /// <param name="senderId">Instagram-scoped ID of the user who pressed the button.</param>
    /// <param name="payload">Payload of the pressed button; null when the event carries none.</param>
    /// <returns>
    /// The recognized event, or null for a button of the phone step whose transaction cannot be read.
    /// </returns>
    private static RecognizedEvent? RecognizeQuickReply(string senderId, string? payload)
    {
        if (payload is not null
            && PhoneStepButtonPayload.TryRead(payload, out var action, out var rawTransactionId))
        {
            return TransactionId.TryParse(rawTransactionId, out var transactionId)
                ? new RecognizedEvent(InboundIntent.PhoneStepAction, senderId, transactionId, PhoneStepAction: action)
                : null;
        }

        return new RecognizedEvent(InboundIntent.PhoneShared, senderId, default, SharedPhoneNumber: payload);
    }

    /// <summary>
    /// Turns a recognized event into the result the core acts on. A start and a start by code carry the
    /// profile of the user: the webhook names only the scoped ID, and without the read every claim would
    /// be that ID. A "Yes" and a "No" carry the sender alone: the core decides them with the profile the
    /// start read, which it keeps with the binding of the subject. A pressed button that names no
    /// transaction, an answer of the phone step and a pressed menu item are not worth a network call
    /// either: the core reads nothing but the sender off them.
    /// </summary>
    /// <param name="recognized">Recognized messaging event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Inbound result typed by intent.</returns>
    private async Task<ChannelInboundResult> ToInboundResultAsync(
        RecognizedEvent recognized,
        CancellationToken cancellationToken)
    {
        switch (recognized.Intent)
        {
            case InboundIntent.Unaddressed:
                // Whether such a press is answered at all is the deployment's to say. With the reply
                // switched off the press is handed on as an event the core has nothing to do with, so
                // nothing is sent to the user.
                var replyEnabled = await _resolver.ResolveAsync(
                    InstagramConfigKeys.UnaddressedReplyEnabled,
                    ResolutionContext.ForTenant(ChannelTenantContext.CurrentTenantId),
                    ConfigDimensionValues.None,
                    cancellationToken);

                if (!replyEnabled.Value)
                {
                    return new ChannelUnrelatedResult();
                }

                return new ChannelUnaddressedResult
                {
                    Identity = BuildIdentitySnapshot(recognized.SenderId, profile: null)
                };

            case InboundIntent.PhoneShared:
                return new ChannelPhoneSharedResult
                {
                    // The number crosses this boundary in E.164 or not at all (SPEC-041 L47): a value
                    // with no E.164 form is handed on empty, which the core counts as absent. The
                    // channel has no check of whose number it is, so it never rejects the ownership.
                    PhoneNumber = E164PhoneNumber.TryNormalize(recognized.SharedPhoneNumber, out var phoneNumber)
                        ? phoneNumber
                        : string.Empty,
                    Identity = BuildIdentitySnapshot(recognized.SenderId, profile: null)
                };

            case InboundIntent.PhoneStepAction:
                return new ChannelPhoneStepActionResult
                {
                    TransactionId = recognized.TransactionId,
                    Identity = BuildIdentitySnapshot(recognized.SenderId, profile: null),
                    Action = recognized.PhoneStepAction
                };

            // The value goes to the host as the operator stated it; the press needs no answer of its
            // own to the platform.
            case InboundIntent.MenuItemPressed:
                return new ChannelMenuItemPressedResult
                {
                    Identity = BuildIdentitySnapshot(recognized.SenderId, profile: null),
                    Value = recognized.MenuItemValue ?? string.Empty,
                    PressId = recognized.PressId
                };

            // "Yes" and "No" are decided with the profile of the start, so the press reads no profile.
            case InboundIntent.Confirm:
                return new ChannelAuthConfirmResult
                {
                    TransactionId = recognized.TransactionId,
                    Identity = BuildIdentitySnapshot(recognized.SenderId, profile: null)
                };

            case InboundIntent.Decline:
                return new ChannelAuthDeclineResult
                {
                    TransactionId = recognized.TransactionId,
                    Identity = BuildIdentitySnapshot(recognized.SenderId, profile: null)
                };
        }

        var identity = BuildIdentitySnapshot(
            recognized.SenderId,
            await _graphApi.GetUserProfileAsync(recognized.SenderId, cancellationToken));

        return recognized.Intent switch
        {
            // A start by code is a start: the identity is read the way the start by link reads it.
            InboundIntent.StartByCode => new ChannelAuthStartByCodeResult
            {
                Code = recognized.StartCode ?? string.Empty,
                Identity = identity
            },
            _ => new ChannelAuthStartResult
            {
                TransactionId = recognized.TransactionId,
                Identity = identity
            }
        };
    }

    /// <summary>
    /// Builds the identity snapshot of an Instagram user.
    /// </summary>
    /// <param name="userId">Instagram-scoped ID of the user.</param>
    /// <param name="profile">Profile of the user; null when it was not read — the ID alone identifies them.</param>
    /// <returns>Channel identity snapshot.</returns>
    private ChannelIdentitySnapshot BuildIdentitySnapshot(string userId, InstagramUserProfile? profile)
    {
        var name = TrimOrNull(profile?.Name);
        var username = TrimOrNull(profile?.Username);

        return new ChannelIdentitySnapshot
        {
            // Tenant of the inbound request, taken from the ambient scope the host opened for it —
            // the {tenant} segment of the webhook route. The identity of a channel user belongs to
            // the tenant whose channel received the event.
            TenantId = ChannelTenantContext.CurrentTenantId,
            ChannelType = ChannelTypes.Instagram,
            IsBot = false,
            ChannelUserId = userId,
            DisplayName = name ?? username ?? userId,
            Username = username,
            AvatarUrl = TrimOrNull(profile?.ProfilePic),
            CapturedAt = _timeProvider.GetUtcNow(),
            AdapterVersion = InstagramAdapterConstants.AdapterVersion
        };
    }

    /// <summary>
    /// Cuts a text to a limit the platform sets. A text over the limit of a button template makes
    /// the platform refuse the whole message, buttons included.
    /// </summary>
    /// <param name="text">Text to send.</param>
    /// <param name="limit">Maximum length (characters) the platform accepts.</param>
    /// <returns>The text as is, or its beginning followed by the truncation mark.</returns>
    private static string LimitTo(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        var keep = limit - InstagramAdapterConstants.TruncationMark.Length;

        // A cut between the two halves of a surrogate pair would leave a broken character behind.
        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep), InstagramAdapterConstants.TruncationMark);
    }

    /// <summary>
    /// Cuts a text to a limit the platform sets in bytes of UTF-8: the beginning of the text followed by
    /// the truncation mark, the limit long or shorter in all. The cut falls between characters, never
    /// inside one, so no byte sequence is left broken.
    /// </summary>
    /// <param name="text">Text to send.</param>
    /// <param name="byteLimit">Maximum size (bytes of UTF-8) the platform accepts.</param>
    /// <returns>The text as is, or its beginning followed by the truncation mark.</returns>
    private string LimitToUtf8Bytes(string text, int byteLimit)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount <= byteLimit)
        {
            return text;
        }

        _logger.LogWarning(
            "The text of an Instagram message is cut: it is {ByteCount} bytes of UTF-8 long, the platform limit is {ByteLimit} bytes",
            byteCount,
            byteLimit);

        var budget = byteLimit - Encoding.UTF8.GetByteCount(InstagramAdapterConstants.TruncationMark);
        var keptBytes = 0;
        var keptChars = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            if (keptBytes + rune.Utf8SequenceLength > budget)
            {
                break;
            }

            keptBytes += rune.Utf8SequenceLength;
            keptChars += rune.Utf16SequenceLength;
        }

        return string.Concat(text.AsSpan(0, keptChars), InstagramAdapterConstants.TruncationMark);
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

    /// <summary>
    /// The answer to a request that carries nothing about a sign-in.
    /// </summary>
    /// <returns>Successful result with <see cref="ChannelUnrelatedResult"/>.</returns>
    private static Result<ChannelInboundResult> Unrelated() =>
        Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult());
}
