// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Sample.PseudoChannels.Adapter;

/// <summary>
/// Adapter of a pseudo-channel: a sign-in at an identity provider wrapped into the shape of a
/// channel. There is no delivery channel behind it — the "platform" is the sample host, which
/// sends the phone to the provider, and then reports what happened through the signed relay
/// webhook. One class serves every provider; an instance is told apart by its
/// <see cref="PseudoChannelOptions"/>, so each channel is registered with a factory of its own.
/// </summary>
public sealed class PseudoChannelAdapter : IChannelAdapter, IChannelDisplayMetadata
{
    /// <summary>
    /// Settings of the channel this instance serves.
    /// </summary>
    private readonly PseudoChannelOptions _options;

    /// <summary>
    /// Where the question context and the outcome are left for the host.
    /// </summary>
    private readonly PseudoChannelExchange _exchange;

    /// <summary>
    /// Clock.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<PseudoChannelAdapter> _logger;

    /// <summary>
    /// Channel link without the transaction value.
    /// </summary>
    private readonly string _startLinkPrefix;

    /// <summary>
    /// Creates the adapter of one pseudo-channel.
    /// </summary>
    /// <param name="options">Settings of the channel.</param>
    /// <param name="exchange">Process-wide exchange shared with the host.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentException">The public base address is not an absolute URL, or the
    /// state lifetime is not positive.</exception>
    public PseudoChannelAdapter(
        PseudoChannelOptions options,
        PseudoChannelExchange exchange,
        TimeProvider timeProvider,
        ILogger<PseudoChannelAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException(
                $"The public base URL of the '{options.ChannelType}' pseudo-channel is not an absolute URL.",
                nameof(options));
        }

