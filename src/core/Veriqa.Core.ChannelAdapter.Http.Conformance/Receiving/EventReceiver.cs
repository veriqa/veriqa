// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.AspNetCore.Http;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Schema;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Receiving;

/// <summary>
/// The receiver of events: the suite in the role of the core on the webhook route of the channel
/// (SPEC-042 C39). It checks nothing of the wire itself — the signature is checked by
/// <see cref="IChannelAdapter.ValidateWebhookAsync"/> and the event is read by
/// <see cref="IChannelAdapter.ProcessInboundEventAsync"/> of the working composition — and adds the
/// check against the schema, the rule of the declared proof of a phone number (SPEC-042 C39) and the
/// rule of the route of a transaction (SPEC-042 R13). Every request is
/// kept for the check that all events were valid; the event of a transaction a scenario observes is
/// handed to the scenario before the answer goes back to the adapter.
/// </summary>
/// <param name="adapter">The wrapper of the working composition.</param>
/// <param name="schema">Schema of the wire.</param>
/// <param name="eventIds">Source of the <c>webhook-id</c> of the played event.</param>
internal sealed class EventReceiver(IChannelAdapter adapter, WireSchema schema, ConformanceInboundEventIds eventIds)
{
    /// <summary>
    /// Name of the route parameter of the tenant segment — as on the webhook route of the core.
    /// </summary>
    public const string TenantRouteKey = "tenant";

    /// <summary>
    /// Separator joining the values of a multi-valued header into one value — as the core does.
    /// </summary>
    private const string MultiValueSeparator = ", ";

    /// <summary>
    /// Violation of a request whose body is over the limit of the webhook route.
    /// </summary>
    private const string BodyOverLimitViolation = "the body of the request exceeds the limit of the webhook route";

    /// <summary>
    /// Violation of an event that states its phone number proven on a registration that declares none.
    /// </summary>
    private const string UndeclaredPhoneProofViolation =
        "the event states identity.phone_number_verified = true, while the registration does not declare the phone number proven "
        + "(Http:Capabilities:PhoneNumber:Proof); Veriqa takes such a number as not proven";

    /// <summary>
    /// Member of an event envelope carrying the identity of the user.
    /// </summary>
    private const string IdentityMember = "identity";

    /// <summary>
    /// Member of the identity stating whether its phone number is proven.
    /// </summary>
    private const string PhoneNumberVerifiedMember = "phone_number_verified";

    /// <summary>
    /// The requests received, in order.
    /// </summary>
    private readonly ConcurrentQueue<ReceivedEvent> _events = new();

    /// <summary>
    /// The tenant of every transaction a scenario observed — the route its events must come on.
    /// </summary>
    private readonly ConcurrentDictionary<TransactionId, string?> _transactionTenants = new();

    /// <summary>
    /// The observers of the transactions scenarios wait on.
    /// </summary>
    private readonly ConcurrentDictionary<TransactionId, Func<ReceivedEvent, CancellationToken, Task>> _observers = new();

    /// <summary>
    /// The observer of the read events that name no transaction; <c>null</c> — none.
    /// </summary>
    private Func<ReceivedEvent, CancellationToken, Task>? _untiedObserver;

    /// <summary>
    /// The requests received so far, in order.
    /// </summary>
    public IReadOnlyList<ReceivedEvent> Events => [.. _events];

    /// <summary>
    /// The transactions scenarios observed so far — the transactions of the flows.
    /// </summary>
    public IReadOnlyCollection<TransactionId> ObservedTransactions => [.. _transactionTenants.Keys];

    /// <summary>
    /// Hands the events of a transaction to an observer until the observation is disposed. The tenant of
    /// the transaction is kept after that: every later event of the transaction is checked against its
    /// route.
    /// </summary>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="tenant">Tenant of the transaction — the one its <c>deep-link</c> was made for.</param>
    /// <param name="observer">
    /// Called with each read event of the transaction before the answer to the adapter, in the tenant
    /// scope of the route and in the scope of the <c>webhook-id</c> of the event; it must not throw.
    /// </param>
    /// <returns>The observation.</returns>
    public IDisposable Observe(TransactionId transactionId, string? tenant, Func<ReceivedEvent, CancellationToken, Task> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        _transactionTenants[transactionId] = tenant;
        _observers[transactionId] = observer;
        return new Observation(this, transactionId);
    }

