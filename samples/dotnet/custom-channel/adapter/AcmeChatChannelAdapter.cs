// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Sample.CustomChannel.Adapter;

/// <summary>
/// Sample third-party channel adapter built against the MIT contracts package only.
/// It shows the three things the SPI requires from an adapter: rejecting a forged webhook
/// (<see cref="ValidateWebhookAsync"/>), turning the raw request body into a
/// <see cref="ChannelInboundResult"/> (<see cref="ProcessInboundEventAsync"/>), and describing
/// itself for the sign-in window (<see cref="IChannelDisplayMetadata"/>).
/// Outbound messaging is stubbed out with logging — the sample has no real platform behind it.
/// </summary>
public sealed class AcmeChatChannelAdapter : IChannelAdapter, IChannelDisplayMetadata
{
    /// <summary>
    /// The facts the sample channel declares about itself: it confirms right on the deep link and has
    /// no prompt of its own, so it cannot confirm inside the channel — the fact stays at its safe
    /// default and the core asks the question on its own surface instead. It also cannot update what
    /// it has sent, shows no terminal status of its own, and does not know the recipient's locale.
    /// Declaring "cannot" is the whole point — the core then never calls what this channel is unable
    /// to do, and never silently skips a confirmation the operator configured.
    /// </summary>
    private static readonly ChannelCapabilities BaseCapabilities = new()
    {
        SupportedMessageKinds = new[] { ChannelMessageKind.PlainText }.ToFrozenSet(),
        SupportsMessageUpdate = false,
        DeliversOutcomeNotice = false,
        ProvidesRecipientLocale = false
    };

    /// <summary>
    /// JSON options of the adapter: the body format is the adapter's own business.
    /// </summary>
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Channel settings owned by the adapter's author.
    /// </summary>
    private readonly AcmeChatOptions _options;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<AcmeChatChannelAdapter> _logger;

    /// <summary>
    /// The facts this instance declares: <see cref="BaseCapabilities"/>, plus the start by code when
    /// the settings turn it on.
    /// </summary>
    private readonly ChannelCapabilities _capabilities;

    /// <summary>
    /// Creates the sample adapter.
    /// </summary>
    /// <param name="options">Channel settings.</param>
    /// <param name="logger">Logger.</param>
    public AcmeChatChannelAdapter(AcmeChatOptions options, ILogger<AcmeChatChannelAdapter> logger)
    {
        _options = options;
        _logger = logger;
        _capabilities = BaseCapabilities with { CodeStart = DeclareCodeStart(options) };
    }

    /// <inheritdoc />
    public string ChannelType => AcmeChatConstants.ChannelType;

    /// <inheritdoc />
    public string DisplayName => AcmeChatConstants.DisplayName;

    /// <inheritdoc />
    public string? IconSvgPath => AcmeChatConstants.IconSvgPath;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities => _capabilities;

