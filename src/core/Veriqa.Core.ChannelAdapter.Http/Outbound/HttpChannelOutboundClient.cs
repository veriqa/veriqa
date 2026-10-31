// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Polly;
using Polly.Retry;
using Polly.Timeout;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Http.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Constants;
using Veriqa.Core.ChannelAdapter.Http.Wire;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Outbound;

/// <summary>
/// The ONLY path of an outbound call of an HTTP registration to its external adapter (SPEC-042 C32,
/// L43–L48): every operation of the channel contract that the adapter carries out goes through
/// <see cref="CallAsync{TPayload, TResult}"/>. One instance serves one registration.
/// <para>
/// A call is built once — the envelope with the tenant of the call and the settings string resolved for
/// it, and the idempotency key — and then attempted: before every attempt the egress policy is asked,
/// every attempt is signed anew with the current moment and posts the same body under the same key.
/// The retries and the timeouts are a resilience pipeline of this client rather than a handler of the
/// <c>HttpClient</c>: the egress question and the fresh signature belong inside every attempt, which
/// a handler that re-sends one prepared request cannot give.
/// </para>
/// <para>
/// Nothing of the call reaches the log but its channel, operation and the reason of a failure: not the
/// body, not the settings string, not the transport secret (SPEC-042 N58).
/// </para>
/// </summary>
internal sealed class HttpChannelOutboundClient
{
    /// <summary>
    /// Reason of an attempt the egress policy refused.
    /// </summary>
    private const string EgressRefusedReason = "the egress policy refused the connection";

    /// <summary>
    /// Reason of an answer whose body exceeds the limit.
    /// </summary>
    private const string BodyTooLargeReason = "the body of the answer exceeds the limit of the channel contour";

    /// <summary>
    /// Reason of an answer read in full.
    /// </summary>
    private const string AnsweredReason = "answered";

    /// <summary>
    /// Reason of an answer that is the JSON literal <c>null</c>.
    /// </summary>
    private const string NullEnvelopeReason = "the answer is the JSON literal null, not an envelope";

    /// <summary>
    /// Reason of an envelope with both a result and an error, or with neither.
    /// </summary>
    private const string AmbiguousEnvelopeReason = "the envelope carries both result and error, or neither";

    /// <summary>
    /// Reason of the refusal of a call made for a tenant other than the owner of the registration.
    /// </summary>
    private const string NotOwnerReason = "the tenant of the call does not own the registration";

    /// <summary>
    /// Message of an operation this client does not know.
    /// </summary>
    private const string UnknownOperationMessage = "Not an operation of the wire contract.";

    /// <summary>
    /// Message of a call of an operation keyed by its transaction that states no transaction.
    /// </summary>
    private const string TransactionRequiredMessage = "The operation is keyed by its transaction and requires one.";

    /// <summary>
    /// Property of the resilience context carrying the moment the call started, as a timestamp of the
    /// clock of the host: the retry budget left is measured from it.
    /// </summary>
    private static readonly ResiliencePropertyKey<long> CallStartedAt = new(HttpChannelConstants.CallStartedAtProperty);

    /// <summary>
    /// The registration this client calls the adapter of.
    /// </summary>
    private readonly HttpChannelRegistration _registration;

    /// <summary>
    /// Factory of the named client of the outbound calls — created per attempt, never captured.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Egress policy asked before every connection attempt (SPEC-042 L48).
    /// </summary>
    private readonly IHttpChannelEgressPolicy _egressPolicy;

    /// <summary>
    /// Source of the identifier of the inbound event that triggered the call (SPEC-042 L43).
    /// </summary>
    private readonly IHttpChannelInboundEventIdSource _inboundEventIds;

    /// <summary>
    /// Resolver of the opaque settings string for the tenant of the call (SPEC-042 R12).
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Clock of the signatures, the retry pauses and the budget.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<HttpChannelOutboundClient> _logger;

    /// <summary>
    /// Channel dimension of the settings string of this registration.
    /// </summary>
    private readonly ConfigDimensionValues _channelDimension;