    /// <summary>
    /// Hands every read event that names no transaction — <c>auth_start_by_code</c> among them — to an
    /// observer until the observation is disposed. One such observation at a time.
    /// </summary>
    /// <param name="observer">
    /// Called with each such event before the answer to the adapter, in the tenant scope of the route
    /// and in the scope of the <c>webhook-id</c> of the event; it must not throw.
    /// </param>
    /// <returns>The observation.</returns>
    /// <exception cref="InvalidOperationException">Another observation of such events is open.</exception>
    public IDisposable ObserveUntied(Func<ReceivedEvent, CancellationToken, Task> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        if (Interlocked.CompareExchange(ref _untiedObserver, observer, null) is not null)
        {
            throw new InvalidOperationException("The events that name no transaction are already observed.");
        }

        return new UntiedObservation(this, observer);
    }

    /// <summary>
    /// Handles a request on the webhook route.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <returns>A task that completes when the answer is set.</returns>
    public async Task HandleAsync(HttpContext httpContext)
    {
        // The route of the core, played: the tenant from the route; the body read once under the limit;
        // the signature through the wrapper — a refusal is 403 with nothing read; then the event through
        // the wrapper and the schema, and 200 whatever came of it (CA-070). An event of an observed
        // transaction goes to its observer BEFORE the answer: the counter call of an auth_start is made
        // while the POST of the adapter is still open (SPEC-042 E49); an event that names no transaction
        // goes to the observer of such events, if one is open.
        var request = httpContext.Request;
        var cancellationToken = httpContext.RequestAborted;
        // An empty or blank segment is the implicit tenant, as on the route of the core.
        var routeTenant = request.RouteValues.TryGetValue(TenantRouteKey, out var segment) ? segment as string : null;
        if (string.IsNullOrWhiteSpace(routeTenant))
        {
            routeTenant = null;
        }

        var body = await ReadBodyAsync(request.Body, cancellationToken);
        if (body is null)
        {
            _events.Enqueue(new ReceivedEvent(routeTenant, WebhookId: null, Result: null, BodyOverLimitViolation));
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        var inbound = InboundRequestOf(request, body);
        using var tenantScope = ChannelTenantContext.BeginScope(routeTenant);

        if (await RefusalOfAsync(inbound, cancellationToken) is { } refusal)
        {
            _events.Enqueue(new ReceivedEvent(routeTenant, WebhookId: null, Result: null, refusal));
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // The signature verified, so the header is there.
        var webhookId = inbound.Headers[HttpChannelWire.WebhookIdHeader];
        var received = await ReadAsync(inbound, routeTenant, webhookId, cancellationToken);
        _events.Enqueue(received);

        Func<ReceivedEvent, CancellationToken, Task>? observer;
        if (received.TransactionId is { } transactionId)
        {
            _observers.TryGetValue(transactionId, out observer);
        }
        else
        {
            observer = Volatile.Read(ref _untiedObserver);
        }

        if (observer is not null)
        {
            using var eventScope = eventIds.Begin(webhookId);
            await observer(received, cancellationToken);
        }

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
    }

    /// <summary>
    /// Reads the body under the limit of the webhook route and one byte more — enough to know it is over.
    /// </summary>
    /// <param name="body">The body stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The body; <c>null</c> — over the limit.</returns>
    private static async Task<byte[]?> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        var limit = ChannelAdapterLimits.MaxWebhookBodySize;
        var buffer = new byte[limit + 1];
        var read = await body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);

        return read > limit ? null : buffer.AsSpan(0, read).ToArray();
    }