    /// <summary>
    /// Validates the webhook by the shared secret in the signature header. A real adapter would
    /// verify an HMAC of the raw body bytes the envelope carries; either way the comparison is
    /// fixed-time and the default answer is "reject" — an unconfigured secret never lets a
    /// request through.
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the request is valid.</returns>
    public Task<Result<bool>> ValidateWebhookAsync(ChannelInboundRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.WebhookSecret))
        {
            _logger.LogWarning("Acme Chat webhook secret is not configured — the webhook is rejected");
            return Task.FromResult(Result<bool>.Success(false));
        }

        request.Headers.TryGetValue(AcmeChatConstants.SignatureHeader, out var signature);
        signature ??= string.Empty;
        var isValid = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature),
            Encoding.UTF8.GetBytes(_options.WebhookSecret));

        return Task.FromResult(Result<bool>.Success(isValid));
    }

    /// <summary>
    /// Turns the raw webhook body carried by the envelope into an inbound result. The user opened
    /// the deep link and reached the platform, which is the sign-in intent. When the channel declares
    /// a start by code, an event carrying a code is the same intent addressed by that code instead.
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of processing the incoming event.</returns>
    public Task<Result<ChannelInboundResult>> ProcessInboundEventAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // The body format is the adapter's own business — the core hands over the bytes as they
        // arrived and imposes nothing.
        AcmeChatWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AcmeChatWebhookPayload>(request.Body.Span, PayloadJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize the Acme Chat webhook body");
            // The code comes from the declared SPI registry, not from a string of the adapter's
            // own invention: only a declared code reaches the core's branches and telemetry tags.
            return Task.FromResult(Result<ChannelInboundResult>.Failure(
                VeriqaErrorCodes.UnsupportedEventType,
                "The Acme Chat webhook body is not valid JSON."));
        }

        var startsByCode = _capabilities.CodeStart is not null && !string.IsNullOrWhiteSpace(payload?.Code);

        if (payload?.UserId is null || (payload.TransactionId is null && !startsByCode))
        {
            return Task.FromResult(Result<ChannelInboundResult>.Failure(
                VeriqaErrorCodes.IdentityExtractionFailed,
                "The Acme Chat webhook body has no transaction_id/user_id."));
        }

        var snapshot = new ChannelIdentitySnapshot
        {
            ChannelType = AcmeChatConstants.ChannelType,
            IsBot = false,
            ChannelUserId = payload.UserId,
            DisplayName = payload.DisplayName ?? payload.UserId,
            CapturedAt = DateTimeOffset.UtcNow,
            AdapterVersion = AcmeChatConstants.AdapterVersion
        };

        // A code is handed over exactly as the user sent it: the adapter neither normalizes it nor
        // checks its length — both belong to the core, which finds the transaction by the code and
        // answers a code that names none.
        if (startsByCode)
        {
            return Task.FromResult(Result<ChannelInboundResult>.Success(
                new ChannelAuthStartByCodeResult
                {
                    Code = payload.Code!,
                    Identity = snapshot
                }));
        }

        // The identifier arrives as a string in the channel event, so the adapter is where it is
        // parsed. A value that does not parse means the event is not ours: the core is handed an
        // irrelevant-event result rather than a broken identifier.
        if (!TransactionId.TryParse(payload.TransactionId, out var transactionId))
        {
            _logger.LogWarning(
                "Unparseable transaction identifier in an Acme Chat webhook; the event is treated as irrelevant.");

            return Task.FromResult(Result<ChannelInboundResult>.Success(
                new ChannelUnrelatedResult()));
        }

        var result = new ChannelAuthStartResult
        {
            TransactionId = transactionId,
            Identity = snapshot
        };

        return Task.FromResult(Result<ChannelInboundResult>.Success(result));
    }

    /// <summary>
    /// Builds the deep link the sign-in window turns into a button and a QR code. A channel started
    /// by code alone returns a link that only opens the chat: it has no way to carry the transaction,
    /// which is what <see cref="ChannelCodeStart.LinkCarriesTransaction"/> <c>false</c> declares. The
    /// link does not depend on the purpose of the request: the button, the QR code and the hop target
    /// all get the same one.
    /// </summary>
    /// <param name="request">The transaction and the purpose of the link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deep link URL.</returns>
    public Task<Result<string>> GetDeepLinkAsync(DeepLinkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_capabilities.CodeStart is { LinkCarriesTransaction: false })
        {
            return Task.FromResult(Result<string>.Success(_options.DeepLinkBaseUrl));
        }

        var deepLink =
            $"{_options.DeepLinkBaseUrl}?{AcmeChatConstants.DeepLinkTransactionParameter}=" +
            Uri.EscapeDataString(request.TransactionId.ToString());

        return Task.FromResult(Result<string>.Success(deepLink));
    }

    /// <summary>
    /// What the channel declares about a start by code, from its settings: none by default; the code
    /// as the only start when the link carries no transaction; the code as a fallback beside a link
    /// that does. A fallback code is sent as it is — the user types the digits off another screen, and
    /// a command around them would only get in the way.
    /// </summary>
    /// <param name="options">Channel settings.</param>
    /// <returns>The declaration, or null when the sign-in is started by the link alone.</returns>
    private static ChannelCodeStart? DeclareCodeStart(AcmeChatOptions options)
    {
        if (options.StartByCode)
        {
            return new ChannelCodeStart
            {
                MessageTemplate = AcmeChatConstants.StartMessageTemplate,
                LinkCarriesTransaction = false
            };
        }

        return options.CodeFallback
            ? new ChannelCodeStart { LinkCarriesTransaction = true }
            : null;
    }

    /// <inheritdoc />
    public Task<Result<ChannelMessageRef?>> SendConfirmationPromptAsync(
        TransactionId transactionId,
        string channelUserId,
        ConfirmationPromptContext promptContext,
        CancellationToken cancellationToken = default)
    {
        // The sample channel confirms on the deep link, so no in-channel prompt is sent.
        _logger.LogInformation(
            "Acme Chat confirmation prompt suppressed for user {ChannelUserId}: the channel confirms on the deep link",
            channelUserId);

        // Nothing was sent, so there is nothing to address later.
        return Task.FromResult(Result<ChannelMessageRef?>.Success(null));
    }

    /// <summary>
    /// Delivers a message to the user. A real adapter calls its platform API here and reports back
    /// the coordinates of what it sent; the sample logs the text so the run is observable.
    /// </summary>
    /// <param name="message">Message to deliver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success without a reference — the sample platform has no addressable messages.</returns>
    public Task<Result<ChannelMessageRef?>> SendMessageAsync(
        ChannelMessage message,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Acme Chat message to {ChannelUserId}: {MessageText}",
            message.ChannelUserId,
            message.Text);

        return Task.FromResult(Result<ChannelMessageRef?>.Success(null));
    }

    /// <inheritdoc />
    public Task<Result<ChannelHealthStatus>> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var status = new ChannelHealthStatus(
            true,
            AcmeChatConstants.ChannelType,
            AcmeChatConstants.HealthResponseTime,
            null,
            DateTimeOffset.UtcNow);

        return Task.FromResult(Result<ChannelHealthStatus>.Success(status));
    }

    /// <inheritdoc />
    public Task<Result<bool>> ReportOutcomeAsync(
        TransactionOutcomeNotice notice,
        CancellationToken cancellationToken = default)
    {
        // The channel declares DeliversOutcomeNotice = false, so the core never calls this. A direct
        // call answers "deliberately showed nothing" — that is what the declaration already said,
        // and it is no longer a third different way of saying "cannot".
        return Task.FromResult(Result<bool>.Success(false));
    }
}
