// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Constants;
using Veriqa.Core.ChannelAdapter.Http.Inbound;
using Veriqa.Core.ChannelAdapter.Http.Outbound;
using Veriqa.Core.ChannelAdapter.Http.Wire;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Http;

/// <summary>
/// The built-in HTTP channel wrapper (SPEC-042 R1): ONE adapter class registered once per HTTP
/// channel section, each instance serving its own registration under its own channel type. It speaks
/// to the external adapter of the channel over the wire contract of SPEC-042 §4, and holds nothing of
/// the platform's transport — no webhook of the platform, no polling, no platform secret (SPEC-042 R2,
/// R3).
/// <para>
/// The channel type, the facts and the display metadata come from the registration alone and are
/// fixed at start (SPEC-042 R1, R8). The ownership predicate of the inbound contract keeps its default
/// body: the external adapter sorts its events before it posts them (SPEC-042 R9).
/// </para>
/// </summary>
internal sealed class HttpChannelAdapter : IChannelAdapter, IChannelDisplayMetadata
{
    /// <summary>
    /// Message of the refusal of a deep link the registration may not hand out.
    /// </summary>
    private const string DeepLinkNotAdmittedMessage = "The external adapter returned a deep link that is not admitted.";

    /// <summary>
    /// Language of the Natural Keys: the language a text falls back to when no locale file covers the
    /// language of the recipient.
    /// </summary>
    private const string BaseLanguageTag = ChannelLocalizationOptions.DefaultBaseLocale;

    /// <summary>
    /// Separator of the primary subtag of a language tag (<c>pt-br</c> → <c>pt</c>).
    /// </summary>
    private const char LanguageSubtagSeparator = '-';

    /// <summary>
    /// Opening of the details of a probe that failed with an exception; the exception follows.
    /// </summary>
    private const string HealthProbeFailedMessage = "The health probe of the external adapter failed: ";

    /// <summary>
    /// Message of the refusal of an inbound request whose transport signature does not verify.
    /// </summary>
    private const string SignatureNotVerifiedMessage = "The transport signature of the inbound request does not verify.";

    /// <summary>
    /// Message of the refusal of an inbound request that arrived on the route of a tenant other than
    /// the owner of the registration.
    /// </summary>
    private const string RouteNotOfOwnerMessage = "The inbound request arrived on a route other than the one of the owner of the registration.";

    /// <summary>
    /// Message of the refusal of a request for the phone number that carries no content of the request.
    /// </summary>
    private const string PhoneNumberRequestWithoutContentMessage = "The message is a request for the phone number without the content of the request.";

    /// <summary>
    /// Message of the refusal of a link to the claim form that carries no content of the link.
    /// </summary>
    private const string ClaimFormLinkWithoutContentMessage = "The message is a link to the claim form without the content of the link.";

    /// <summary>
    /// Message of the refusal of an action message that carries no buttons.
    /// </summary>
    private const string ActionButtonsWithoutButtonsMessage = "The message is an action message without buttons.";

    /// <summary>
    /// Message of the refusal of an action message with a button that is neither a link nor an item of
    /// the menu, or an item whose value is longer than <see cref="HttpChannelWire.MaxMenuItemValueLength"/>.
    /// </summary>
    private const string MalformedActionButtonMessage = "The message carries a button that is neither a link nor an item of the menu, or an item whose value is longer than the wire carries.";

    /// <summary>
    /// The registration this instance serves.
    /// </summary>
    private readonly HttpChannelRegistration _registration;

    /// <summary>
    /// Clock of the signature window, of the moment an event is received and of the health probe.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Reader of the inbound event envelopes of this registration.
    /// </summary>
    private readonly HttpChannelInboundEventReader _inboundEvents;

    /// <summary>
    /// The one path of the outbound calls of this registration (SPEC-042 C32–C38).
    /// </summary>
    private readonly HttpChannelOutboundClient _outbound;

    /// <summary>
    /// The accessor this adapter takes the templates of its channel messages from: the core resolves
    /// them through the canonical resolver (SPEC-036 TPL-001). Singleton, like this adapter.
    /// </summary>
    private readonly IChannelMessageTemplates _messageTemplates;

    /// <summary>
    /// Localizer of the prompt and its button captions into the recipient's language (SPEC-017 §7.2).
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<HttpChannelAdapter> _logger;

