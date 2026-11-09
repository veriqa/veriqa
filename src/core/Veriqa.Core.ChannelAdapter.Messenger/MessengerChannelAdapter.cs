// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Messenger.Configuration;
using Veriqa.Core.ChannelAdapter.Messenger.Constants;
using Veriqa.Core.ChannelAdapter.Meta;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Messenger;

/// <summary>
/// Messenger channel adapter (SPEC-003 §6): a sign-in started by an <c>m.me</c> link and confirmed
/// in the Messenger thread with a Facebook Page. The link carries the transaction in its <c>ref</c>
/// parameter; opening it delivers a referral event that starts the sign-in, the user is asked with
/// a button template, and the outcome arrives as a message of its own.
/// </summary>
internal sealed class MessengerChannelAdapter : IChannelAdapter
{
    /// <summary>
    /// Facts Messenger declares about itself (SPEC-003 §6.1): buttons of a template let the user
    /// confirm inside the channel; a message is plain text; a delivered message cannot be edited,
    /// so the outcome is a new message; the recipient locale is the one of the user's profile,
    /// read on the start of a sign-in (a pressed "Yes" or "No" reads no profile: it is decided with the
    /// profile of the start). The webhook carries none, and the profile yields it only
    /// to an app granted a permission of its own — without it the snapshot has no locale, and the
    /// recipient-locale chain falls back to the locale of the transaction.
    /// </summary>
    /// <remarks>
    /// The phone number comes on request, by a quick reply the platform fills with the number of
    /// the user's profile (SPEC-041 R6). The platform says nothing about having checked that
    /// number, so it is not proven to belong to the sender (SPEC-041 L45).
    /// <para>
    /// A link to the claim completion form is a URL button of a button template (SPEC-041 C76).
    /// After a completed sign-in the thread can be sent a welcome message, a menu or a link to
    /// continue with (SPEC-012 §4.9); the last two are a button template whose buttons are URL
    /// buttons and postbacks.
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
        SupportedCompletionBehaviorActions = new[]
        {
            CompletionBehaviorAction.None,
            CompletionBehaviorAction.WelcomeMessage,
            CompletionBehaviorAction.Menu,
            CompletionBehaviorAction.Redirect
        }.ToFrozenSet(),
        DefaultCompletionBehaviorAction = CompletionBehaviorAction.None,
        SupportsMessageUpdate = false,
        DeliversOutcomeNotice = true,
        ProvidesRecipientLocale = true,
        RendersEmoji = true,
        PhoneNumber = new PhoneNumberCapability
        {
            Acquisition = PhoneNumberAcquisition.OnRequest,
            Proof = PhoneNumberProof.NotProven
        },