        if (options.StateLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"The state lifetime of the '{options.ChannelType}' pseudo-channel must be positive.",
                nameof(options));
        }

        _options = options;
        _exchange = exchange;
        _timeProvider = timeProvider;
        _logger = logger;

        _startLinkPrefix =
            $"{options.PublicBaseUrl.TrimEnd('/')}/{PseudoChannelConstants.PathPrefix}/{options.ChannelType}/" +
            $"{PseudoChannelConstants.StartSegment}?{PseudoChannelConstants.TransactionQueryParameter}=";

        // What the core is told decides whether it calls the adapter at all. The question is asked
        // on the phone page, so the channel confirms inside itself. Plain text is declared because
        // the core hands a confirmation prompt only to a channel that accepts it — it is not a
        // promise to deliver messages, and SendMessageAsync refuses. The outcome is declared for
        // the same reason: without it the core never reports one, and the phone page shows it.
        Capabilities = new ChannelCapabilities
        {
            SupportsInChannelConfirmation = true,
            SupportedMessageKinds = new[] { ChannelMessageKind.PlainText }.ToFrozenSet(),
            SupportsMessageUpdate = false,
            DeliversOutcomeNotice = true,
            ProvidesRecipientLocale = options.ProvidesLocale
        };
    }

    /// <inheritdoc />
    public string ChannelType => _options.ChannelType;

    /// <inheritdoc />
    public string DisplayName => _options.DisplayName;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities { get; }

    /// <summary>
    /// Validates the relay webhook by the HMAC-SHA256 of its raw body. The default answer is
    /// "reject": an unconfigured secret never lets a request through.
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the request is valid.</returns>
    public Task<Result<bool>> ValidateWebhookAsync(ChannelInboundRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.RelaySecret))
        {
            _logger.LogWarning(
                "The relay secret of the {ChannelType} pseudo-channel is not configured — the webhook is rejected",
                _options.ChannelType);

            return Task.FromResult(Result<bool>.Success(false));
        }

        request.Headers.TryGetValue(PseudoChannelConstants.SignatureHeader, out var signature);
        var isValid = PseudoChannelSignature.IsValid(_options.RelaySecret, request.Body.Span, signature);

        return Task.FromResult(Result<bool>.Success(isValid));
    }

    /// <summary>
    /// Turns the relay webhook body into an inbound result: the sign-in at the provider starts the
    /// authentication, the answer on the phone page confirms or declines it.
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of processing the incoming event.</returns>
    public Task<Result<ChannelInboundResult>> ProcessInboundEventAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        PseudoChannelWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PseudoChannelWebhookPayload>(request.Body.Span);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize the {ChannelType} relay webhook body", _options.ChannelType);

            return Task.FromResult(Result<ChannelInboundResult>.Failure(
                VeriqaErrorCodes.UnsupportedEventType,
                "The relay webhook body is not valid JSON."));
        }

        if (payload is null || payload.Intent is not (
            PseudoChannelConstants.Intents.Start
            or PseudoChannelConstants.Intents.Confirm
            or PseudoChannelConstants.Intents.Decline))
        {
            return Task.FromResult(Result<ChannelInboundResult>.Failure(
                VeriqaErrorCodes.UnsupportedEventType,
                "The relay webhook body names no supported intent."));
        }

        if (string.IsNullOrEmpty(payload.TransactionId) || string.IsNullOrEmpty(payload.Subject))
        {
            return Task.FromResult(Result<ChannelInboundResult>.Failure(
                VeriqaErrorCodes.IdentityExtractionFailed,
                "The relay webhook body has no transaction identifier or no subject."));
        }

        // A value that does not parse means the event is not ours: the core is handed an
        // irrelevant-event result rather than a broken identifier.
        if (!TransactionId.TryParse(payload.TransactionId, out var transactionId))
        {
            _logger.LogWarning(
                "Unparseable transaction identifier in a {ChannelType} relay webhook; the event is treated as irrelevant",
                _options.ChannelType);

            return Task.FromResult(Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult()));
        }

        var identity = CreateIdentity(payload, payload.Subject);
        ChannelInboundResult result = payload.Intent switch
        {
            PseudoChannelConstants.Intents.Confirm =>
                new ChannelAuthConfirmResult { TransactionId = transactionId, Identity = identity },
            PseudoChannelConstants.Intents.Decline =>
                new ChannelAuthDeclineResult { TransactionId = transactionId, Identity = identity },
            _ => new ChannelAuthStartResult { TransactionId = transactionId, Identity = identity }
        };

        return Task.FromResult(Result<ChannelInboundResult>.Success(result));
    }

    /// <summary>
    /// Builds the channel link: the address of the host that sends the phone to the provider. The
    /// same request always yields the same link, and nothing is created by asking for it. The link does
    /// not depend on the purpose of the request.
    /// </summary>
    /// <param name="request">The transaction and the purpose of the link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Channel link.</returns>
    public Task<Result<string>> GetDeepLinkAsync(DeepLinkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.FromResult(Result<string>.Success(
            _startLinkPrefix + Uri.EscapeDataString(request.TransactionId.ToString())));
    }

    /// <summary>
    /// Receives the question from the core. Nothing can be sent to the user — the phone page of the
    /// host asks instead — so the context is left in the exchange for that page to read, addressed
    /// to the user the core named: the page shows it to that account only.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="channelUserId">User identifier in the channel.</param>
    /// <param name="promptContext">Confirmation context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success without a reference: there is no message to address later.</returns>
    public Task<Result<ChannelMessageRef?>> SendConfirmationPromptAsync(
        TransactionId transactionId,
        string channelUserId,
        ConfirmationPromptContext promptContext,
        CancellationToken cancellationToken = default)
    {
        _exchange.StorePrompt(transactionId, channelUserId, promptContext, _options.StateLifetime);

        // The core does not log a prompt that was accepted, so this record is the only trace that
        // the question reached the channel.
        _logger.LogInformation(
            "Confirmation prompt received. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
            _options.ChannelType,
            transactionId.ToString());

        return Task.FromResult(Result<ChannelMessageRef?>.Success(null));
    }

    /// <summary>
    /// Refuses: a pseudo-channel has nowhere to deliver a message to, and saying "delivered" would
    /// be a lie the core acts upon.
    /// </summary>
    /// <param name="message">Message to deliver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A failure — the message was not delivered.</returns>
    public Task<Result<ChannelMessageRef?>> SendMessageAsync(
        ChannelMessage message,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Result<ChannelMessageRef?>.Failure(
            VeriqaErrorCodes.MessageSendFailed,
            "A pseudo-channel has no delivery channel: the message cannot be sent."));

    /// <inheritdoc />
    public Task<Result<ChannelHealthStatus>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var status = new ChannelHealthStatus(
            true,
            _options.ChannelType,
            PseudoChannelConstants.HealthResponseTime,
            null,
            _timeProvider.GetUtcNow());

        return Task.FromResult(Result<ChannelHealthStatus>.Success(status));
    }

    /// <summary>
    /// Receives the outcome from the core and leaves it in the exchange: the phone page of the host
    /// is the surface that shows it.
    /// </summary>
    /// <param name="notice">What to show.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success: the outcome is available to the page.</returns>
    public Task<Result<bool>> ReportOutcomeAsync(
        TransactionOutcomeNotice notice,
        CancellationToken cancellationToken = default)
    {
        _exchange.StoreOutcome(notice, _options.StateLifetime);

        return Task.FromResult(Result<bool>.Success(true));
    }

    /// <summary>
    /// Builds the identity snapshot from the claims the host relayed. The display name falls back
    /// from the full name to the email and then to the subject — a snapshot is never nameless.
    /// </summary>
    /// <param name="payload">Relay webhook body.</param>
    /// <param name="subject">Subject identifier, already checked to be present.</param>
    /// <returns>Identity snapshot.</returns>
    private ChannelIdentitySnapshot CreateIdentity(PseudoChannelWebhookPayload payload, string subject)
        => new()
        {
            ChannelType = _options.ChannelType,
            IsBot = false,
            ChannelUserId = subject,
            DisplayName = FirstNonBlank(payload.Name, payload.Email) ?? subject,
            FirstName = payload.GivenName,
            LastName = payload.FamilyName,
            Email = payload.Email,
            EmailVerified = payload.EmailVerified,
            AvatarUrl = payload.Picture,
            Locale = payload.Locale,
            CapturedAt = _timeProvider.GetUtcNow(),
            AdapterVersion = PseudoChannelConstants.AdapterVersion
        };

    /// <summary>
    /// Returns the first value that is neither null nor whitespace.
    /// </summary>
    /// <param name="first">First candidate.</param>
    /// <param name="second">Second candidate.</param>
    /// <returns>The first non-blank value, or null.</returns>
    private static string? FirstNonBlank(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first
            : !string.IsNullOrWhiteSpace(second) ? second
            : null;
}