    /// <summary>
    /// Creates the wrapper over one registration.
    /// </summary>
    /// <param name="registration">The registration this instance serves.</param>
    /// <param name="outbound">Transport of the outbound calls of the registration.</param>
    /// <param name="messageTemplates">Accessor of the channel message templates of this adapter.</param>
    /// <param name="promptLocalizer">Localizer of the channel texts.</param>
    /// <param name="timeProvider">Clock of the host.</param>
    /// <param name="logger">Logger.</param>
    public HttpChannelAdapter(
        HttpChannelRegistration registration,
        HttpChannelOutboundClient outbound,
        IChannelMessageTemplates messageTemplates,
        IConfirmationPromptLocalizer promptLocalizer,
        TimeProvider timeProvider,
        ILogger<HttpChannelAdapter> logger)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _inboundEvents = new HttpChannelInboundEventReader(
            registration.ChannelType,
            registration.Capabilities.PhoneNumber.Proof,
            logger);
    }

    /// <summary>
    /// Channel type — the name of the registration (SPEC-042 R1).
    /// </summary>
    public string ChannelType => _registration.ChannelType;

    /// <summary>
    /// Channel facts declared by the operator in the section of the registration (SPEC-042 R8).
    /// </summary>
    public ChannelCapabilities Capabilities => _registration.Capabilities;

    /// <summary>
    /// Name of the channel on the sign-in button; the channel type when the registration states none.
    /// </summary>
    public string DisplayName => _registration.DisplayName ?? _registration.ChannelType;

    /// <summary>
    /// Inner markup of the channel glyph stated by the registration; <c>null</c> — no icon.
    /// </summary>
    public string? IconSvgPath => _registration.IconSvgPath;

    /// <summary>
    /// Verifies the transport signature of an inbound request (SPEC-042 C31, R14): the Standard
    /// Webhooks headers against the transport secrets of the registration — either one while a
    /// rotation is in progress — within five minutes of the moment of receipt. No setting switches the
    /// check off. A registration with an owner also requires the route of its owner (SPEC-042 R60). A
    /// refusal makes the pipeline answer 403 without reading the event.
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the request is accepted; a failure <c>webhook_validation_failed</c> — refused.</returns>
    public Task<Result<bool>> ValidateWebhookAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.TryGetValue(HttpChannelWire.WebhookIdHeader, out var webhookId);
        request.Headers.TryGetValue(HttpChannelWire.WebhookTimestampHeader, out var webhookTimestamp);
        request.Headers.TryGetValue(HttpChannelWire.WebhookSignatureHeader, out var webhookSignature);

        var verified = HttpChannelSignature.Verify(
            webhookId,
            webhookTimestamp,
            webhookSignature,
            request.Body.Span,
            _registration.TransportSecrets,
            _timeProvider.GetUtcNow());

        if (!verified)
        {
            return Task.FromResult(Result<bool>.Failure(
                ChannelAdapterErrorCodes.WebhookValidationFailed,
                SignatureNotVerifiedMessage));
        }

        // SPEC-042 R60: a registration with an owner accepts events on the route of its owner alone.
        // The route without a tenant segment carries no tenant and is not the owner's route either.
        // Refused the way a signature that does not verify is — before the body is read.
        if (_registration.Tenant is { } owner
            && !string.Equals(ChannelTenantContext.CurrentTenantId, owner, StringComparison.Ordinal))
        {
            return Task.FromResult(Result<bool>.Failure(
                ChannelAdapterErrorCodes.WebhookValidationFailed,
                RouteNotOfOwnerMessage));
        }

        // The single point where an inbound request is accepted: every further condition of
        // acceptance is checked before this line and refuses with the same code.
        return Task.FromResult(Result<bool>.Success(true));
    }

    /// <summary>
    /// Reads the event envelope of a request whose signature is verified into the result of the
    /// pipeline (SPEC-042 C39); the moment of receipt is the capture moment of the identity. An
    /// envelope that does not parse is a failure the pipeline answers with 200 (SPEC-042 E52).
    /// Repeated deliveries are not deduplicated here: within its short window the pipeline drops a repeat
    /// of an accepted event whatever its <c>webhook-id</c>, and past the window the effects of the
    /// pipeline are idempotent (SPEC-042 E56). A press of a menu item and the <c>unaddressed</c> and
    /// <c>unrelated</c> events are never dropped by the window. The key of an <c>auth_confirm</c> or
    /// <c>auth_decline</c> answer includes its <c>inbound_token</c>, so a repeat is dropped only when it
    /// carries the same token, or when neither of the two carries one.
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the event, typed by intent.</returns>
    public Task<Result<ChannelInboundResult>> ProcessInboundEventAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_inboundEvents.Read(request, _timeProvider.GetUtcNow()));

    /// <summary>
    /// Asks the external adapter to put the confirmation to the user — <c>send-prompt</c> (SPEC-042 C34,
    /// L46). The text is rendered here, by the mechanism of the built-in channels: the confirmation
    /// message at the address "in-channel surface + the channel type of this registration + the owners of
    /// the transaction", in the language of the recipient; the subject of a confirmation goes as it is.
    /// The structured context travels beside it, and the adapter may render from it on its own. A
    /// message that cannot be resolved refuses the prompt before any connection.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="channelUserId">Delivery address of the recipient in the channel.</param>
    /// <param name="promptContext">Confirmation context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Coordinates of the sent prompt; <c>null</c> — the channel has none (CA-182).</returns>
    public async Task<Result<ChannelMessageRef?>> SendConfirmationPromptAsync(
        TransactionId transactionId,
        string channelUserId,
        ConfirmationPromptContext promptContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelUserId);
        ArgumentNullException.ThrowIfNull(promptContext);

        // The recipient locale travels with the context and nowhere else (SPEC-017 §7.2).
        var recipientLocale = promptContext.RecipientLocale;

        // The template is resolved by the core, which takes the address and the owners itself — the
        // in-channel surface, the channel type of this registration and the levels the transaction
        // states; a template it cannot resolve refuses the prompt before any connection.
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

        var payload = new WireSendPromptPayload
        {
            TransactionId = transactionId.ToString(),
            ChannelUserId = channelUserId,
            Prompt = HttpChannelWireMapping.Prompt(promptContext),
            Rendered = new WireRenderedPrompt
            {
                Text = text,
                ConfirmLabel = _promptLocalizer.ResolveOrBaseText(HttpChannelConstants.ConfirmButtonText, recipientLocale),
                DeclineLabel = _promptLocalizer.ResolveOrBaseText(HttpChannelConstants.DeclineButtonText, recipientLocale),
                Locale = RenderedLanguageOf(recipientLocale)
            }
        };

        var sent = await _outbound.CallAsync(
            new HttpChannelCall<WireSendPromptPayload, WireMessageResult>(
                HttpChannelWire.Operations.SendPrompt,
                transactionId,
                payload,
                HttpChannelWireJsonContext.Default.SendPromptCall,
                HttpChannelWireJsonContext.Default.MessageResponse),
            cancellationToken);

        if (sent.IsFailure)
        {
            return Result<ChannelMessageRef?>.Failure(sent.Error);
        }

        _logger.LogInformation(
            "HTTP channel {ChannelType}: confirmation prompt sent to user {ChannelUserIdHash}, transaction {TransactionId}",
            _registration.ChannelType,
            LogMasking.Fingerprint(channelUserId),
            transactionId.ToString());

        return Result<ChannelMessageRef?>.Success(HttpChannelWireMapping.MessageRef(sent.Value.Message));
    }

    /// <summary>
    /// Asks the external adapter to send a message — <c>send-message</c> (SPEC-042 C35). The kind is
    /// always one the registration declares: the pipeline checks it before the call. A message whose
    /// kind has content of its own — a request for the phone number, a link to the claim form, action
    /// buttons — travels with that content; without it, or with a malformed button, it is refused
    /// before any connection, because the adapter would have nothing to show. Content stated on a
    /// message of another kind is not carried.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Coordinates of the sent message; <c>null</c> — the channel has none (CA-182).</returns>
    public async Task<Result<ChannelMessageRef?>> SendMessageAsync(
        ChannelMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = SendMessagePayload(message);
        if (payload.IsFailure)
        {
            return Result<ChannelMessageRef?>.Failure(payload.Error);
        }

        var sent = await _outbound.CallAsync(
            new HttpChannelCall<WireSendMessagePayload, WireMessageResult>(
                HttpChannelWire.Operations.SendMessage,
                TransactionId: null,
                payload.Value,
                HttpChannelWireJsonContext.Default.SendMessageCall,
                HttpChannelWireJsonContext.Default.MessageResponse),
            cancellationToken);

        return sent.IsFailure
            ? Result<ChannelMessageRef?>.Failure(sent.Error)
            : Result<ChannelMessageRef?>.Success(HttpChannelWireMapping.MessageRef(sent.Value.Message));
    }

    /// <summary>
    /// Builds the payload of <c>send-message</c>: the member of the content of the kind is filled, the
    /// members of the other kinds stay <c>null</c> (SPEC-042 C35). The log of a refusal names the
    /// channel only — neither the address of a button or of the form nor a caption.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The payload, or a refusal of a message whose content is missing or malformed.</returns>
    private Result<WireSendMessagePayload> SendMessagePayload(ChannelMessage message)
    {
        WirePhoneNumberRequest? phoneNumberRequest = null;
        WireClaimFormLink? claimFormLink = null;
        WireActionButtons? actionButtons = null;

        switch (message.Kind.Value)
        {
            case ChannelMessageKinds.PhoneNumberRequest:
                if (message.PhoneNumberRequest is null)
                {
                    _logger.LogError(
                        "HTTP channel {ChannelType}: a request for the phone number carries no content of the request; nothing is sent",
                        _registration.ChannelType);

                    return NotSent(PhoneNumberRequestWithoutContentMessage);
                }

                phoneNumberRequest = HttpChannelWireMapping.PhoneNumberRequest(message.PhoneNumberRequest);
                break;

            case ChannelMessageKinds.ClaimFormLink:
                if (message.ClaimFormLink is null)
                {
                    _logger.LogError(
                        "HTTP channel {ChannelType}: a link to the claim form carries no content of the link; nothing is sent",
                        _registration.ChannelType);

                    return NotSent(ClaimFormLinkWithoutContentMessage);
                }

                claimFormLink = HttpChannelWireMapping.ClaimFormLink(message.ClaimFormLink);
                break;

            case ChannelMessageKinds.ActionButtons:
                if (message.ActionButtons is not { Buttons.Count: > 0 } buttons)
                {
                    _logger.LogError(
                        "HTTP channel {ChannelType}: an action message carries no buttons; nothing is sent",
                        _registration.ChannelType);

                    return NotSent(ActionButtonsWithoutButtonsMessage);
                }

                // Unlike a built-in channel, which leaves a malformed button out and draws the rest,
                // the wire does not carry a message with a malformed button: one the core got wrong, or a
                // menu item whose configured value is longer than the wire carries.
                actionButtons = HttpChannelWireMapping.ActionButtons(buttons);
                if (actionButtons is null)
                {
                    _logger.LogError(
                        "HTTP channel {ChannelType}: an action message carries a button that is neither a link nor an item of the menu, or an item whose value is empty or over {MaxLength} characters; nothing is sent",
                        _registration.ChannelType,
                        HttpChannelWire.MaxMenuItemValueLength);

                    return NotSent(MalformedActionButtonMessage);
                }

                break;
        }

        return Result<WireSendMessagePayload>.Success(new WireSendMessagePayload
        {
            ChannelUserId = message.ChannelUserId,
            Text = message.Text,
            Kind = message.Kind.Value,
            PhoneNumberRequest = phoneNumberRequest,
            ClaimFormLink = claimFormLink,
            ActionButtons = actionButtons
        });
    }

    /// <summary>
    /// The refusal of a message before any connection (SPEC-042 C35).
    /// </summary>
    /// <param name="message">Message of the refusal; it names no value of the message.</param>
    /// <returns>A failed result.</returns>
    private static Result<WireSendMessagePayload> NotSent(string message) =>
        Result<WireSendMessagePayload>.Failure(ChannelAdapterErrorCodes.MessageSendFailed, message);

    /// <summary>
    /// Asks the external adapter to show the outcome of a transaction — <c>report-outcome</c>
    /// (SPEC-042 C36).
    /// </summary>
    /// <param name="notice">The outcome notice.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the outcome is shown; <c>false</c> — the channel deliberately showed nothing.</returns>
    public async Task<Result<bool>> ReportOutcomeAsync(
        TransactionOutcomeNotice notice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        var reported = await _outbound.CallAsync(
            new HttpChannelCall<WireReportOutcomePayload, WireReportOutcomeResult>(
                HttpChannelWire.Operations.ReportOutcome,
                notice.TransactionId,
                new WireReportOutcomePayload
                {
                    TransactionId = notice.TransactionId.ToString(),
                    Outcome = HttpChannelWireMapping.Outcome(notice.Outcome),
                    ChannelUserId = notice.ChannelUserId,
                    StatusText = notice.StatusText,
                    InboundToken = notice.InboundToken?.Value,
                    PromptMessage = HttpChannelWireMapping.MessageRef(notice.PromptMessage),
                    PhoneRequestMessage = HttpChannelWireMapping.MessageRef(notice.PhoneRequestMessage),
                    DisplayIntent = HttpChannelWireMapping.DisplayIntent(notice.DisplayIntent)
                },
                HttpChannelWireJsonContext.Default.ReportOutcomeCall,
                HttpChannelWireJsonContext.Default.ReportOutcomeResponse),
            cancellationToken);

        return reported.IsFailure
            ? Result<bool>.Failure(reported.Error)
            : Result<bool>.Success(reported.Value.Shown);
    }

    /// <summary>
    /// Asks the external adapter for the deep link of a transaction — <c>deep-link</c> (SPEC-042 C37),
    /// with the purpose of the link and the text to prefill it with, rendered here by the mechanism of
    /// the built-in channels. The link is handed on only when it is admitted (SPEC-042 L45).
    /// </summary>
    /// <param name="request">The transaction the link starts and where the link is going to be used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deep link.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The purpose of <paramref name="request"/> is not a
    /// defined value of <see cref="DeepLinkPurpose"/>.</exception>
    public async Task<Result<string>> GetDeepLinkAsync(
        DeepLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var purpose = HttpChannelWireMapping.LinkPurpose(request.Purpose);
        var rendered = await RenderPrefillAsync(request, cancellationToken);

        var generated = await _outbound.CallAsync(
            new HttpChannelCall<WireDeepLinkPayload, WireDeepLinkResult>(
                HttpChannelWire.Operations.DeepLink,
                request.TransactionId,
                new WireDeepLinkPayload
                {
                    TransactionId = request.TransactionId.ToString(),
                    Purpose = purpose,
                    Rendered = rendered
                },
                HttpChannelWireJsonContext.Default.DeepLinkCall,
                HttpChannelWireJsonContext.Default.DeepLinkResponse),
            cancellationToken);

        if (generated.IsFailure)
        {
            return Result<string>.Failure(generated.Error);
        }

        var url = generated.Value.Url;
        if (!HttpChannelDeepLinkRule.IsAdmitted(url, _registration))
        {
            // The link itself is not logged: it is the adapter's, of any content and any length.
            _logger.LogWarning(
                "HTTP channel {ChannelType}: the deep link of transaction {TransactionId} is not admitted (not an absolute URI of https or of a scheme of the registration, or longer than {MaxLength} characters)",
                _registration.ChannelType,
                request.TransactionId.ToString(),
                HttpChannelConstants.MaxDeepLinkLength);

            return Result<string>.Failure(ChannelAdapterErrorCodes.DeepLinkGenerationFailed, DeepLinkNotAdmittedMessage);
        }

        return Result<string>.Success(url);
    }

    /// <summary>
    /// Renders the prefill text of a deep link the way the built-in channels render it (SPEC-042 C37,
    /// SPEC-003 §10.4): the <c>deeplink-prefill</c> template at the address "in-channel surface + the
    /// channel type of this registration + the purpose of the link + the owners of the transaction", in
    /// the language of the transaction, with the application name and the sign-in code
    /// <c>auth_&lt;transaction&gt;</c> as the values of their slots. Whether the code is in the text is the
    /// template's: the core always fills the <c>{code}</c> slot but does not require the template to state
    /// it. A template the core cannot resolve, or a ladder none of whose steps states the plain-text
    /// edition, leaves the call without a text, and the adapter builds the link from what it has.
    /// </summary>
    /// <param name="request">The request for the link: the transaction and the purpose of the link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendered text; <c>null</c> — no text for this link.</returns>
    private async Task<WireRenderedPrefill?> RenderPrefillAsync(DeepLinkRequest request, CancellationToken cancellationToken)
    {
        var prefillTemplate = await _messageTemplates.ResolveAsync(
            this, MessageKinds.DeeplinkPrefill, request, cancellationToken);
        if (prefillTemplate.IsFailure)
        {
            // The core has already logged and counted the failure; the link is still asked for.
            return null;
        }

        var resolved = prefillTemplate.Value;

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SlotNames.Code] = string.Concat(CallbackDataPrefixes.Auth, request.TransactionId.ToString()),
        };

        // {app} present → the variant with the application name; absent → the variant without it (TPL-031).
        if (!string.IsNullOrWhiteSpace(resolved.ApplicationName))
        {
            values[SlotNames.App] = resolved.ApplicationName;
        }

        var text = MessageTextRenderer.Render(
            resolved.Message,
            values,
            resolved.Locale,
            resolved.TimeZone,
            _promptLocalizer,
            MessageRenderMode.PlainText,
            _logger);

        if (text is null)
        {
            // The wording the deployment wrote reaches nobody, so it is told; the link is still asked for.
            _logger.LogError(
                "HTTP channel {ChannelType}: no step of the {MessageKind} ladder states the plain-text edition; the deep-link call carries no prefill text",
                _registration.ChannelType,
                MessageKinds.DeeplinkPrefill);

            return null;
        }

        return new WireRenderedPrefill
        {
            Text = text,
            Locale = RenderedLanguageOf(resolved.Locale)
        };
    }

    /// <summary>
    /// Probes the external adapter — <c>health</c> (SPEC-042 C38, R17). Any failure of the probe is a
    /// healthy result of the check with <c>IsHealthy = false</c> and the reason in the details; the
    /// response time and the check moment are measured here. The cancellation of the caller propagates.
    /// The wire and adapter versions of the answer have no place in the health status and are not
    /// carried.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The health of the channel.</returns>
    public async Task<Result<ChannelHealthStatus>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = _timeProvider.GetTimestamp();

        Result<WireHealthResult> probed;
        try
        {
            probed = await _outbound.CallAsync(
                new HttpChannelCall<WireHealthPayload, WireHealthResult>(
                    HttpChannelWire.Operations.Health,
                    TransactionId: null,
                    new WireHealthPayload(),
                    HttpChannelWireJsonContext.Default.HealthCall,
                    HttpChannelWireJsonContext.Default.HealthResponse),
                cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // What the client does not turn into a refusal itself — a failure of the egress policy or of
            // the settings resolver — is a failed probe too; only the cancellation of the caller is not.
            _logger.LogWarning(
                exception,
                "HTTP channel {ChannelType}: the health probe of the external adapter failed",
                _registration.ChannelType);

            probed = Result<WireHealthResult>.Failure(
                ChannelAdapterErrorCodes.ChannelNotAvailable,
                HealthProbeFailedMessage + exception.GetType().Name + ": " + exception.Message);
        }

        var responseTime = _timeProvider.GetElapsedTime(startedAt);

        return Result<ChannelHealthStatus>.Success(new ChannelHealthStatus(
            IsHealthy: probed.IsSuccess && probed.Value.Healthy,
            ChannelType: _registration.ChannelType,
            ResponseTime: responseTime,
            Details: probed.IsSuccess ? probed.Value.Details : probed.Error.Message,
            CheckedAt: _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// The language a text for this recipient is actually rendered in: the locale file that serves the
    /// recipient's tag in full, then by its primary subtag (<c>pt-br</c> → <c>pt</c>) — the order the
    /// localizer resolves in — and the base language when none serves it.
    /// </summary>
    /// <param name="recipientLocale">Locale of the recipient; <c>null</c> — unknown.</param>
    /// <returns>Language tag of the rendered text.</returns>
    private string RenderedLanguageOf(string? recipientLocale)
    {
        var normalized = ChannelLocaleTag.Normalize(recipientLocale);
        if (normalized is null)
        {
            return BaseLanguageTag;
        }

        var available = _promptLocalizer.AvailableLocales;
        if (available.Contains(normalized))
        {
            return normalized;
        }

        var separator = normalized.IndexOf(LanguageSubtagSeparator);
        if (separator > 0 && available.Contains(normalized[..separator]))
        {
            return normalized[..separator];
        }

        return BaseLanguageTag;
    }
}