    /// <summary>
    /// Attempts of the operations with a visible effect: retried on a transport failure within the
    /// budget of the registration (SPEC-042 L44).
    /// </summary>
    private readonly ResiliencePipeline<HttpChannelAttempt> _retriedCalls;

    /// <summary>
    /// The single attempt of <c>deep-link</c>, under its own short timeout (SPEC-042 L44).
    /// </summary>
    private readonly ResiliencePipeline<HttpChannelAttempt> _deepLinkCalls;

    /// <summary>
    /// The single attempt of <c>health</c> (SPEC-042 L44).
    /// </summary>
    private readonly ResiliencePipeline<HttpChannelAttempt> _healthCalls;

    /// <summary>
    /// Creates the client of one registration.
    /// </summary>
    /// <param name="registration">The registration.</param>
    /// <param name="httpClientFactory">Factory of the named client of the outbound calls.</param>
    /// <param name="egressPolicy">Egress policy.</param>
    /// <param name="inboundEventIds">Source of the triggering event identifier.</param>
    /// <param name="resolver">Configuration resolver.</param>
    /// <param name="timeProvider">Clock of the host.</param>
    /// <param name="logger">Logger.</param>
    public HttpChannelOutboundClient(
        HttpChannelRegistration registration,
        IHttpClientFactory httpClientFactory,
        IHttpChannelEgressPolicy egressPolicy,
        IHttpChannelInboundEventIdSource inboundEventIds,
        IConfigurationResolver resolver,
        TimeProvider timeProvider,
        ILogger<HttpChannelOutboundClient> logger)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _egressPolicy = egressPolicy ?? throw new ArgumentNullException(nameof(egressPolicy));
        _inboundEventIds = inboundEventIds ?? throw new ArgumentNullException(nameof(inboundEventIds));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _channelDimension = ConfigDimensionValues.Of((ConfigDimensionNames.Channel, registration.ChannelType));
        _retriedCalls = BuildRetriedPipeline();
        _deepLinkCalls = BuildSingleAttemptPipeline(registration.DeepLinkTimeout);
        _healthCalls = BuildSingleAttemptPipeline(registration.OperationTimeout);
    }

    /// <summary>
    /// Makes one outbound call and reads its answer into a result (SPEC-042 L44, L45, R16, R17). A
    /// failure carries the code the channel contract expects for the operation; its reason is logged.
    /// </summary>
    /// <typeparam name="TPayload">Payload of the operation.</typeparam>
    /// <typeparam name="TResult">Result of the operation.</typeparam>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">Cancellation token of the caller.</param>
    /// <returns>The result of the operation, or the refusal of the call.</returns>
    public async Task<Result<TResult>> CallAsync<TPayload, TResult>(
        HttpChannelCall<TPayload, TResult> call,
        CancellationToken cancellationToken)
        where TPayload : class
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(call);

        // The tenant of the envelope is the tenant the settings string is resolved for — one source,
        // so the two never differ (SPEC-042 L47). A health probe belongs to no tenant and carries the
        // string of the core level.
        var isHealth = call.Operation is HttpChannelWire.Operations.Health;
        var tenant = isHealth ? null : ChannelTenantContext.CurrentTenantId;

        // SPEC-042 R60: a registration with an owner makes calls for its owner alone. The call of
        // another tenant is refused here, before the egress policy is asked, so no connection is
        // opened; a health probe is no call of a tenant and is not refused.
        if (!isHealth
            && _registration.Tenant is { } owner
            && !string.Equals(tenant, owner, StringComparison.Ordinal))
        {
            return Refuse<TResult>(call.Operation, ChannelAdapterErrorCodes.ChannelNotAvailable, NotOwnerReason);
        }

        // A call made while no tenant scope is open lands on the default tenant without anybody having
        // said so, as a credential read does: not refused (a self-hosted installation needs no scope),
        // made visible, so the path that forgot to open a scope is named.
        if (!isHealth && !ChannelTenantContext.IsEstablished)
        {
            _logger.LogWarning(
                "HTTP channel {ChannelType}: the call {Operation} resolves its adapter settings outside a tenant scope — the default tenant is assumed",
                _registration.ChannelType,
                call.Operation);
        }

        var settings = await _resolver.ResolveAsync(
            HttpChannelConfigKeys.AdapterSettings,
            ResolutionContext.ForTenant(tenant),
            _channelDimension,
            cancellationToken);

        var envelope = new WireCallEnvelope<TPayload>
        {
            Version = HttpChannelWire.Version,
            Operation = call.Operation,
            ChannelType = _registration.ChannelType,
            Tenant = tenant,
            AdapterSettings = settings.Value,
            Payload = call.Payload
        };

        // The body and the key are fixed for the whole call: a retry posts the same body under the same
        // key, only its signature is new.
        var exchange = new Exchange(
            new HttpChannelEgressRequest(tenant, _registration.ChannelType, call.Operation, _registration.BaseUrl),
            WebhookIdOf(call.Operation, call.TransactionId, tenant),
            JsonSerializer.SerializeToUtf8Bytes(envelope, call.EnvelopeType));

        var attempt = await ExchangeAsync(PipelineOf(call.Operation), exchange, cancellationToken);

        return attempt.Kind is HttpChannelAttemptKind.Answered
            ? ReadAnswer(call, attempt.Body)
            : Refuse<TResult>(call.Operation, ChannelAdapterErrorCodes.ChannelNotAvailable, attempt.Reason);
    }

    /// <summary>
    /// Whether an exception of an attempt is a transport failure (SPEC-042 L44): no connection, a
    /// broken exchange, the attempt or the budget timed out. The cancellation of the caller is not one.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns><c>true</c> — a transport failure.</returns>
    private static bool IsTransportFailure(Exception exception) =>
        exception is HttpRequestException or IOException or TimeoutRejectedException;

    /// <summary>
    /// The code of the refusal of an operation by the adapter (SPEC-042 L45, R16): terminal
    /// <c>channel_cannot_continue</c> only in the answer to <c>send-prompt</c>, the ordinary refusal of
    /// the operation in the answer to any other; a code of the channel contract that applies to the
    /// operation as it is; anything else — the channel is not available.
    /// </summary>
    /// <param name="operation">Operation of the call.</param>
    /// <param name="code">Code of the wire error.</param>
    /// <returns>Code of the refusal.</returns>
    private static string RefusalCodeOf(string operation, string code) => (operation, code) switch
    {
        (HttpChannelWire.Operations.SendPrompt, HttpChannelWire.ErrorCodes.ChannelCannotContinue) =>
            ChannelAdapterErrorCodes.ChannelCannotContinue,
        (HttpChannelWire.Operations.SendMessage or HttpChannelWire.Operations.ReportOutcome,
            HttpChannelWire.ErrorCodes.ChannelCannotContinue) =>
            ChannelAdapterErrorCodes.MessageSendFailed,
        (HttpChannelWire.Operations.DeepLink, HttpChannelWire.ErrorCodes.ChannelCannotContinue) =>
            ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
        (HttpChannelWire.Operations.SendPrompt
            or HttpChannelWire.Operations.SendMessage
            or HttpChannelWire.Operations.ReportOutcome,
            ChannelAdapterErrorCodes.MessageSendFailed) =>
            ChannelAdapterErrorCodes.MessageSendFailed,
        (HttpChannelWire.Operations.DeepLink, ChannelAdapterErrorCodes.DeepLinkGenerationFailed) =>
            ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
        _ => ChannelAdapterErrorCodes.ChannelNotAvailable
    };

    /// <summary>
    /// Runs the attempts of one call through its pipeline. A transport failure that ends the call —
    /// the last attempt failed, the budget ran out — is an attempt of its own kind; the cancellation of
    /// the caller propagates.
    /// </summary>
    /// <param name="pipeline">Pipeline of the operation.</param>
    /// <param name="exchange">The prepared call.</param>
    /// <param name="cancellationToken">Cancellation token of the caller.</param>
    /// <returns>How the last attempt ended.</returns>
    private async Task<HttpChannelAttempt> ExchangeAsync(
        ResiliencePipeline<HttpChannelAttempt> pipeline,
        Exchange exchange,
        CancellationToken cancellationToken)
    {
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(CallStartedAt, _timeProvider.GetTimestamp());

        try
        {
            return await pipeline.ExecuteAsync(
                static (attemptContext, state) => state.Client.AttemptAsync(state.Exchange, attemptContext.CancellationToken),
                context,
                (Client: this, Exchange: exchange));
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            return new HttpChannelAttempt(
                HttpChannelAttemptKind.TransportFailure,
                exception.GetType().Name + ": " + exception.Message);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>
    /// One attempt of a call: the egress question, then a freshly signed <c>POST</c> of the prepared
    /// body, then the status and — for a 200 — the body of the answer, read up to the limit and not a
    /// byte further (SPEC-042 L48, C31, L44, E54).
    /// </summary>
    /// <param name="exchange">The prepared call.</param>
    /// <param name="cancellationToken">Cancellation token of the attempt.</param>
    /// <returns>How the attempt ended.</returns>
    private async ValueTask<HttpChannelAttempt> AttemptAsync(Exchange exchange, CancellationToken cancellationToken)
    {
        if (!await _egressPolicy.IsAllowedAsync(exchange.Egress, cancellationToken))
        {
            return new HttpChannelAttempt(HttpChannelAttemptKind.EgressRefused, EgressRefusedReason);
        }

        using var request = CreateSignedRequest(exchange);
        var client = _httpClientFactory.CreateClient(HttpChannelConstants.HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var status = string.Create(CultureInfo.InvariantCulture, $"the adapter answered with status {(int)response.StatusCode}");

        // A 5xx and a 429 are transport failures; the body of such an answer is not read.
        if (response.StatusCode >= HttpStatusCode.InternalServerError)
        {
            return new HttpChannelAttempt(HttpChannelAttemptKind.TransportFailure, status);
        }

        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            return new HttpChannelAttempt(HttpChannelAttemptKind.TransportFailure, status)
            {
                RetryAfter = RetryAfterOf(response.Headers.RetryAfter)
            };
        }

        // A redirect is not followed, and neither it nor any other status but 200 is an answer of the
        // contract.
        if (response.StatusCode is not HttpStatusCode.OK)
        {
            return new HttpChannelAttempt(HttpChannelAttemptKind.UnexpectedStatus, status);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();

        try
        {
            await BoundedBodyReader.CopyAsync(
                stream,
                body,
                ChannelAdapterLimits.MaxWebhookBodySize,
                static () => new InvalidDataException(BodyTooLargeReason),
                cancellationToken);
        }
        catch (InvalidDataException)
        {
            return new HttpChannelAttempt(HttpChannelAttemptKind.BodyTooLarge, BodyTooLargeReason);
        }

        return new HttpChannelAttempt(HttpChannelAttemptKind.Answered, AnsweredReason) { Body = body.ToArray() };
    }

    /// <summary>
    /// Builds the request of one attempt, signed with the current moment and every transport secret of
    /// the registration (SPEC-042 C31).
    /// </summary>
    /// <param name="exchange">The prepared call.</param>
    /// <returns>The request; the caller owns it.</returns>
    private HttpRequestMessage CreateSignedRequest(Exchange exchange)
    {
        var content = new ByteArrayContent(exchange.Body);
        content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json, Encoding.UTF8.WebName);

        var request = new HttpRequestMessage(HttpMethod.Post, _registration.BaseUrl)
        {
            Content = content
        };

        try
        {
            var signedAt = _timeProvider.GetUtcNow();
            request.Headers.Add(HttpChannelWire.WebhookIdHeader, exchange.WebhookId);
            request.Headers.Add(
                HttpChannelWire.WebhookTimestampHeader,
                signedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            request.Headers.Add(
                HttpChannelWire.WebhookSignatureHeader,
                HttpChannelSignature.Sign(exchange.WebhookId, signedAt, exchange.Body, _registration.TransportSecrets));

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The pause a <c>Retry-After</c> header asks for: the seconds it states, or the time left until the
    /// moment it names; <c>null</c> — no header.
    /// </summary>
    /// <param name="retryAfter">The header.</param>
    /// <returns>The pause.</returns>
    private TimeSpan? RetryAfterOf(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var pause = date - _timeProvider.GetUtcNow();
            return pause > TimeSpan.Zero ? pause : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// The retry rule of the operations with a visible effect (SPEC-042 L44): only a transport failure
    /// is retried, and not when the pause the adapter asked for does not fit into what is left of the
    /// budget — such a pause is not waited out.
    /// </summary>
    /// <param name="arguments">The end of the attempt.</param>
    /// <returns><c>true</c> — retry.</returns>
    private ValueTask<bool> ShouldRetryAsync(RetryPredicateArguments<HttpChannelAttempt> arguments)
    {
        if (arguments.Outcome.Exception is { } exception)
        {
            return ValueTask.FromResult(IsTransportFailure(exception));
        }

        if (arguments.Outcome.Result is not { Kind: HttpChannelAttemptKind.TransportFailure } attempt)
        {
            return ValueTask.FromResult(false);
        }

        if (attempt.RetryAfter is { } pause
            && arguments.Context.Properties.TryGetValue(CallStartedAt, out var startedAt)
            && pause >= _registration.RetryBudget - _timeProvider.GetElapsedTime(startedAt))
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(true);
    }

    /// <summary>
    /// Builds the pipeline of the retried operations: all attempts with their pauses within the budget,
    /// up to three retries after 1 s, 2 s and 4 s or after the pause of <c>Retry-After</c>, each attempt
    /// within the operation timeout (SPEC-042 L44, SPEC-003 §12.2).
    /// </summary>
    /// <returns>The pipeline.</returns>
    private ResiliencePipeline<HttpChannelAttempt> BuildRetriedPipeline() =>
        new ResiliencePipelineBuilder<HttpChannelAttempt> { TimeProvider = _timeProvider }
            .AddTimeout(_registration.RetryBudget)
            .AddRetry(new RetryStrategyOptions<HttpChannelAttempt>
            {
                MaxRetryAttempts = HttpChannelConstants.MaxRetryAttempts,
                Delay = TimeSpan.FromSeconds(HttpChannelConstants.FirstRetryDelaySeconds),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = false,
                ShouldHandle = ShouldRetryAsync,
                DelayGenerator = static arguments => ValueTask.FromResult(arguments.Outcome.Result?.RetryAfter)
            })
            .AddTimeout(_registration.OperationTimeout)
            .Build();

    /// <summary>
    /// Builds the pipeline of an operation that is never retried: one attempt within its timeout.
    /// </summary>
    /// <param name="timeout">Timeout of the attempt.</param>
    /// <returns>The pipeline.</returns>
    private ResiliencePipeline<HttpChannelAttempt> BuildSingleAttemptPipeline(TimeSpan timeout) =>
        new ResiliencePipelineBuilder<HttpChannelAttempt> { TimeProvider = _timeProvider }
            .AddTimeout(timeout)
            .Build();

    /// <summary>
    /// The pipeline of an operation: <c>send-prompt</c>, <c>send-message</c> and <c>report-outcome</c>
    /// are retried; <c>deep-link</c> and <c>health</c> are not (SPEC-042 L44).
    /// </summary>
    /// <param name="operation">Operation of the call.</param>
    /// <returns>The pipeline.</returns>
    private ResiliencePipeline<HttpChannelAttempt> PipelineOf(string operation) => operation switch
    {
        HttpChannelWire.Operations.SendPrompt
            or HttpChannelWire.Operations.SendMessage
            or HttpChannelWire.Operations.ReportOutcome => _retriedCalls,
        HttpChannelWire.Operations.DeepLink => _deepLinkCalls,
        HttpChannelWire.Operations.Health => _healthCalls,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, UnknownOperationMessage)
    };

    /// <summary>
    /// The idempotency key of a call — its <c>webhook-id</c> (SPEC-042 L43). <c>send-prompt</c> and
    /// <c>report-outcome</c> caused by an inbound event are keyed by the channel, the tenant, the
    /// transaction, the event and the operation, so a redelivered event gives no second message; outside
    /// an inbound event they are keyed by the transaction and the operation. Every other call gets a
    /// random key of its own.
    /// </summary>
    /// <param name="operation">Operation of the call.</param>
    /// <param name="transactionId">Transaction of the call.</param>
    /// <param name="tenant">Tenant of the call.</param>
    /// <returns>The key.</returns>
    private string WebhookIdOf(string operation, TransactionId? transactionId, string? tenant)
    {
        if (operation is not (HttpChannelWire.Operations.SendPrompt or HttpChannelWire.Operations.ReportOutcome))
        {
            return HttpChannelIdempotencyKey.NewRandom();
        }

        var transaction = transactionId?.ToString()
            ?? throw new ArgumentException(TransactionRequiredMessage, nameof(transactionId));
        var eventId = _inboundEventIds.GetCurrentEventId(_registration.ChannelType);

        return eventId is null
            ? HttpChannelIdempotencyKey.Derive(transaction, operation)
            : HttpChannelIdempotencyKey.Derive(_registration.ChannelType, tenant, transaction, eventId, operation);
    }

    /// <summary>
    /// Reads the body of a 200 as the envelope of the answer (SPEC-042 C32, L44, L45): a body that is
    /// not an envelope, an envelope of another major version and an envelope with both or neither of
    /// result and error are answers outside the contract; an error is the refusal of the operation.
    /// </summary>
    /// <typeparam name="TPayload">Payload of the operation.</typeparam>
    /// <typeparam name="TResult">Result of the operation.</typeparam>
    /// <param name="call">The call.</param>
    /// <param name="body">Body of the answer.</param>
    /// <returns>The result of the operation, or the refusal of the call.</returns>
    private Result<TResult> ReadAnswer<TPayload, TResult>(HttpChannelCall<TPayload, TResult> call, ReadOnlyMemory<byte> body)
        where TPayload : class
        where TResult : class
    {
        WireResponseEnvelope<TResult>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(body.Span, call.ResponseType);
        }
        catch (JsonException exception)
        {
            return Refuse<TResult>(
                call.Operation,
                ChannelAdapterErrorCodes.ChannelNotAvailable,
                "the answer is not a wire envelope: " + WireParseFailure.ReasonOf(exception));
        }

        if (envelope is null)
        {
            return Refuse<TResult>(call.Operation, ChannelAdapterErrorCodes.ChannelNotAvailable, NullEnvelopeReason);
        }

        if (!WireVersion.TryGetMajor(envelope.Version, out var major) || major != HttpChannelWire.MajorVersion)
        {
            return Refuse<TResult>(
                call.Operation,
                ChannelAdapterErrorCodes.ChannelNotAvailable,
                "the answer is of the wire version " + envelope.Version);
        }

        if ((envelope.Result is null) == (envelope.Error is null))
        {
            return Refuse<TResult>(call.Operation, ChannelAdapterErrorCodes.ChannelNotAvailable, AmbiguousEnvelopeReason);
        }

        if (envelope.Error is { } error)
        {
            return Refuse<TResult>(
                call.Operation,
                RefusalCodeOf(call.Operation, error.Code),
                "the adapter refused with " + error.Code + ": " + error.Message);
        }

        return Result<TResult>.Success(envelope.Result!);
    }

    /// <summary>
    /// The refusal of a call, its reason written to the log (SPEC-042 L45).
    /// </summary>
    /// <typeparam name="TResult">Result of the operation.</typeparam>
    /// <param name="operation">Operation of the call.</param>
    /// <param name="code">Code of the refusal.</param>
    /// <param name="reason">Reason of the refusal.</param>
    /// <returns>The failed result.</returns>
    private Result<TResult> Refuse<TResult>(string operation, string code, string reason)
    {
        _logger.LogWarning(
            "HTTP channel {ChannelType}: the call {Operation} to the external adapter failed with {ErrorCode}. Reason: {Reason}",
            _registration.ChannelType,
            operation,
            code,
            reason);

        return Result<TResult>.Failure(code, "The call " + operation + " to the external adapter failed: " + reason + ".");
    }

    /// <summary>
    /// A call prepared for its attempts: what the egress policy is asked, the key and the body.
    /// </summary>
    /// <param name="Egress">The question to the egress policy.</param>
    /// <param name="WebhookId">Idempotency key of the call.</param>
    /// <param name="Body">Body of the call — the serialized envelope.</param>
    private sealed record Exchange(HttpChannelEgressRequest Egress, string WebhookId, byte[] Body);
}