    /// <summary>
    /// The channel-neutral envelope of the request, as the core builds it: the raw body, the headers and
    /// the query, names compared without case.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="body">Its body.</param>
    /// <returns>The envelope.</returns>
    private static ChannelInboundRequest InboundRequestOf(HttpRequest request, byte[] body)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(MultiValueSeparator, header.Value.Where(static value => value is not null));
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in request.Query)
        {
            query[parameter.Key] = string.Join(MultiValueSeparator, parameter.Value.Where(static value => value is not null));
        }

        return new ChannelInboundRequest
        {
            Method = request.Method.ToUpperInvariant(),
            Body = body,
            Headers = headers,
            Query = query
        };
    }

    /// <summary>
    /// Whether the wrapper refuses the request: its signature, or the route of the owner of the
    /// registration. An exception of the check is a refusal, as on the route of the core.
    /// </summary>
    /// <param name="inbound">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>null</c> — accepted; otherwise the refusal.</returns>
    private async Task<string?> RefusalOfAsync(ChannelInboundRequest inbound, CancellationToken cancellationToken)
    {
        try
        {
            var validated = await adapter.ValidateWebhookAsync(inbound, cancellationToken);
            if (validated.IsFailure)
            {
                return "the wrapper refused the request: " + validated.Error.Message;
            }

            return validated.Value ? null : "the wrapper did not accept the request";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "the check of the wrapper ended with " + exception.GetType().Name;
        }
    }

    /// <summary>
    /// Reads a signed event through the wrapper and checks it against the schema, the proof of the phone
    /// number the registration declares and the route of its transaction.
    /// </summary>
    /// <param name="inbound">The request.</param>
    /// <param name="routeTenant">Tenant of the route.</param>
    /// <param name="webhookId">Its <c>webhook-id</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The event.</returns>
    private async Task<ReceivedEvent> ReadAsync(
        ChannelInboundRequest inbound,
        string? routeTenant,
        string webhookId,
        CancellationToken cancellationToken)
    {
        Result<ChannelInboundResult> read;
        try
        {
            read = await adapter.ProcessInboundEventAsync(inbound, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ReceivedEvent(routeTenant, webhookId, Result: null, "the wrapper failed to read the event: " + exception.GetType().Name);
        }

        if (read.IsFailure)
        {
            return new ReceivedEvent(routeTenant, webhookId, Result: null, "the wrapper did not read the event: " + read.Error.Message);
        }

        var received = new ReceivedEvent(routeTenant, webhookId, read.Value, Violation: null);

        if (schema.FindViolation(WireDocument.EventEnvelope, inbound.Body) is { } violation)
        {
            return received with { Violation = "the event does not match the wire schema: " + violation };
        }

        // Checked over the raw body: the wrapper has already lowered the proof in what it read.
        if (adapter.Capabilities.PhoneNumber.Proof is not PhoneNumberProof.Proven && StatesPhoneNumberProven(inbound.Body))
        {
            return received with { Violation = UndeclaredPhoneProofViolation };
        }

        return RouteViolationOf(received) is { } routeViolation
            ? received with { Violation = routeViolation }
            : received;
    }

    /// <summary>
    /// Whether the identity of an event states its phone number proven (SPEC-042 C39). The body has
    /// passed the schema, so a member of another JSON type is simply not that statement.
    /// </summary>
    /// <param name="body">Raw body of the event.</param>
    /// <returns>Whether <c>identity.phone_number_verified</c> is <c>true</c>.</returns>
    private static bool StatesPhoneNumberProven(ReadOnlyMemory<byte> body)
    {
        using var document = JsonDocument.Parse(body);

        return document.RootElement.ValueKind is JsonValueKind.Object
            && document.RootElement.TryGetProperty(IdentityMember, out var identity)
            && identity.ValueKind is JsonValueKind.Object
            && identity.TryGetProperty(PhoneNumberVerifiedMember, out var verified)
            && verified.ValueKind is JsonValueKind.True;
    }

    /// <summary>
    /// The rule of the route (SPEC-042 R13): the event of a transaction comes on the route of the tenant
    /// its <c>deep-link</c> was made for. Checked for the transactions of the suite alone; an event that
    /// names no transaction — <c>auth_start_by_code</c> among them — has no such route to keep.
    /// </summary>
    /// <param name="received">The event.</param>
    /// <returns><c>null</c> — kept; otherwise how it is broken.</returns>
    private string? RouteViolationOf(ReceivedEvent received)
    {
        if (received.TransactionId is not { } transactionId
            || !_transactionTenants.TryGetValue(transactionId, out var tenant)
            || string.Equals(tenant, received.RouteTenant, StringComparison.Ordinal))
        {
            return null;
        }

        return "the event of a transaction of " + TenantNames.Describe(tenant)
            + " arrived on the route of " + TenantNames.Describe(received.RouteTenant);
    }

    /// <summary>
    /// Ends an observation.
    /// </summary>
    /// <param name="owner">The receiver.</param>
    /// <param name="transactionId">The observed transaction.</param>
    private sealed class Observation(EventReceiver owner, TransactionId transactionId) : IDisposable
    {
        /// <summary>
        /// Stops handing the events of the transaction to its observer.
        /// </summary>
        public void Dispose() => owner._observers.TryRemove(transactionId, out _);
    }

    /// <summary>
    /// Ends the observation of the events that name no transaction.
    /// </summary>
    /// <param name="owner">The receiver.</param>
    /// <param name="observer">The observer of the observation.</param>
    private sealed class UntiedObservation(EventReceiver owner, Func<ReceivedEvent, CancellationToken, Task> observer) : IDisposable
    {
        /// <summary>
        /// Stops handing such events to the observer.
        /// </summary>
        public void Dispose() => Interlocked.CompareExchange(ref owner._untiedObserver, null, observer);
    }
}