        // The link carries the transaction, but the platform does not always hand it back: the first
        // press of a new user may arrive with no referral. The code of the sign-in page is the
        // fallback start beside the link, and the user sends it alone, with no command around it.
        CodeStart = new ChannelCodeStart { LinkCarriesTransaction = true }
    };

    /// <summary>
    /// What a button template of Messenger lets the buttons of a message be: at most three, link
    /// buttons included, with the title and the postback payload within the lengths Meta documents.
    /// The platform names no limit for the address of a URL button, so none is checked beforehand.
    /// </summary>
    private static readonly ActionButtonLimits ButtonTemplateLimits = new()
    {
        MaxButtons = MetaButtonTemplate.MaxButtons,
        MaxLabelLength = MessengerAdapterConstants.ButtonTitleLimit,
        MaxCallbackDataLength = MessengerAdapterConstants.PostbackPayloadLimit
    };

    /// <summary>
    /// Outbound calls of the channel: the Send API and the user profile read.
    /// </summary>
    private readonly MessengerGraphApiClient _graphApi;

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
    private readonly ILogger<MessengerChannelAdapter> _logger;

    /// <summary>
    /// Creates an instance of <see cref="MessengerChannelAdapter"/>.
    /// </summary>
    /// <param name="graphApi">Outbound calls of the channel.</param>
    /// <param name="resolver">Canonical layer resolver (source of the tenant's credentials).</param>
    /// <param name="promptLocalizer">Channel message localizer.</param>
    /// <param name="messageTemplates">Accessor of the channel message templates of this adapter.</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public MessengerChannelAdapter(
        MessengerGraphApiClient graphApi,
        IConfigurationResolver resolver,
        IConfirmationPromptLocalizer promptLocalizer,
        IChannelMessageTemplates messageTemplates,
        TimeProvider timeProvider,
        ILogger<MessengerChannelAdapter> logger)
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
        /// A pressed callback item of the post-sign-in menu (SPEC-012 §4.9).
        /// </summary>
        MenuItemPressed
    }

    /// <summary>
    /// A messaging event read down to its meaning for the core.
    /// </summary>
    /// <param name="Intent">What the event asks for.</param>
    /// <param name="SenderId">Page-scoped ID of the user the event comes from.</param>
    /// <param name="TransactionId">
    /// Transaction the event names; default for an event that names none — a pressed button without
    /// one, a pressed item of the menu, a sent code and a shared phone number.
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
    /// Value of the pressed menu item, the marker cut off — set for
    /// <see cref="InboundIntent.MenuItemPressed"/> only.
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
    public string ChannelType => ChannelTypes.Messenger;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities => DeclaredCapabilities;

    /// <inheritdoc />
    /// <remarks>
    /// Besides the markers of Veriqa buttons and links, one more event is claimed: a pressed quick
    /// reply whose payload is a phone number. It answers the phone number request of claim
    /// completion and carries the number alone, with no marker of its own. A quick reply with any
    /// other payload is left to the code the Page is shared with — quick replies are a common tool
    /// of a bot, and the button of the secondary action of the step is claimed by its marker.
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
            _logger.LogWarning(ex, "Failed to deserialize the Messenger webhook payload.");
            return Result<ChannelInboundResult>.Failure(
                ChannelAdapterErrorCodes.UnsupportedEventType,
                "The Messenger webhook body is not valid JSON.");
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
        // recipient locale travels with the context and nowhere else (SPEC-017 §7.2): the locale of
        // the user's profile when the app may read it, the locale of the transaction otherwise.
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
            MessengerAdapterConstants.ConfirmButtonText, recipientLocale);
        var declineButtonText = _promptLocalizer.ResolveOrBaseText(
            MessengerAdapterConstants.DeclineButtonText, recipientLocale);

        var transactionIdString = transactionId.ToString();
        var message = new
        {
            attachment = new
            {
                type = MessengerAdapterConstants.AttachmentTypeTemplate,
                payload = new
                {
                    template_type = MessengerAdapterConstants.TemplateTypeButton,
                    text = LimitTo(messageText, MessengerAdapterConstants.ButtonTemplateTextLimit),
                    buttons = new[]
                    {
                        new
                        {
                            type = MessengerAdapterConstants.ButtonTypePostback,
                            title = confirmButtonText,
                            payload = CallbackDataPrefixes.Confirm + transactionIdString
                        },
                        new
                        {
                            type = MessengerAdapterConstants.ButtonTypePostback,
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
        // under it. Messenger shows that button only to a user whose profile has a number; saying
        // so is the business of the request text, which arrives here already worded.
        if (message.PhoneNumberRequest is { } phoneRequest)
        {
            return _graphApi.SendMessageAsync(
                message.ChannelUserId,
                MetaPhoneNumberQuickReply.BuildRequestMessage(message.Text, phoneRequest),
                cancellationToken);
        }

        // A link to the claim completion form is one URL button under the text (SPEC-041 C76), drawn
        // the way a single link of the post-sign-in buttons is: the platform opens the page, and the
        // Page never sees the press.
        if (message.ClaimFormLink is { } formLink)
        {
            var formLinkButtons = new ActionButtonsContent
            {
                Buttons = [new ChannelActionButton { Label = formLink.ButtonLabel, Url = formLink.Url }]
            };

            return SendButtonTemplateAsync(message.ChannelUserId, message.Text, formLinkButtons, cancellationToken);
        }

        // Buttons of a post-sign-in action are a button template under the text (SPEC-012 §4.9).
        if (message.ActionButtons is { } actionButtons)
        {
            return SendButtonTemplateAsync(message.ChannelUserId, message.Text, actionButtons, cancellationToken);
        }

        return _graphApi.SendMessageAsync(message.ChannelUserId, new { text = message.Text }, cancellationToken);
    }

    /// <summary>
    /// Sends a text with buttons under it as a button template. The buttons the template can hold are
    /// chosen by the rules every adapter shares: the first three that are well formed and fit the
    /// lengths of the platform, their titles cut to its limit; a text over the limit of the template is
    /// cut the same way, with a warning. A message none of whose buttons can be drawn is not sent at
    /// all: its text only introduces the buttons.
    /// </summary>
    /// <remarks>
    /// Nothing a button carries is logged here: the address of a link may hold the claim form token or
    /// a link handle (SPEC-041 N78), and the payload is the operator's data.
    /// </remarks>
    /// <param name="recipientId">Page-scoped ID of the recipient.</param>
    /// <param name="text">Text of the message, already localized.</param>
    /// <param name="content">Buttons of the message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the send; a failure when no button can be drawn.</returns>
    private Task<Result<ChannelMessageRef?>> SendButtonTemplateAsync(
        string recipientId,
        string text,
        ActionButtonsContent content,
        CancellationToken cancellationToken)
    {
        var buttons = ActionButtonSelection.SelectDrawable(content, ButtonTemplateLimits, ChannelType, _logger);
        if (buttons.Count == 0)
        {
            return Task.FromResult(Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "None of the buttons of the message can be drawn in Messenger"));
        }

        var wireMessage = MetaButtonTemplate.BuildMessage(
            ActionButtonSelection.FitText(text, MessengerAdapterConstants.ButtonTemplateTextLimit, ChannelType, _logger),
            buttons);

        return _graphApi.SendMessageAsync(recipientId, wireMessage, cancellationToken);
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

        var credentialsResult = await MessengerCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogError("Messenger webhook rejected: credentials are unavailable.");
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
        var credentialsResult = await MessengerCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);

        // The username of the Page is needed only by the link built from it.
        var linkByPageId = await IsDeepLinkByPageIdAsync(cancellationToken);

        var isHealthy = credentialsResult.TryGetValue(out var credentials)
            && !string.IsNullOrWhiteSpace(credentials.PageId)
            && (linkByPageId || !string.IsNullOrWhiteSpace(credentials.PageName))
            && !string.IsNullOrWhiteSpace(credentials.PageAccessToken)
            && !string.IsNullOrWhiteSpace(credentials.AppSecret)
            && !string.IsNullOrWhiteSpace(credentials.WebhookVerifyToken);

        var checkedAt = _timeProvider.GetUtcNow();

        return Result<ChannelHealthStatus>.Success(new ChannelHealthStatus(
            IsHealthy: isHealthy,
            ChannelType: ChannelTypes.Messenger,
            ResponseTime: checkedAt - startedAt,
            Details: isHealthy ? null : "Messenger credentials are unavailable or incomplete.",
            CheckedAt: checkedAt));
    }

    /// <inheritdoc />
    public async Task<Result<string>> GetDeepLinkAsync(
        DeepLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The method builds the m.me link that opens the Messenger thread with the tenant's Page.
        // Its ref parameter names the transaction; Messenger hands it back in the referral event.
        var credentialsResult = await MessengerCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogError("Messenger credentials are unavailable for the deep link.");
            return Result<string>.Failure(
                ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                "Messenger credentials are unavailable for the deep link.");
        }

        // What the link names the Page by is the say of the tenant the link is built for: its
        // identifier, or — the documented form and the default — its username.
        string? pageAddress;
        if (await IsDeepLinkByPageIdAsync(cancellationToken))
        {
            pageAddress = credentialsResult.Value.PageId?.Trim();
            if (string.IsNullOrEmpty(pageAddress))
            {
                return Result<string>.Failure(
                    ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                    "PageId is not configured for the Messenger deep link.");
            }
        }
        else
        {
            // A Page username arriving from an upper configuration layer has not passed the setter
            // of the options, so the at-sign it is commonly written with is dropped here as well.
            pageAddress = credentialsResult.Value.PageName?.Trim().TrimStart(MessengerAdapterConstants.PageNamePrefix);
            if (string.IsNullOrEmpty(pageAddress))
            {
                return Result<string>.Failure(
                    ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                    "PageName is not configured for the Messenger deep link.");
            }
        }

        var deepLink = string.Concat(
            MessengerAdapterConstants.DeepLinkBaseUrl,
            Uri.EscapeDataString(pageAddress),
            "?",
            MessengerAdapterConstants.DeepLinkReferralQueryKey,
            "=",
            Uri.EscapeDataString(CallbackDataPrefixes.Auth + request.TransactionId.ToString()));

        return Result<string>.Success(deepLink);
    }

    /// <summary>
    /// Reads, for the tenant of the current scope, whether the sign-in link names the Page by its
    /// identifier rather than by its username.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the link is built from the identifier of the Page.</returns>
    private async ValueTask<bool> IsDeepLinkByPageIdAsync(CancellationToken cancellationToken)
    {
        var byPageId = await _resolver.ResolveAsync(
            MessengerConfigKeys.DeepLinkByPageId,
            ResolutionContext.ForTenant(ChannelTenantContext.CurrentTenantId),
            ConfigDimensionValues.None,
            cancellationToken);

        return byPageId.Value;
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ReportOutcomeAsync(
        TransactionOutcomeNotice notice,
        CancellationToken cancellationToken = default)
    {
        // Messenger has neither callback answers nor an editable delivered message: the only way to
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
                    // button of other code on the Page and ranks with the events that name no
                    // transaction.
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
    /// The recognized event, or null for one that is not about a sign-in: an echo of the Page's own
    /// message, an event without a sender, an event that is neither a message nor a button, a button
    /// of the phone step whose transaction cannot be read, a menu button whose payload is the marker
    /// alone, a typed message that is neither the marker of a sign-in nor a possible code.
    /// </returns>
    private static RecognizedEvent? TryRecognize(MetaMessagingEvent messagingEvent)
    {
        // The echo of a message the Page sent names the Page as its sender: read as a message of a
        // user it would make the Page a party of its own sign-in.
        var senderId = messagingEvent.Sender?.Id;
        if (string.IsNullOrWhiteSpace(senderId) || messagingEvent.Message is { IsEcho: true })
        {
            return null;
        }

        // A link opened into an existing thread arrives as a referral of its own; a link that opens
        // the thread for the first time arrives inside the postback of the pressed Get Started
        // button. A message carries a referral of the entry point the thread was opened from as well.
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

        // A pressed callback item of the post-sign-in menu is read before the press that names no
        // transaction, so it never ends up as a user asking to sign in. The marker alone, with no
        // value after it, names no item: such a press is a Veriqa payload that stands for nothing and
        // is not answered at all.
        if (MenuItemPayload.TryRead(messagingEvent.Postback?.Payload, out var menuItemValue))
        {
            return new RecognizedEvent(
                InboundIntent.MenuItemPressed,
                senderId,
                default,
                MenuItemValue: menuItemValue,
                PressId: messagingEvent.Postback?.Mid is { Length: > 0 } mid ? mid : null);
        }

        if (MenuItemPayload.HasMarker(messagingEvent.Postback?.Payload))
        {
            return null;
        }

        // A pressed button that is none of the above names no transaction. The first press of the
        // Get Started button of a new user may arrive without the referral of the link that
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
        // the Page about something else is not asking to sign in, and the reply to an event that
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
    /// as absent. <see cref="OwnsInboundEvent"/> does not claim such an event: on a Page shared with
    /// other code it is a quick reply of that code.
    /// </remarks>
    /// <param name="senderId">Page-scoped ID of the user who pressed the button.</param>
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
    /// transaction, an answer of the phone step and a pressed item of the menu are not worth a network
    /// call either: the core reads nothing but the sender off them.
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
                    MessengerConfigKeys.UnaddressedReplyEnabled,
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

            // A postback needs no answer to the platform, so the press is handed on as it came: the
            // value of the item with its sender.
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
    /// Builds the identity snapshot of a Messenger user.
    /// </summary>
    /// <param name="userId">Page-scoped ID of the user.</param>
    /// <param name="profile">Profile of the user; null when it was not read — the ID alone identifies them.</param>
    /// <returns>Channel identity snapshot.</returns>
    private ChannelIdentitySnapshot BuildIdentitySnapshot(string userId, MessengerUserProfile? profile)
    {
        var firstName = TrimOrNull(profile?.FirstName);
        var lastName = TrimOrNull(profile?.LastName);
        var fullName = string.Join(' ', new[] { firstName, lastName }.Where(static part => part is not null));

        return new ChannelIdentitySnapshot
        {
            // Tenant of the inbound request, taken from the ambient scope the host opened for it —
            // the {tenant} segment of the webhook route. The identity of a channel user belongs to
            // the tenant whose channel received the event.
            TenantId = ChannelTenantContext.CurrentTenantId,
            ChannelType = ChannelTypes.Messenger,
            IsBot = false,
            ChannelUserId = userId,
            DisplayName = fullName.Length > 0 ? fullName : userId,
            FirstName = firstName,
            LastName = lastName,
            AvatarUrl = TrimOrNull(profile?.ProfilePic),

            // The Graph API writes a locale as en_US; the identity carries a BCP 47 tag. It is the
            // source of the locale claim and the head of the recipient-locale chain at once.
            Locale = TrimOrNull(profile?.Locale)?.Replace(
                MessengerAdapterConstants.GraphLocaleSeparator,
                MessengerAdapterConstants.Bcp47LocaleSeparator),
            CapturedAt = _timeProvider.GetUtcNow(),
            AdapterVersion = MessengerAdapterConstants.AdapterVersion
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

        var keep = limit - MessengerAdapterConstants.TruncationMark.Length;

        // A cut between the two halves of a surrogate pair would leave a broken character behind.
        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep), MessengerAdapterConstants.TruncationMark);
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
