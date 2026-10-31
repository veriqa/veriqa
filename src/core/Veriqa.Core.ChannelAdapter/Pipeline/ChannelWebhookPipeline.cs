// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.ChannelAdapter.Diagnostics;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Constants;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Processing pipeline for channel adapter webhooks.
/// The adapter does NOT call TransactionEngine directly (CA-012) — orchestration lives here.
/// Always returns HTTP 200 for valid webhooks (CA-070).
/// The wire format of an inbound event is parsed by the adapter, not here: the pipeline builds one
/// channel-neutral envelope per request and hands the same envelope to validation and to processing.
/// </summary>
public static partial class ChannelWebhookPipeline
{
    /// <summary>
    /// Message of the exception raised when the webhook body crosses the size limit.
    /// </summary>
    private const string BodyOverLimitMessage = "The webhook request body exceeds the size limit.";

    /// <summary>
    /// Separator joining the values of a multi-valued header or query parameter into one envelope value.
    /// </summary>
    private const string MultiValueSeparator = ", ";

    /// <summary>
    /// Name of the write <see cref="RecordChannelRefusalAsync"/> logs for a refused channel identity attach.
    /// </summary>
    private const string RefusedIdentityAttach = "Channel identity not attached";

    /// <summary>
    /// Message of the failure the attach of a letter's mailbox hands back for a refusal decided before the
    /// attach was attempted (<see cref="AttachSignInLetterIdentityAsync"/>, <see cref="RefuseLetterAttachAsync"/>,
    /// <see cref="RefuseLetterOutOfTimeAsync"/>).
    /// </summary>
    private const string LetterNotAttached =
        "The mailbox of the letter was not attached to the transaction.";

    /// <summary>
    /// Name of the write <see cref="RecordChannelRefusalAsync"/> logs for a refused subject binding.
    /// </summary>
    private const string RefusedSubjectBinding = "The sender is not bound";

    /// <summary>
    /// Registers the endpoints of every registered channel.
    /// Besides the base webhook paths (default tenant, CA-168), registers optional routes with
    /// a tenant segment <c>…/webhook/{tenant}</c> (tenant demux, opt-in, CA-167), and lets each
    /// channel contribute endpoints of its own (<see cref="IChannelEndpointRegistrar"/>).
    /// </summary>
    /// <param name="endpoints">Endpoint router.</param>
    /// <param name="rateLimitPolicyName">Rate limiting policy name for webhook endpoints (null — no limit).</param>
    /// <returns>Router for chaining.</returns>
    public static IEndpointRouteBuilder MapChannelWebhookEndpoints(this IEndpointRouteBuilder endpoints, string? rateLimitPolicyName = null)
        => MapChannelWebhookEndpoints(
            endpoints,
            new ChannelEndpointPolicies { WebhookPolicyName = rateLimitPolicyName });

    /// <summary>
    /// Registers the endpoints of every registered channel, with the full set of rate-limit policies
    /// the host offers — the webhook budget and the budget of the user-facing pages a channel serves.
    /// </summary>
    /// <param name="endpoints">Endpoint router.</param>
    /// <param name="policies">Rate-limit policy names the host offers to the channel endpoints.</param>
    /// <returns>Router for chaining.</returns>
    public static IEndpointRouteBuilder MapChannelWebhookEndpoints(
        this IEndpointRouteBuilder endpoints,
        ChannelEndpointPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(policies);

        // Webhook routes of the channels registered through ChannelAdapterBuilder.AddChannel — the
        // shipped ones and the third-party ones alike. Same protective layers for every channel: one
        // rate-limit policy and the same 1 MB body size limit inside the handler.
        MapRegisteredChannelWebhooks(endpoints, policies.WebhookPolicyName);

        // Endpoints a channel maps for itself: a page of its own, an inbound route outside the path
        // convention. Nothing here knows which channel contributes what.
        MapChannelOwnEndpoints(endpoints, policies);

        return endpoints;
    }

    /// <summary>
    /// Maps the webhook endpoints of the registered channels. The registry is absent when no channel
    /// is registered — then nothing is mapped.
    /// </summary>
    /// <param name="endpoints">Endpoint router.</param>
    /// <param name="rateLimitPolicyName">Rate limiting policy name (null — no limit).</param>
    private static void MapRegisteredChannelWebhooks(
        IEndpointRouteBuilder endpoints,
        string? rateLimitPolicyName)
    {
        var registry = endpoints.ServiceProvider.GetService<CustomChannelRegistry>();
        if (registry is null)
        {
            return;
        }

        // The registration ↔ adapter cross-check does not live here: it is bound to the host start
        // (CustomChannelStartupValidator), so a host that maps no webhooks at all is diagnosed too.
        foreach (var registration in registry.Registrations)
        {
            // The registration is captured per channel — the handlers are keyed by what it declares.
            var channelType = registration.ChannelType;
            var options = registration.Options;

            if (options.MapWebhook)
            {
                ChannelTenantDemux.MapWithOptionalTenant(
                    endpoints,
                    HttpMethods.Post,
                    registration.WebhookPath,
                    ([FromServices] ChannelWebhookSupport? support, HttpContext httpContext) =>
                        HandleChannelWebhookAsync(
                            httpContext, support ?? ChannelWebhookSupport.None, channelType, options),
                    rateLimitPolicyName);
            }

            // The platform's verification handshake, when the channel declared one: a GET on the very
            // same path, answered with the challenge the platform sent.
            if (options.WebhookVerificationQueryKey is { Length: > 0 } challengeQueryKey)
            {
                ChannelTenantDemux.MapWithOptionalTenant(
                    endpoints,
                    HttpMethods.Get,
                    registration.WebhookPath,
                    ([FromServices] ChannelWebhookSupport? support, HttpContext httpContext) =>
                        HandleWebhookVerificationAsync(
                            httpContext, support ?? ChannelWebhookSupport.None, channelType, challengeQueryKey),
                    rateLimitPolicyName);
            }
        }
    }

    /// <summary>
    /// Lets every channel that registered an endpoint registrar map the endpoints of its own.
    /// </summary>
    /// <param name="endpoints">Endpoint router.</param>
    /// <param name="policies">Rate-limit policy names the host offers to the channel endpoints.</param>
    private static void MapChannelOwnEndpoints(
        IEndpointRouteBuilder endpoints,
        ChannelEndpointPolicies policies)
    {
        var registrars = endpoints.ServiceProvider.GetServices<IChannelEndpointRegistrar>();

        foreach (var registrar in registrars)
        {
            registrar.MapEndpoints(endpoints, policies);
        }
    }

    /// <summary>
    /// Builds the channel-neutral inbound envelope from the ASP.NET Core request.
    /// </summary>
    /// <remarks>
    /// The body is carried as raw bytes on purpose: a signature is computed over exactly those
    /// bytes, so no decoding step here (a text decode would swallow a byte-order mark) can make an
    /// adapter hash something other than what arrived. Decoding is the adapter's business.
    /// </remarks>
    /// <param name="request">HTTP request; its body is read here, and only here.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The neutral inbound envelope.</returns>
    private static async Task<ChannelInboundRequest> BuildInboundRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var body = HttpMethods.IsGet(request.Method)
            ? ReadOnlyMemory<byte>.Empty
            : await ReadBodyAsync(request.Body, cancellationToken);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(MultiValueSeparator, header.Value.Where(value => value is not null));
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in request.Query)
        {
            query[parameter.Key] = string.Join(MultiValueSeparator, parameter.Value.Where(value => value is not null));
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
    /// Reads the request body in full, refusing to hold more than
    /// <see cref="ChannelAdapterLimits.MaxWebhookBodySize"/>.
    /// </summary>
    /// <remarks>
    /// The limit lives here, in the copy itself, rather than in a buffering wrapper around the
    /// stream: the body is read exactly once per request — by this method — so a rewindable buffer
    /// would serve no second reader while reserving its whole ceiling on every request, including
    /// the ones the route rejects before reading anything. Reading it in chunks also means an
    /// over-limit body is refused as it arrives instead of being accepted in full first, and the
    /// request never touches a temp file, so a temp volume cannot fail a webhook read. The chunked
    /// copy itself is the component's shared one (<see cref="BoundedBodyReader"/>); what stays here
    /// is what belongs to this route — its limit and the answer an over-limit body gets.
    /// </remarks>
    /// <param name="body">Request body stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The body bytes.</returns>
    /// <exception cref="BadHttpRequestException">The body is over the limit.</exception>
    private static async Task<ReadOnlyMemory<byte>> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        await BoundedBodyReader.CopyAsync(
            body,
            buffer,
            ChannelAdapterLimits.MaxWebhookBodySize,
            static () => new BadHttpRequestException(BodyOverLimitMessage, StatusCodes.Status413PayloadTooLarge),
            cancellationToken);

        return buffer.ToArray();
    }

    /// <summary>
    /// Handles an incoming POST webhook of a channel registered through
    /// <c>ChannelAdapterBuilder.AddChannel</c> — the shipped channels and the third-party ones alike.
    /// The chain is: tenant demux (an unknown tenant is rejected with 403
    /// before the adapter is reached) → the neutral envelope, whose body is read once under the
    /// 1 MB limit → <c>ValidateWebhookAsync</c> over that envelope (rejection → 403) →
    /// <c>ProcessInboundEventAsync</c> over the same envelope, whose raw body bytes the adapter
    /// parses itself (the core imposes no body format) → the standard post-processing.
    /// Adapter code is never called outside a catch: an exception from <c>ValidateWebhookAsync</c>
    /// is treated as a rejection (fail-closed 403), and everything after a successful validation —
    /// the adapter's processing and the post-processing — always answers HTTP 200 (CA-070), including
    /// a Failure result and an unhandled exception: the details go to the log only, never to the
    /// response body. A body that cannot be read (over the size limit) answers 200 too — that is
    /// not a rejection verdict (CA-185).
    /// The catches are what the route owes the CA-070 invariant: an unhandled exception would turn
    /// into a 500 (with a stack trace on a Development host) and break it. They apply to every
    /// channel — a shipped adapter has no more licence to break the invariant than a third-party one,
    /// and one handler for all of them is what keeps the two from drifting apart.
    /// </summary>
    /// <param name="httpContext">HTTP request context.</param>
    /// <param name="support">Optional dependency of the route — the resolver of the tenant demux.</param>
    /// <param name="channelType">Channel type of the registered channel.</param>
    /// <param name="options">Registration settings of the channel (its status texts).</param>
    /// <returns>HTTP result of the webhook.</returns>
    private static async Task<IResult> HandleChannelWebhookAsync(
        HttpContext httpContext,
        ChannelWebhookSupport support,
        string channelType,
        ChannelRegistrationOptions options)
    {
        // Get dependencies from DI
        var adapters = httpContext.RequestServices.GetRequiredService<IEnumerable<IChannelAdapter>>();
        var logger = GetLogger(httpContext);
        // The processing of the event is a required service of the request, taken the way the
        // adapters above are: AddVeriqaChannelAdapters registers it together with the channel registry
        // this route is mapped from, so a route that exists has a processor to hand the event to.
        var processor = httpContext.RequestServices.GetRequiredService<ChannelInboundProcessor>();

        // The fact of the call is worth a span whether or not it turns out to address a live
        // transaction, so it opens here and not after the lookups. It belongs to the trace of THIS
        // request — the messenger propagates no trace context, and the sign-in trace that sent the
        // prompt ended long ago; the two are stitched by the correlation attribute set further down.
        // This span is the one place that has to mark an exception itself: the CA-070 invariant makes
        // the handler swallow every one of them and answer 200, so nothing upstream — not the host's
        // request instrumentation, not the caller — is left to record that the event failed. Hence
        // the SetStatus in each catch below; the status discipline the three catches follow is stated
        // once on ChannelActivitySource.
        using var inboundActivity = ChannelActivitySource.Source.StartActivity(ChannelTelemetry.InboundActivityName);
        inboundActivity?.SetTag(ChannelTelemetry.ChannelTypeTag, channelType);

        var cancellationToken = httpContext.RequestAborted;

        // Tenant demux (CA-167/168): determine the tenant BEFORE selecting credentials/adapter.
        var tenantResolution = await ChannelTenantDemux.ResolveTenantAsync(
            httpContext, support.ConfigurationResolver, channelType, logger, cancellationToken);

        if (!tenantResolution.Allowed)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var tenantId = tenantResolution.TenantId;

        // Tenant context for the processing path (CA-164): credentials/client are resolved per tenant.
        using var tenantScope = ChannelTenantContext.BeginScope(tenantId);

        // Find the adapter by (tenant, ChannelType). No adapter — there is nobody to
        // check the signature with, so the request is rejected before it is read: 403 (CA-185).
        var adapter = FindAdapter(adapters, channelType);
        if (adapter is null)
        {
            logger.LogError(
                "Adapter of channel {ChannelType} is not registered. " +
                "Webhook rejected — cannot validate the request without an adapter",
                channelType);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Building the envelope reads the body — the single read of the request, and the first one
        // to touch it. A body over the limit throws here. A failed read is not a validation verdict,
        // so it answers 200 (CA-185), with the known limitation that a connection dropped mid-body
        // lands here too and there is no connection left to answer it on.
        ChannelInboundRequest inboundRequest;
        try
        {
            inboundRequest = await BuildInboundRequestAsync(httpContext.Request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            inboundActivity?.SetStatus(ActivityStatusCode.Error);

            logger.LogWarning(
                ex,
                "Failed to read the webhook body of channel {ChannelType} — event dropped",
                channelType);
            return Results.Ok();
        }

        // Validate the webhook (CA-030) with the resolved tenant's credentials.
        // Validation code may throw (parsing a signature header is a classic case);
        // an exception is treated exactly like a Failure — fail-closed 403, details to the log only,
        // never a 500 that would expose a stack trace on an unauthenticated endpoint.
        // Only a real cancellation of THIS request is rethrown: a bare type check would also let
        // through the TaskCanceledException an HttpClient/Polly timeout inside the adapter raises
        // while the client connection is alive — and that would escape the handler as a 500.
        bool isWebhookValid;
        try
        {
            var validationResult = await adapter.ValidateWebhookAsync(inboundRequest, cancellationToken);
            isWebhookValid = validationResult.IsSuccess && validationResult.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            inboundActivity?.SetStatus(ActivityStatusCode.Error);

            logger.LogError(
                ex,
                "Unhandled exception while validating a webhook of channel {ChannelType}",
                channelType);
            isWebhookValid = false;
        }

        if (!isWebhookValid)
        {
            logger.LogWarning("Invalid webhook of channel {ChannelType} — rejected", channelType);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Everything past a successful validation answers 200 (CA-070): an adapter — which
        // may both return a Failure and throw — may not turn the webhook into a non-2xx answer.
        try
        {
            var inboundResult = await adapter.ProcessInboundEventAsync(inboundRequest, cancellationToken);
            if (inboundResult.IsFailure)
            {
                inboundActivity?.SetStatus(ActivityStatusCode.Error);
                inboundActivity?.SetTag(ChannelTelemetry.ErrorCodeTag, inboundResult.Error.Code);

                logger.LogWarning(
                    "Error processing an inbound event. ChannelType: {ChannelType}, Error: {ErrorCode}",
                    channelType,
                    inboundResult.Error.Code);
                return Results.Ok();
            }

            // The processing applies the facts the registration of the channel states for the core
            // (SPEC-042 R60, L62) itself — the owner of the registration and whether an answer takes
            // only this channel's prompt reference.
            await processor.ProcessAsync(
                inboundResult.Value,
                adapter,
                new ChannelReplyTexts(
                    options.ErrorMessageText,
                    options.StaleLinkReplyText,
                    options.UnaddressedReplyText,
                    options.UnmatchedCodeReplyText),
                asksInChannelPrompt: true,
                cancellationToken);
        }
        // As above: only a real cancellation of this request escapes; a spurious timeout of a
        // network call inside the adapter stays inside and keeps the CA-070 "always 200" invariant.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            inboundActivity?.SetStatus(ActivityStatusCode.Error);

            logger.LogError(
                ex,
                "Unhandled exception while processing a webhook of channel {ChannelType}",
                channelType);
        }

        // CA-070: always 200 for valid webhooks
        return Results.Ok();
    }

    /// <summary>
    /// Handles the platform's webhook verification handshake: a GET on the webhook path carrying a
    /// challenge the platform expects echoed back. Which query parameter holds the challenge is the
    /// channel's own declaration (<see cref="ChannelRegistrationOptions.WebhookVerificationQueryKey"/>);
    /// what makes the handshake genuine — a verify token, its spelling, the mode — is checked by the
    /// adapter's <c>ValidateWebhookAsync</c>, exactly as on the POST route.
    /// Adapter code is never called outside a catch here either: an exception while building the
    /// envelope or inside <c>ValidateWebhookAsync</c> is a failed handshake, answered fail-closed
    /// with 403 — never a 500 that would expose a stack trace on an unauthenticated endpoint. The
    /// route is open to any registered channel, a third-party one included, so its defence may not
    /// depend on how well-behaved the adapter is.
    /// </summary>
    /// <param name="httpContext">HTTP request context.</param>
    /// <param name="support">Optional dependency of the route — the resolver of the tenant demux.</param>
    /// <param name="channelType">Channel type of the registered channel.</param>
    /// <param name="challengeQueryKey">Query parameter carrying the challenge to echo back.</param>
    /// <returns>HTTP result of the handshake.</returns>
    private static async Task<IResult> HandleWebhookVerificationAsync(
        HttpContext httpContext,
        ChannelWebhookSupport support,
        string channelType,
        string challengeQueryKey)
    {
        // Get dependencies from DI.
        var adapters = httpContext.RequestServices.GetRequiredService<IEnumerable<IChannelAdapter>>();
        var logger = GetLogger(httpContext);
        var cancellationToken = httpContext.RequestAborted;

        // Tenant demux (CA-167/168): determine the tenant BEFORE selecting credentials/adapter.
        var tenantResolution = await ChannelTenantDemux.ResolveTenantAsync(
            httpContext, support.ConfigurationResolver, channelType, logger, cancellationToken);

        if (!tenantResolution.Allowed)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var tenantId = tenantResolution.TenantId;

        using var tenantScope = ChannelTenantContext.BeginScope(tenantId);

        // Find the adapter by (tenant, ChannelType).
        var adapter = FindAdapter(adapters, channelType);
        if (adapter is null)
        {
            logger.LogError(
                "Adapter of channel {ChannelType} is not registered. " +
                "Webhook verification rejected — cannot validate the verify token.",
                channelType);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Build the envelope and validate the handshake under one guard: on this route both steps
        // have the same failed outcome — the handshake is not proven, so it is refused (403).
        // An exception is treated exactly like a Failure, details to the log only.
        // Only a real cancellation of THIS request is rethrown: a bare type check would also let
        // through the TaskCanceledException an HttpClient/Polly timeout inside the adapter raises
        // while the client connection is alive — and that would escape the handler as a 500.
        bool isHandshakeValid;
        try
        {
            var inboundRequest = await BuildInboundRequestAsync(httpContext.Request, cancellationToken);
            var validationResult = await adapter.ValidateWebhookAsync(inboundRequest, cancellationToken);
            isHandshakeValid = validationResult.IsSuccess && validationResult.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                ex,
                "Unhandled exception while validating a webhook verification of channel {ChannelType}",
                channelType);
            isHandshakeValid = false;
        }

        if (!isHandshakeValid)
        {
            logger.LogWarning(
                "Invalid webhook verification of channel {ChannelType} — rejected", channelType);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Return the challenge upon successful validation.
        var challenge = httpContext.Request.Query[challengeQueryKey].FirstOrDefault();
        if (string.IsNullOrEmpty(challenge))
        {
            logger.LogWarning(
                "Webhook verification of channel {ChannelType} does not contain the challenge parameter",
                channelType);
            return Results.BadRequest();
        }

        return Results.Text(challenge);
    }

    /// <summary>
    /// Routes the result of an inbound event to the handler of its intent — the body of
    /// <see cref="ChannelInboundProcessor"/>, which states the services of the event's scope and the
    /// choices of the call.
    /// </summary>
    /// <remarks>
    /// SPEC-042 R60: with an owner stated, an event that names a transaction is acted on only when the
    /// transaction belongs to the owner. The transaction is read once, past the TTL gate, before any
    /// other work with it — before its deadline is looked at, so an expired transaction of another
    /// tenant is answered like a live one and like an identifier nobody issued. Any read that does not
    /// yield a transaction of the owner — another tenant's, none, or a failed read — gets the answer to
    /// an unknown transaction, which reads and spends nothing keyed by the transaction: no prompt
    /// reference, no journal, no write. A failed read is not retried by the handler, so no second read
    /// can find a transaction whose tenant was never compared.
    /// </remarks>
    /// <param name="processor">Processor holding the services of the event's scope.</param>
    /// <param name="inboundResult">Inbound event processing result.</param>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="replyTexts">Texts the channel states for itself.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator (null — the path
    /// asks no question in the chat: AuthStart auto-confirmation, SPEC-003 §4.8).</param>
    /// <param name="promptLocalizer">Channel message localizer (null — status texts stay in the base
    /// language, 1:1).</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — the outcome notice
    /// carries no prompt reference).</param>
    /// <param name="ownerTenantId">
    /// Tenant that owns the channel registration (SPEC-042 R60); null — the registration of the
    /// operator, whose events are processed for any tenant.
    /// </param>
    /// <param name="letterKey">What identifies the letter the event is, so that its decision is charged
    /// once (<see cref="TakeConfirmBudgetOnceAsync"/>); null — the event is no letter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal static async Task RouteInboundResultAsync(
        ChannelInboundProcessor processor,
        ChannelInboundResult inboundResult,
        IChannelAdapter adapter,
        ChannelReplyTexts replyTexts,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        IConfirmationPromptLocalizer? promptLocalizer,
        IChannelPromptMessageStore? promptMessageStore,
        string? ownerTenantId,
        string? letterKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(replyTexts);

        var transactionService = processor.TransactionService;
        var transactionStore = processor.TransactionStore;
        var timeProvider = processor.TimeProvider;
        var identityResolutionService = processor.IdentityResolutionService;
        var logger = processor.Logger;
        var metrics = processor.Metrics;
        var userAuthRateLimiter = processor.UserAuthRateLimiter;
        var receiptText = processor.ReceiptText;
        var displayIntentSource = processor.DisplayIntentSource;
        var eventPublisher = processor.EventPublisher;
        var completionBehaviorDispatcher = processor.CompletionBehaviorDispatcher;

        var errorText = replyTexts.ErrorMessageText;

        // Routing by result type (SPEC-003 §13): each intent brings its own mandatory set, so there
        // is no "which field is valid here" rule left to remember.
        switch (inboundResult)
        {
            case ChannelAuthStartResult start:
                await RouteAuthStartAsync(
                    processor, start, adapter, replyTexts, userAuthRateLimiter,
                    confirmationPromptOrchestrator, promptLocalizer, ownerTenantId, cancellationToken);
                break;

            case ChannelAuthStartByCodeResult startByCode:
                // A start by a code is served only for a channel that declared it — before any attempt
                // is taken or any code is looked up, whatever channel the event came through.
                if (adapter.Capabilities.CodeStart is null)
                {
                    DropUndeclaredCodeStart(adapter, logger, metrics);
                    break;
                }

                await HandleAuthStartByCodeAsync(
                    processor, startByCode, adapter, replyTexts, confirmationPromptOrchestrator,
                    promptLocalizer, ownerTenantId, cancellationToken);
                break;

            case ChannelAuthConfirmResult confirm:
                {
                    var decision = await ReadDecisionTransactionAsync(
                        transactionStore, confirm.TransactionId, ownerTenantId, promptMessageStore,
                        adapter, logger, metrics, cancellationToken);

                    await HandleAuthConfirmAsync(
                        processor, confirm, decision.Transaction, adapter, transactionService, transactionStore,
                        timeProvider, identityResolutionService, logger, metrics,
                        confirmationPromptOrchestrator,
                        receiptText, displayIntentSource, eventPublisher, completionBehaviorDispatcher,
                        errorText, promptLocalizer, decision.PromptMessageStore, letterKey, cancellationToken);
                    break;
                }

            case ChannelAuthDeclineResult decline:
                {
                    var decision = await ReadDecisionTransactionAsync(
                        transactionStore, decline.TransactionId, ownerTenantId, promptMessageStore,
                        adapter, logger, metrics, cancellationToken);

                    await HandleAuthDeclineAsync(
                        processor, decline, decision.Transaction, adapter, transactionService, transactionStore,
                        timeProvider, logger, metrics, confirmationPromptOrchestrator,
                        receiptText, displayIntentSource, eventPublisher, errorText,
                        promptLocalizer, decision.PromptMessageStore, cancellationToken);
                    break;
                }

            case ChannelPhoneSharedResult phoneShared:
                await HandlePhoneSharedAsync(
                    processor, phoneShared, adapter, confirmationPromptOrchestrator, promptMessageStore,
                    ownerTenantId, cancellationToken);
                break;

            case ChannelPhoneStepActionResult phoneStepAction:
                await ApplyPhoneStepActionAsync(
                    processor, phoneStepAction.TransactionId, phoneStepAction.Identity, phoneStepAction.Action,
                    adapter, confirmationPromptOrchestrator, promptMessageStore, ownerTenantId, cancellationToken);
                break;

            case ChannelMenuItemPressedResult menuItemPressed:
                await HandleMenuItemPressedAsync(processor, menuItemPressed, adapter, cancellationToken);
                break;

            case ChannelUnaddressedResult unaddressed:
                // A plain keyboard button of the phone step arrives as a message naming no transaction
                // (SPEC-041 L73); every other such message keeps the answer it always had.
                if (await TryApplyPhoneStepButtonTextAsync(
                        processor, unaddressed, adapter, confirmationPromptOrchestrator, promptMessageStore,
                        ownerTenantId, cancellationToken))
                {
                    break;
                }

                await HandleUnaddressedAsync(
                    unaddressed, adapter, replyTexts.UnaddressedReplyText, userAuthRateLimiter,
                    promptLocalizer, logger, metrics, cancellationToken);
                break;

            case ChannelUnrelatedResult:
                break;

            default:
                // A further intent added later must not bring the runtime down here.
                logger.LogWarning(
                    "Unknown inbound result type — no action taken. ChannelType: {ChannelType}, ResultType: {ResultType}",
                    adapter.ChannelType,
                    inboundResult.GetType().Name);
                break;
        }
    }

    /// <summary>
    /// Starts the authentication of the transaction an event names — the one path both a start by
    /// link and a start by code take once the transaction is known: the check of the owner of the
    /// registration (SPEC-042 R60), then <see cref="HandleAuthStartAsync"/>.
    /// </summary>
    /// <param name="processor">Processor holding the services of the event's scope.</param>
    /// <param name="start">The start, naming its transaction.</param>
    /// <param name="adapter">Channel adapter the event arrived through.</param>
    /// <param name="replyTexts">Texts the channel states for itself.</param>
    /// <param name="userAuthRateLimiter">
    /// Per-user rate limiter the start is charged to. Null — nothing is charged on this path: either
    /// the host registered no limiter, or the caller has already taken the attempt of this very event
    /// and every point of the path that would take one more must not.
    /// </param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator (null — the path
    /// asks no question in the chat).</param>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="ownerTenantId">Tenant that owns the channel registration (null — no owner).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task RouteAuthStartAsync(
        ChannelInboundProcessor processor,
        ChannelAuthStartResult start,
        IChannelAdapter adapter,
        ChannelReplyTexts replyTexts,
        IUserAuthRateLimiter? userAuthRateLimiter,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        IConfirmationPromptLocalizer? promptLocalizer,
        string? ownerTenantId,
        CancellationToken cancellationToken)
    {
        var logger = processor.Logger;
        var metrics = processor.Metrics;

        if (ownerTenantId is not null
            && await ReadOwnedTransactionAsync(
                processor.TransactionStore, start.TransactionId, ownerTenantId, adapter, logger, metrics,
                cancellationToken) is null)
        {
            // The answer of the handler's branch for an identifier no record answers to: the
            // unserved start is counted and the declared reply goes out in the language of
            // the channel snapshot — the transaction is not in hand to take one off.
            metrics.RecordUnservedStart(adapter.ChannelType);

            await ReplyToUnservedStartAsync(
                adapter, start.Identity, replyTexts.StaleLinkReplyText, start.Identity.Locale,
                userAuthRateLimiter, promptLocalizer, logger, metrics, start.TransactionId,
                cancellationToken);
            return;
        }

        // The transaction of the owner is read again by the handler, through the service
        // it needs the transaction from; the identifier is the one just compared, and the
        // tenant of a transaction is written once, at its creation.
        await HandleAuthStartAsync(
            processor, start, adapter, processor.TransactionService, processor.TimeProvider,
            processor.IdentityResolutionService, logger, metrics,
            userAuthRateLimiter, confirmationPromptOrchestrator, processor.ReceiptText,
            processor.DisplayIntentSource, processor.EventPublisher, processor.CompletionBehaviorDispatcher,
            replyTexts, promptLocalizer, cancellationToken);
    }

    /// <summary>
    /// Handles the start of authentication (the user followed the deep link).
    /// The mode is determined by the pipeline configuration (SPEC-003 §4.8, §13.2):
    /// AutoConfirm — following the deep link is treated as confirmation of intent;
    /// ConfirmationPrompt — a confirmation message with buttons and initiator context
    /// (SPEC-017) is sent, and the user makes the decision.
    /// An event whose transaction cannot be served at all is answered before any of that
    /// (SPEC-003 CA-200) — with the one text the registration declares for it, or with silence when
    /// it declares none.
    /// This handler shows no outcome itself; it carries the display intent onwards because the
    /// web-confirmation branch it hands over to does answer a closed window with a receipt.
    /// </summary>
    private static async Task HandleAuthStartAsync(
        ChannelInboundProcessor processor,
        ChannelAuthStartResult result,
        IChannelAdapter adapter,
        ITransactionService transactionService,
        TimeProvider timeProvider,
        IIdentityResolutionService identityResolutionService,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        IUserAuthRateLimiter? userAuthRateLimiter,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        IOutcomeReceiptText receiptText,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ITransactionEventPublisher? eventPublisher,
        ICompletionBehaviorDispatcher completionBehaviorDispatcher,
        ChannelReplyTexts replyTexts,
        IConfirmationPromptLocalizer? promptLocalizer,
        CancellationToken cancellationToken)
    {
        var errorText = replyTexts.ErrorMessageText;

        // The identifier arrives already parsed: the adapter is the one that reads it out of the
        // channel event, so an unparseable value never reaches this point (SPEC-003 §6.3).
        var transactionId = result.TransactionId;

        // Get the transaction for its ConcurrencyToken
        var txResult = await transactionService.GetTransactionAsync(transactionId, cancellationToken);
        if (txResult.IsFailure)
        {
            logger.LogWarning(
                "Failed to get the transaction for AuthStart. TransactionId: {TransactionId}, Error: {ErrorCode} — {ErrorMessage}",
                result.TransactionId,
                txResult.Error.Code,
                txResult.Error.Message);

            metrics.RecordUnservedStart(adapter.ChannelType);

            // A refused read is not the same as a missing record. The engine hands back
            // transaction_expired for a transaction it FOUND whose deadline is behind it and which
            // the cleanup pass has not promoted yet, and transaction_not_found only for an
            // identifier no record answers to. The journal is keyed by the transaction, so the found
            // one is written down under the same code the promoted one gets below — the two are the
            // two sides of that pass (SPEC-039 E48) — while an identifier with nothing behind it
            // writes nothing, or a stranger sending random ones would be the one filling the journal.
            // The counter above is what makes that second branch visible.
            if (txResult.Error.Code is TransactionErrorCodes.TransactionExpired)
            {
                await PublishChannelRefusalAsync(
                    eventPublisher, timeProvider, transactionId, adapter.ChannelType,
                    ChannelInteractionAuditCodes.LinkExpired, txResult.Error.Code, logger);
            }

            // The transaction is not in hand here even when it exists, so the recipient-locale chain
            // (B17) collapses to the channel snapshot locale and then to the base language — the same
            // chain a transaction found in Expired takes below, so neither the side of the cleanup
            // pass nor the existence of the transaction shows through in the language of the answer.
            await ReplyToUnservedStartAsync(
                adapter, result.Identity, replyTexts.StaleLinkReplyText, result.Identity.Locale,
                userAuthRateLimiter, promptLocalizer, logger, metrics, transactionId,
                cancellationToken);
            return;
        }

        TagCorrelatingTrace(txResult.Value);

        // Recipient-locale chain (B17): channel snapshot locale → initiation-page language stored in
        // the transaction → base language (the localizer applies the base fallback for null).
        var recipientLocale = result.Identity.Locale ?? txResult.Value.GetUiLocale();

        // Only the channel subject the transaction is bound to may act on it (SPEC-007 UI-022, SPEC-039
        // N34). Compared first — before the expiry branch and before the rate-limit block — so another
        // sender learns nothing of the state of the transaction and cannot spend a budget whose refusal
        // fails the owner's sign-in.
        if (txResult.Value.IsBoundToAnotherSubject(result.Identity))
        {
            metrics.RecordUnservedStart(adapter.ChannelType);

            // A sender the transaction does not admit is answered the way it always is, with silence
            // (SPEC-003 CA-200) — whoever the transaction is bound to.
            if (!await IsSenderAdmittedAsync(
                    txResult.Value, result.Identity, adapter, transactionService, eventPublisher,
                    timeProvider, logger, cancellationToken))
            {
                return;
            }

            await RecordSubjectMismatchAsync(transactionId, adapter, eventPublisher, timeProvider, logger);

            // Answered as an identifier with nothing behind it (SPEC-039 C15): the same text, in the
            // language of the snapshot alone, under the same budget.
            await ReplyToUnservedStartAsync(
                adapter, result.Identity, replyTexts.StaleLinkReplyText, result.Identity.Locale,
                userAuthRateLimiter, promptLocalizer, logger, metrics, transactionId,
                cancellationToken);
            return;
        }

        // The cleanup pass has already written the expiry down. It is the same occurrence the failed
        // read above answers — the two are simply the two sides of that pass — so it gets the same
        // text (SPEC-003 CA-200). It stands here, before the rate-limit block below, for two reasons:
        // the budget is spent once per event, inside the helper, and the limiter's FailTransactionAsync
        // has nothing to write on a transaction that is already terminal.
        // No other state is looked at: what a transaction that was already decided is answered with
        // belongs to the orchestrator and to the surface it chose.
        if (txResult.Value.State is TransactionState.Expired)
        {
            // The branch leaves a trace of its own whether or not it answers: a channel that declares
            // no text is silent, and without this line the event would leave nothing behind at all.
            logger.LogWarning(
                "AuthStart on a transaction the cleanup pass has already expired. " +
                "TransactionId: {TransactionId}, State: {State}",
                transactionId,
                txResult.Value.State);

            metrics.RecordUnservedStart(adapter.ChannelType);

            // The transaction was found, so this refusal reaches the journal (SPEC-039 E48). No
            // engine code went with it — the state itself is the reason — hence no details.
            await PublishChannelRefusalAsync(
                eventPublisher, timeProvider, transactionId, adapter.ChannelType,
                ChannelInteractionAuditCodes.LinkExpired, refusalErrorCode: null, logger);

            // The snapshot locale alone, not the recipientLocale chain above: the answer must read
            // the same on both sides of the cleanup pass, and the side that has not been promoted yet
            // has no transaction in hand to take a language off. Preferring the better language here
            // would let the recipient tell a transaction that exists from one that never did — the
            // very distinction the single text is there to withhold (SPEC-039 C15 / R51).
            await ReplyToUnservedStartAsync(
                adapter, result.Identity, replyTexts.StaleLinkReplyText, result.Identity.Locale,
                userAuthRateLimiter, promptLocalizer, logger, metrics, transactionId,
                cancellationToken);
            return;
        }

        // Everything below acts on the transaction for this sender: the question with the subject is
        // sent, the transaction is failed (an exhausted budget, a channel that cannot continue, a
        // subject that cannot be worded), the identity is attached, or the sign-in is finalized — and
        // the finalization succeeds on its idempotent branch without asking who is writing. So the
        // sender is gated once, here, for the whole start. A refused sender is answered with silence
        // on every surface: a sender the transaction does not admit must not be able to make the core
        // send it a message (SPEC-003 CA-200 states it for the web-page surface, and which surface
        // applies is resolved only below). The budget is not taken either — there is nothing to send,
        // and the limiter's own refusal fails the transaction, which is exactly what this sender may
        // not cause.
        if (!await IsSenderAdmittedAsync(
                txResult.Value, result.Identity, adapter, transactionService, eventPublisher,
                timeProvider, logger, cancellationToken))
        {
            metrics.RecordUnservedStart(adapter.ChannelType);
            return;
        }

        // The user has already confirmed and the transaction waits for missing claims (SPEC-041
        // L91): the snapshot is frozen, so the start is recorded and, on a channel that updates its
        // messages, answered like one on a transaction that no longer accepts it. A channel that
        // does not is not sent that reply — it would arrive as one more message under the request
        // of the step in progress — and is sent the request again instead: the message of the user
        // may have taken its buttons away (SPEC-003 CA-200). A button press during the completion
        // is answered the same way. Like the expiry above,
        // the branch stands before the rate-limit block, whose refusal would fail the transaction
        // the user is completing; the reply takes the budget itself.
        if (txResult.Value.IsCompletingClaims())
        {
            metrics.RecordUnservedStart(adapter.ChannelType);

            await RecordClaimCompletionRefusalAsync(
                transactionId, adapter, refusalErrorCode: null, eventPublisher, timeProvider, logger);

            if (adapter.Capabilities.SupportsMessageUpdate)
            {
                await ReplyToUnservedStartAsync(
                    adapter, result.Identity, replyTexts.StaleLinkReplyText, result.Identity.Locale,
                    userAuthRateLimiter, promptLocalizer, logger, metrics, transactionId,
                    cancellationToken);
            }
            else
            {
                // The limiter of this start, not the one of the processor: a start whose attempt
                // the caller has already taken arrives here with none.
                await SendPhoneRequestAgainAsync(
                    processor, txResult.Value, result.Identity, userAuthRateLimiter,
                    UserAuthRateLimitOperations.AuthStart, cancellationToken);
            }

            return;
        }

        // Per-user rate limiting: check before confirmation. The partition is scoped to the
        // auth-start operation so this check does not drain the confirmation/token budgets
        // of the same user (see UserAuthRateLimitOperations).
        if (userAuthRateLimiter is not null)
        {
            var identitySnapshot = result.Identity;
            var userKey = NormalizeUserKey(identitySnapshot);
            var rateLimitResult = await userAuthRateLimiter.TryAcquireAsync(
                UserAuthRateLimitOperations.AuthStart + userKey, cancellationToken);

            if (!rateLimitResult.IsAllowed)
            {
                logger.LogWarning(
                    "Per-user rate limit exceeded in AuthStart. TransactionId: {TransactionId}, UserKeyHash: {UserKeyHash}",
                    result.TransactionId,
                    LogMasking.Fingerprint(userKey));

                var failRlResult = await transactionService.FailTransactionAsync(
                    transactionId,
                    TransactionErrorCodes.UserRateLimitExceeded,
                    txResult.Value.ConcurrencyToken,
                    CancellationToken.None);

                if (failRlResult.IsFailure)
                {
                    logger.LogWarning(
                        "Failed to move the transaction to Failed (rate limit, AuthStart). TransactionId: {TransactionId}",
                        result.TransactionId);
                }

                // AuthStart is not a transaction outcome, so this stays a plain message and is not
                // gated by the outcome capability — a channel that shows no outcome still gets it.
                await SendUserMessageAsync(
                    adapter,
                    result.Identity.ChannelUserId,
                    Localize(promptLocalizer, MessageTemplateNaturalKeys.OutcomeTooManyAttempts, recipientLocale),
                    logger,
                    metrics,
                    cancellationToken);
                return;
            }
        }

        // The sender enters the transaction, so it is bound to this subject here — whatever surface the
        // orchestrator resolves below, which is not known yet: the question of the channel then goes to
        // the bound subject only, and a later start of another sender is refused above. The transaction
        // the binding hands back carries the concurrency token it left, so every write below goes with it.
        var bindResult = await transactionService.BindChannelSubjectAsync(
            transactionId, result.Identity, cancellationToken);

        if (bindResult.IsFailure)
        {
            await AnswerRefusedBindingAsync(
                bindResult.Error.Code, result.Identity, transactionId, adapter,
                replyTexts.StaleLinkReplyText, promptLocalizer, eventPublisher, timeProvider, logger,
                metrics, cancellationToken);
            return;
        }

        var transaction = bindResult.Value;

        // The orchestrator resolves the effective surface (SPEC-012 §4.4) and returns what to do:
        // InChannel — a confirmation message with buttons and initiator context (SPEC-017) has been
        // sent, the transaction will be confirmed after a button press (AuthConfirm); OnWebPage — the
        // question belongs to the core page, so the chat gets no question, only the one notice
        // sending the sender to that page (a transaction that refuses the event is a different
        // matter — CA-200); None — the previous auto-confirmation applies.
        if (confirmationPromptOrchestrator is not null)
        {
            var outcome = await confirmationPromptOrchestrator.HandleAuthStartConfirmationAsync(
                transaction,
                adapter,
                result.Identity,
                recipientLocale,
                cancellationToken);

            if (outcome is AuthStartConfirmationOutcome.HandledInChannel)
            {
                // The answer is now awaited in the chat, and the page is told so: it stops offering
                // the way around a silent chat. The same predicate decides the replay to a page that
                // connects later, so the two cannot disagree.
                if (transaction.IsAwaitingAnswerInChannel())
                {
                    await PublishChannelAnsweredAsync(
                        eventPublisher, timeProvider, transactionId, adapter.ChannelType, logger);
                }

                return;
            }

            if (outcome is AuthStartConfirmationOutcome.AwaitingWebConfirmation)
            {
                await AttachIdentityForWebConfirmationAsync(
                    result.Identity, transaction, adapter, transactionService, timeProvider,
                    recipientLocale, replyTexts.StaleLinkReplyText, receiptText, displayIntentSource,
                    processor.WebConfirmationNoticeSender, promptLocalizer, eventPublisher, logger, metrics,
                    cancellationToken);
                return;
            }

            if (outcome is AuthStartConfirmationOutcome.ChannelCannotContinue)
            {
                await FailForChannelCannotContinueAsync(
                    transactionId, transactionService, transaction.ConcurrencyToken, logger);

                // Nothing is sent to the user in the channel — it has just declared it cannot carry
                // the confirmation out — and auto-confirmation is not performed either. The user
                // sees the error in the browser through the existing Failed-status branch.
                return;
            }

            if (outcome is AuthStartConfirmationOutcome.ConfirmationSubjectUnavailable)
            {
                // There is no wording of the subject, so the question was not asked (SPEC-039 E28).
                // Waiting out the TTL would leave the user in front of a channel that answered
                // nothing, and auto-confirming would complete an action nobody was shown: the
                // transaction ends here, and the user gets the channel's own neutral error text —
                // the one its options carry, not a wording of this particular cause.
                await FailForConfirmationSubjectUnavailableAsync(
                    transactionId, transactionService, transaction.ConcurrencyToken, logger);

                await SendUserMessageAsync(
                    adapter,
                    result.Identity.ChannelUserId,
                    Localize(promptLocalizer, errorText, recipientLocale),
                    logger,
                    metrics,
                    cancellationToken);
                return;
            }
        }

        // Following the deep link IS the decision on this path (SPEC-003 §4.8), so it passes the same
        // gate as a decision reported later (SPEC-039 E41). The gate matters here precisely when the
        // branch above made no decision at all — no confirmation orchestration is wired, so nothing
        // asked the question and nothing can establish that the subject was shown. The transaction is
        // then left as it is, to its TTL: auto-confirming it would complete the action the relying
        // party named without ever showing it, which is what E41 exists to prevent.
        if (!await AcceptsChannelDecisionAsync(
                transaction, adapter, confirmationPromptOrchestrator, logger, cancellationToken))
        {
            return;
        }

        // Whether the sign-in was completed BEFORE this event is taken as a value here: a transaction
        // store is free to hand out the very instance it keeps, and the finalization below would then
        // move this one to Completed as well, leaving nothing to tell the two cases apart.
        var wasCompleted = transaction.State is TransactionState.Completed;

        // Automatically confirm and complete the transaction (SPEC-003 §4.8)
        var finalizeResult = await ChannelTransactionFinalizer.ConfirmAndCompleteAsync(
            result.Identity, transactionId, transactionService,
            identityResolutionService, transaction.ConcurrencyToken, logger, cancellationToken);

        // A parallel delivery set the claims completion mark between the read above and this write:
        // the same frozen snapshot the pre-check answers, with the same text. The budget of this
        // event is already spent by the limiter block above.
        if (finalizeResult.IsFailure
            && await IsRefusedByClaimCompletionAsync(
                finalizeResult.Error.Code, transactionId, transactionService, cancellationToken))
        {
            metrics.RecordUnservedStart(adapter.ChannelType);

            await RecordClaimCompletionRefusalAsync(
                transactionId, adapter, finalizeResult.Error.Code, eventPublisher, timeProvider, logger);

            await SendDeclaredReplyAsync(
                adapter, result.Identity, replyTexts.StaleLinkReplyText, result.Identity.Locale,
                promptLocalizer, logger, metrics, cancellationToken);
            return;
        }

        // The confirmation left the transaction waiting for missing claims (SPEC-041 L91): the
        // sign-in is not confirmed, so there is no receipt — what the user sees in the chat now is
        // the business of the step starter, which has already run.
        if (finalizeResult.IsSuccess && finalizeResult.Value.IsCompletingClaims())
        {
            return;
        }

        var completed = finalizeResult.IsSuccess;

        // Send a text notification (without buttons). A confirmed outcome is a RECEIPT — the core
        // states its wording; an error stays the channel's own text and is localized here.
        var outcomeText = completed
            ? await receiptText.RenderAsync(
                ReceiptAddressOf(TransactionOutcome.Confirmed, adapter, transaction),
                recipientLocale,
                transaction.GetUiTimeZone(),
                // The moment of the outcome is the one the completion wrote down, read off the
                // instance the finalizer returned: the one held before the call is the stale one.
                finalizeResult.Value.UpdatedAt,
                // The gate above accepted this channel, so the question stood before this user here.
                SenderSlotSource(transaction, result.Identity),
                TransactionResolutionContext.For(transaction),
                cancellationToken)
            : Localize(promptLocalizer, errorText, recipientLocale);

        await SendUserMessageAsync(
            adapter,
            result.Identity.ChannelUserId,
            outcomeText,
            logger,
            metrics,
            cancellationToken);

        // The post-completion action follows the receipt and never replaces it (SPEC-012 §4.9). A
        // repeated start on a sign-in that is already completed passes the finalization on its
        // idempotent branch, so the action is performed only by the event that completed the sign-in;
        // it is handed the instance the finalizer returned — the one carrying the resolved identity.
        if (completed && !wasCompleted)
        {
            await completionBehaviorDispatcher.DispatchAsync(finalizeResult.Value, cancellationToken);
        }
    }

    /// <summary>
    /// The address of a receipt shown INSIDE a channel for a transaction the point has loaded: what
    /// the render point knows, and nothing more. The wording of that address is chosen by the
    /// resolution (SPEC-036 TPL-116), never here.
    /// </summary>
    /// <param name="outcome">Outcome being reported — a terminal one, or the claims completion state.</param>
    /// <param name="adapter">Channel adapter the receipt travels through.</param>
    /// <param name="transaction">Transaction the decision was made on.</param>
    /// <returns>The address of the receipt.</returns>
    internal static OutcomeReceiptAddress ReceiptAddressOf(
        TransactionOutcome outcome,
        IChannelAdapter adapter,
        Transaction transaction) =>
        new(outcome,
            OutcomeReceiptSurfaces.ForChannel(adapter),
            transaction.Type,
            transaction.ConfirmationSnapshot?.ActionType,
            adapter.ChannelType,
            transaction.InitiatorContextSnapshot?.DeviceType);

    /// <summary>
    /// The address of a receipt shown inside a channel when the transaction could NOT be read
    /// (SPEC-003 CA-192): the type of a transaction that is not there is unknowable, so the address
    /// states none and the wording that narrows by no type answers instead
    /// (SPEC-036 TPL-107 — the one deliberate departure from the byte-for-byte rule TPL-098).
    /// </summary>
    /// <param name="outcome">Terminal outcome being reported.</param>
    /// <param name="adapter">Channel adapter the receipt travels through.</param>
    /// <returns>The address of the receipt.</returns>
    private static OutcomeReceiptAddress ReceiptAddressWithoutTransaction(
        TransactionOutcome outcome,
        IChannelAdapter adapter) =>
        new(outcome,
            OutcomeReceiptSurfaces.ForChannel(adapter),
            ChannelType: adapter.ChannelType);

    /// <summary>
    /// Answers a pressed button as one naming a transaction that is not there — the single answer both
    /// a missing transaction and a sender other than the bound subject get, so the two cannot be told
    /// apart (SPEC-039 C15).
    /// </summary>
    /// <remarks>
    /// A pressed button is always answered (SPEC-003 §13.3, CA-192): a transaction that is not there is
    /// an outcome too, and leaving without one would keep the platform's loading indicator spinning
    /// until its own timeout. "Never existed" and "reaped long ago" are not told apart — to the user
    /// both mean the same thing — hence the receipt of an expiry. There is no transaction to word it
    /// with, so the recipient-locale chain (B17) collapses to the channel snapshot locale and then to
    /// the base language.
    /// </remarks>
    /// <param name="adapter">Channel adapter the answer travels through.</param>
    /// <param name="transactionId">Transaction identifier the button named.</param>
    /// <param name="identity">Identity snapshot of the sender.</param>
    /// <param name="inboundToken">Opaque token of the live interaction (null — no interaction).</param>
    /// <param name="receiptText">Port the terminal wording comes from.</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — no prompt reference is
    /// consumed; the one stored for a transaction bound to another subject belongs to that subject).</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task ShowUnknownTransactionAsync(
        IChannelAdapter adapter,
        TransactionId transactionId,
        ChannelIdentitySnapshot identity,
        ChannelInboundToken? inboundToken,
        IOutcomeReceiptText receiptText,
        IChannelPromptMessageStore? promptMessageStore,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
        => await ShowOutcomeAsync(
            adapter, transactionId, identity.ChannelUserId, inboundToken,
            TransactionOutcome.Expired,
            await receiptText.RenderAsync(
                ReceiptAddressWithoutTransaction(TransactionOutcome.Expired, adapter),
                identity.Locale,
                // Neither the zone nor the moment of the outcome has a source here: the transaction
                // they are both read off is not in hand. Stating "now" instead would be this handler's
                // own clock passed off as the moment the transaction ended, so nothing is stated and a
                // moment falls back to UTC.
                recipientTimeZone: null,
                outcomeMoment: null,
                // Nor any value of the transaction: they are read off the same transaction
                // (SPEC-036 TPL-107).
                transaction: null,
                // There is no transaction to take ownership from. The only level left is the tenant of
                // the ambient channel scope, which the single context builder puts in for a null
                // transaction.
                TransactionResolutionContext.For(null),
                cancellationToken),
            promptMessageStore,
            displayIntentSource,
            // The same context the wording above is addressed with: the receipt and the way it is shown
            // are one answer about one notice.
            TransactionResolutionContext.For(null),
            logger, metrics, cancellationToken);

    /// <summary>
    /// Reads the transaction a channel decision names, from the STORE — past the TTL gate of the
    /// transaction service, which answers an expiry by withholding the transaction.
    /// </summary>
    /// <remarks>
    /// A store that cannot answer must not leave a pressed button unanswered (SPEC-003 CA-192), so a
    /// failure of the read is turned into the same "nothing to answer with" a missing transaction
    /// produces; why it failed stays in the deployment's log. Only a real cancellation of the request
    /// escapes.
    /// </remarks>
    /// <param name="transactionStore">Transaction store.</param>
    /// <param name="transactionId">Transaction identifier the decision names.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction, or null when there is none to answer with.</returns>
    private static async Task<Transaction?> ReadForDecisionAsync(
        ITransactionStore transactionStore,
        TransactionId transactionId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            return await transactionStore.GetByIdAsync(transactionId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Failed to read the transaction a channel decision names. TransactionId: {TransactionId}",
                transactionId);
            return null;
        }
    }

    /// <summary>
    /// Reads the transaction a channel decision names and states the store its answer may take a
    /// prompt reference from. Without an owner of the registration it is the read every decision has
    /// always had (<see cref="ReadForDecisionAsync"/>); with one, it is the single read of
    /// <see cref="ReadOwnedTransactionAsync"/>, and a decision it yields no transaction for is answered
    /// with no store at all — the answer to an unknown transaction reads nothing keyed by the
    /// transaction (SPEC-042 R60).
    /// </summary>
    /// <param name="transactionStore">Transaction store.</param>
    /// <param name="transactionId">Transaction identifier the decision names.</param>
    /// <param name="ownerTenantId">Tenant that owns the channel registration (null — no owner).</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — no prompt reference).</param>
    /// <param name="adapter">Channel adapter the decision arrived through.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a foreign transaction is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction to act on (null — none to answer with) and the store of the answer.</returns>
    private static async Task<DecisionTransaction> ReadDecisionTransactionAsync(
        ITransactionStore transactionStore,
        TransactionId transactionId,
        string? ownerTenantId,
        IChannelPromptMessageStore? promptMessageStore,
        IChannelAdapter adapter,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (ownerTenantId is null)
        {
            return new DecisionTransaction(
                await ReadForDecisionAsync(transactionStore, transactionId, logger, cancellationToken),
                promptMessageStore);
        }

        var owned = await ReadOwnedTransactionAsync(
            transactionStore, transactionId, ownerTenantId, adapter, logger, metrics, cancellationToken);

        return new DecisionTransaction(owned, owned is null ? null : promptMessageStore);
    }

    /// <summary>
    /// The one read of the transaction an event of a registration with an owner names (SPEC-042
    /// R60): from the STORE, past the TTL gate, so the tenant is compared before the deadline is. It
    /// yields the transaction only when its tenant is the owner, compared ordinally; a transaction of
    /// another tenant — the implicit one included — is logged with the identifier as a fingerprint and
    /// counted, and a failed read is logged; both, like a missing record, yield null.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ReadForDecisionAsync"/> the three outcomes stay apart here: a failed read is
    /// not a transaction of another tenant — it is not proven to be — so it is not counted as one,
    /// yet it is not taken for "nothing there" and read again later either. Only a real cancellation
    /// of the request escapes.
    /// </remarks>
    /// <param name="transactionStore">Transaction store.</param>
    /// <param name="transactionId">Transaction identifier the event names.</param>
    /// <param name="ownerTenantId">Tenant that owns the channel registration.</param>
    /// <param name="adapter">Channel adapter the event arrived through.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a foreign transaction is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transaction of the owner, or null.</returns>
    private static async Task<Transaction?> ReadOwnedTransactionAsync(
        ITransactionStore transactionStore,
        TransactionId transactionId,
        string ownerTenantId,
        IChannelAdapter adapter,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
    {
        Transaction? transaction;

        try
        {
            transaction = await transactionStore.GetByIdAsync(transactionId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Failed to read the transaction a channel event of an owned registration names — answered as an unknown transaction. ChannelType: {ChannelType}, TransactionIdHash: {TransactionIdHash}",
                adapter.ChannelType,
                LogMasking.Fingerprint(transactionId.ToString()));
            return null;
        }

        if (transaction is null
            || string.Equals(transaction.GetTenantId(), ownerTenantId, StringComparison.Ordinal))
        {
            return transaction;
        }

        logger.LogWarning(
            "Channel event names a transaction of another tenant — answered as an unknown transaction. ChannelType: {ChannelType}, RouteTenant: {RouteTenant}, TransactionIdHash: {TransactionIdHash}",
            adapter.ChannelType,
            ChannelTenantContext.CurrentTenantId,
            LogMasking.Fingerprint(transactionId.ToString()));

        metrics.RecordForeignTransactionEvent(adapter.ChannelType);
        return null;
    }

    /// <summary>
    /// The slot source of a receipt answering an event of a sender in the channel: the transaction, with the
    /// channel snapshot of the sender as the account the receipt names (SPEC-036 TPL-124). The instance read
    /// before the outcome was written down carries no snapshot of its own yet, and the sender of the event
    /// is the account the question stood before.
    /// </summary>
    /// <param name="transaction">Transaction the receipt reports.</param>
    /// <param name="sender">Identity snapshot of the sender of the event.</param>
    /// <returns>The slot source.</returns>
    private static TransactionSlotSource SenderSlotSource(Transaction transaction, ChannelIdentitySnapshot sender) =>
        TransactionSlotSource.From(transaction) with { ChannelIdentity = sender };

    /// <summary>
    /// The receipt of a closed sign-in window, worded for the transaction that closed. The ONE place
    /// its address is built, so the pre-check and the refusal of a write answer the same occurrence
    /// with the same wording.
    /// </summary>
    /// <param name="adapter">Channel adapter the receipt travels through.</param>
    /// <param name="transaction">Transaction whose window closed.</param>
    /// <param name="slotSource">Source of the values the receipt may be worded with — of the same
    /// transaction, carrying its caller values where the question of the transaction stood before this
    /// user in this channel and none where it never did (SPEC-036 TPL-123, TPL-124). Stated by the
    /// caller because only the caller knows which of the two it is.</param>
    /// <param name="recipientLocale">Recipient locale (null — base language).</param>
    /// <param name="receiptText">Port the terminal wording comes from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The receipt text, already localized.</returns>
    private static ValueTask<string> ExpiredReceiptAsync(
        IChannelAdapter adapter,
        Transaction transaction,
        TransactionSlotSource slotSource,
        string? recipientLocale,
        IOutcomeReceiptText receiptText,
        CancellationToken cancellationToken)
        => receiptText.RenderAsync(
            ReceiptAddressOf(TransactionOutcome.Expired, adapter, transaction),
            recipientLocale,
            transaction.GetUiTimeZone(),
            // The moment of THIS outcome is the deadline the transaction ran into, not the moment the
            // button was pressed — the same moment the confirmation page states for the same event.
            transaction.ExpiresAt,
            slotSource,
            TransactionResolutionContext.For(transaction),
            cancellationToken);

    /// <summary>
    /// Has the engine move a transaction whose deadline a pressed button met into Expired, once the
    /// press has been answered with the receipt of the expiry.
    /// </summary>
    /// <remarks>
    /// After the answer, never before it: the answer has taken the record of the chat, so the expiry
    /// handler the engine's event wakes finds nothing and the chat gets one receipt — the one the press
    /// produced, the same before and after the cleanup pass. A transaction the pass already moved is
    /// left as it is by the engine.
    /// </remarks>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="transactionId">Transaction the press addressed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed operation.</returns>
    private static Task ExpireAfterLateDecisionAsync(
        ITransactionService transactionService,
        TransactionId transactionId,
        CancellationToken cancellationToken)
        => transactionService.ExpireOverdueAsync(transactionId, cancellationToken);

    /// <summary>
    /// Reads a refused write of the user's decision by its class (SPEC-003 CA-192) and states what
    /// the sender is answered with.
    /// </summary>
    /// <remarks>
    /// The REASON of the refusal is never disclosed to the sender; what the transaction ended in is.
    /// An expiry is told as an expiry because that is what happened to the transaction, and a
    /// transaction that a parallel decision has already finished is told by the receipt of that
    /// outcome — pressing the other button earns the same answer pressing the same one twice does.
    /// Everything left is the channel's own error text. The code goes to the deployment's log
    /// instead — at a level that says whose problem it is: a closed window is a normal end, a
    /// decision recorded in parallel is a benign race, and a refusal of ours is a warning.
    /// <para>
    /// The expiry and the race are also the refusals the integrator's journal records (SPEC-039 E48),
    /// and this is the one place they are written from, so a press reaches the journal once. A race
    /// that turns out to be a repeat of the very decision the press asked for is not a refusal — the
    /// same press delivered twice — and writes nothing, exactly as a repeated confirmation, which the
    /// engine answers with success, writes nothing. A refusal of ours is not a refusal of the surface
    /// and stays in the log.
    /// </para>
    /// </remarks>
    /// <param name="errorCode">Code the write of the decision failed with.</param>
    /// <param name="pressedDecision">Outcome the pressed button asked for — tells a repeat of the same
    /// decision from a decision that someone else recorded.</param>
    /// <param name="transaction">Transaction as it was read before the write.</param>
    /// <param name="sender">Identity snapshot of the sender of the decision — the account a receipt
    /// names.</param>
    /// <param name="adapter">Channel adapter the decision arrived through.</param>
    /// <param name="transactionStore">Transaction store the single re-read goes to.</param>
    /// <param name="eventPublisher">Bus the refusal is written to (null — no journal).</param>
    /// <param name="timeProvider">Clock the moment of the audit event is read off.</param>
    /// <param name="recipientLocale">Recipient locale (null — base language).</param>
    /// <param name="receiptText">Port the terminal wording comes from.</param>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="errorText">Message text shown to the user on error.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// What to show the sender; null — the transaction waits for missing claims (SPEC-041 L91), so
    /// there is no terminal outcome to show and the caller answers the press with the state of that
    /// wait. The refusal is already journaled.
    /// </returns>
    private static async Task<RefusedDecisionAnswer?> AnswerToRefusedDecisionAsync(
        string errorCode,
        TransactionOutcome pressedDecision,
        Transaction transaction,
        ChannelIdentitySnapshot sender,
        IChannelAdapter adapter,
        ITransactionStore transactionStore,
        ITransactionEventPublisher? eventPublisher,
        TimeProvider timeProvider,
        string? recipientLocale,
        IOutcomeReceiptText receiptText,
        IConfirmationPromptLocalizer? promptLocalizer,
        string errorText,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        switch (DecisionRefusalClassifier.Classify(errorCode))
        {
            case DecisionRefusalClass.Expired:
                // The window closed between the read and the write: the same occurrence the pre-check
                // answers, and a normal end of a sign-in rather than a failure of anything.
                logger.LogInformation(
                    "The decision arrived after the sign-in window closed. TransactionId: {TransactionId}, Error: {ErrorCode}",
                    transaction.Id,
                    errorCode);

                // A transaction the store no longer holds is logged above and not journaled, as on every
                // other entry of the channel (SPEC-039 E48, RecordChannelRefusalAsync).
                if (errorCode is not TransactionErrorCodes.TransactionNotFound)
                {
                    await PublishChannelRefusalAsync(
                        eventPublisher, timeProvider, transaction.Id, adapter.ChannelType,
                        ChannelInteractionAuditCodes.LinkExpired, errorCode, logger);
                }

                // A write is attempted only past the decision gate, so the question of this transaction
                // stood before this user in this channel and the receipt may name its subject.
                return new RefusedDecisionAnswer(
                    TransactionOutcome.Expired,
                    await ExpiredReceiptAsync(
                        adapter, transaction, SenderSlotSource(transaction, sender),
                        recipientLocale, receiptText, cancellationToken));

            case DecisionRefusalClass.Race:
                {
                    // A decision was recorded in parallel — a second delivery of the same press, the web
                    // page answering at the same moment, the other button of a question already answered.
                    // What the transaction has BECOME decides the answer, so it is re-read: once, and the
                    // sender is told the outcome it actually ended in. In this delivery that answer is
                    // the same whichever button was pressed — a decision that stands is not re-opened by
                    // pressing the other one, and reporting a failure instead told the user their own
                    // finished sign-in had gone wrong.
                    logger.LogDebug(
                        "The decision was refused — one is already recorded. TransactionId: {TransactionId}, Error: {ErrorCode}",
                        transaction.Id,
                        errorCode);

                    var current = await ReadForDecisionAsync(
                        transactionStore, transaction.Id, logger, cancellationToken);
                    var ending = current is null ? null : TerminalTransactionOutcome.Of(current);

                    // The journal is told what refused the press: an expiry is the same occurrence on
                    // either side of the cleanup pass, so a transaction the pass promoted under the
                    // write keeps the expiry code; any other recorded decision — or one not readable
                    // at all — is a transaction that no longer accepts the event. A repeat of the very
                    // decision this press asked for is no refusal and writes nothing.
                    if (ending != pressedDecision)
                    {
                        await PublishChannelRefusalAsync(
                            eventPublisher, timeProvider, transaction.Id, adapter.ChannelType,
                            ending == TransactionOutcome.Expired
                                ? ChannelInteractionAuditCodes.LinkExpired
                                : ChannelInteractionAuditCodes.LinkNotAnswerable,
                            errorCode, logger);
                    }

                    // The answer for every case below in which there is no outcome to state — the same
                    // one the sender got before this branch learned to state any.
                    var errorAnswer = new RefusedDecisionAnswer(
                        TransactionOutcome.Failed, Localize(promptLocalizer, errorText, recipientLocale));

                    // The re-read found nothing to read an outcome off.
                    if (current is null)
                    {
                        return errorAnswer;
                    }

                    // The decision that stands is the user's confirmation, held as the claims
                    // completion mark (SPEC-041 L91): nothing has ended, and the answer is not a
                    // terminal outcome but the state of the wait, which the caller shows. The journal
                    // above already has it.
                    if (current.IsCompletingClaims())
                    {
                        logger.LogInformation(
                            "The decision was refused — the transaction waits for missing claims. TransactionId: {TransactionId}, Error: {ErrorCode}",
                            current.Id,
                            errorCode);
                        return null;
                    }

                    // Nothing has ended yet (a decision is written but not finalized), or it ended in a
                    // failure. A failure has no receipt of its own — OutcomeReceiptAddress.Kind is
                    // declared for the other three and throws on this one — so the channel's error text
                    // stays the answer for both.
                    if (ending is null || ending.Value == TransactionOutcome.Failed)
                    {
                        return errorAnswer;
                    }

                    var endedIn = ending.Value;

                    if (endedIn == TransactionOutcome.Expired)
                    {
                        return new RefusedDecisionAnswer(
                            TransactionOutcome.Expired,
                            await ExpiredReceiptAsync(
                                adapter, current, SenderSlotSource(current, sender),
                                recipientLocale, receiptText, cancellationToken));
                    }

                    // Confirmed or declined — what someone's decision put on the transaction. Telling a
                    // sender that is a disclosure, and it is made here only because both callers gated
                    // the sender before the write, with the engine's own admissibility rule: a sender
                    // the transaction does not admit never reaches this branch.
                    logger.LogInformation(
                        "Repeated decision — the sender is answered with the receipt of the recorded outcome. TransactionId: {TransactionId}, Outcome: {Outcome}",
                        current.Id,
                        endedIn.Value);

                    // A write is attempted only past the decision gate, so the question of this
                    // transaction stood before this user in this channel and the receipt may name its
                    // subject — the same reading the expiry branch above states.
                    return new RefusedDecisionAnswer(
                        endedIn,
                        await receiptText.RenderAsync(
                            ReceiptAddressOf(endedIn, adapter, current),
                            recipientLocale,
                            current.GetUiTimeZone(),
                            // The moment of the outcome is the one the decision wrote down, read off the
                            // re-read instance: the one held before the write is the stale one.
                            current.UpdatedAt,
                            SenderSlotSource(current, sender),
                            TransactionResolutionContext.For(current),
                            cancellationToken));
                }

            default:
                // Something of ours refused — the store, the identity resolver, a channel the
                // transaction does not admit. The sender is told that it did not work and nothing
                // about why; the code is here.
                logger.LogWarning(
                    "Failed to record the decision reported by the channel. TransactionId: {TransactionId}, Error: {ErrorCode}",
                    transaction.Id,
                    errorCode);

                return new RefusedDecisionAnswer(
                    TransactionOutcome.Failed, Localize(promptLocalizer, errorText, recipientLocale));
        }
    }

    /// <summary>
    /// Records the decision the channel forced (SPEC-003 CA-191) on the transaction itself: the
    /// terminal state plus the reason code, which is what both late readers — the sign-in page
    /// polling and the web callback — already know how to show.
    /// </summary>
    /// <remarks>
    /// <see cref="CancellationToken.None"/> for the same reason as the rate-limit branch: the
    /// decision is already made, and a cancelling request must not leave the transaction hanging
    /// until its TTL. A refused transition means a concurrent delivery recorded the decision first —
    /// a benign race that must not add noise to monitoring, hence a level below Warning; any other
    /// failure leaves the transaction to expire and IS worth a warning.
    /// </remarks>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="concurrencyToken">Concurrency token of the read transaction.</param>
    /// <param name="logger">Logger.</param>
    private static async Task FailForChannelCannotContinueAsync(
        TransactionId transactionId,
        ITransactionService transactionService,
        string concurrencyToken,
        ILogger logger)
    {
        var failResult = await transactionService.FailTransactionAsync(
            transactionId,
            TransactionErrorCodes.ChannelCannotContinue,
            concurrencyToken,
            CancellationToken.None);

        if (failResult.IsSuccess)
        {
            return;
        }

        if (failResult.Error.Code is TransactionErrorCodes.InvalidStateTransition)
        {
            logger.LogDebug(
                "The transaction decision is already recorded — the channel refusal changes nothing. TransactionId: {TransactionId}",
                transactionId);
            return;
        }

        logger.LogWarning(
            "Failed to move the transaction to Failed (the channel cannot continue). TransactionId: {TransactionId}, Error: {ErrorCode}",
            transactionId,
            failResult.Error.Code);
    }

    /// <summary>
    /// Records the core's own refusal to ask a question it has no wording for (SPEC-039 E28) on the
    /// transaction itself: the terminal state plus the reason code both late readers — the status
    /// poll of the relying party and the web callback — already know how to show.
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="FailForChannelCannotContinueAsync"/>, down to the treatment of a refused
    /// transition as a benign race: the decision is already made, so a cancelling request must not
    /// leave the transaction hanging until its TTL.
    /// </remarks>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="concurrencyToken">Concurrency token of the read transaction.</param>
    /// <param name="logger">Logger.</param>
    private static async Task FailForConfirmationSubjectUnavailableAsync(
        TransactionId transactionId,
        ITransactionService transactionService,
        string concurrencyToken,
        ILogger logger)
    {
        var failResult = await transactionService.FailTransactionAsync(
            transactionId,
            TransactionErrorCodes.ConfirmationTemplateUnavailable,
            concurrencyToken,
            CancellationToken.None);

        if (failResult.IsSuccess)
        {
            return;
        }

        if (failResult.Error.Code is TransactionErrorCodes.InvalidStateTransition)
        {
            logger.LogDebug(
                "The transaction decision is already recorded — the missing subject changes nothing. TransactionId: {TransactionId}",
                transactionId);
            return;
        }

        logger.LogWarning(
            "Failed to move the transaction to Failed (the subject of the confirmation is unavailable). TransactionId: {TransactionId}, Error: {ErrorCode}",
            transactionId,
            failResult.Error.Code);
    }

    /// <summary>
    /// The caller slot values an expiry receipt answered BEFORE the decision gate may be worded with:
    /// the transaction's own when its question stood before the user in this channel, null otherwise
    /// (SPEC-036 TPL-123).
    /// </summary>
    /// <remarks>
    /// The expiry pre-check answers ahead of the gate on purpose — a pressed button is always answered
    /// (SPEC-003 CA-192) — so it also answers a channel the question was never sent to (SPEC-039 E41).
    /// That intersection is resolved by the receipt staying neutral to the subject, so the values are
    /// handed over only where the channel is the one the question was asked in, and the answer comes
    /// from the same gate every decision passes. The gate is asked only when there is something to
    /// name: a sign-in transaction and a confirmation without caller values render the same text either
    /// way, and no question to the surface is spent on them. A path wiring no confirmation orchestration
    /// never asked a confirmation question at all, so it has no values to hand over either.
    /// </remarks>
    /// <param name="transaction">Transaction whose window closed.</param>
    /// <param name="adapter">Channel adapter the decision arrived through.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator (null — none is
    /// wired on this path).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The caller slot values, or null.</returns>
    private static async ValueTask<IReadOnlyDictionary<string, string>?> ShownCallerSlotValuesAsync(
        Transaction transaction,
        IChannelAdapter adapter,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        CancellationToken cancellationToken)
    {
        if (transaction.ConfirmationSnapshot?.SlotValues is not { Count: > 0 } values
            || confirmationPromptOrchestrator is null)
        {
            return null;
        }

        return await confirmationPromptOrchestrator.AcceptsChannelDecisionAsync(
                transaction, adapter, cancellationToken)
            ? values
            : null;
    }

    /// <summary>
    /// Whether a decision reported by a channel may be acted on (SPEC-039 E41). The guarantee "the
    /// subject is always shown before it is confirmed" is held by the core for EVERY channel alike,
    /// third-party ones included, and never by the discipline of an adapter's own code.
    /// </summary>
    /// <remarks>
    /// Every way a channel decides the fate of a transaction passes here — the reported confirmation,
    /// the reported decline, and the auto-confirmation a followed deep link stands for. A sign-in
    /// transaction is out of scope in every branch: nothing about its paths changes.
    /// </remarks>
    /// <param name="transaction">Transaction the decision arrived for.</param>
    /// <param name="adapter">Channel adapter the decision arrived through.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator (null — the
    /// caller wired no confirmation orchestration at all).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the decision may be acted on.</returns>
    private static async ValueTask<bool> AcceptsChannelDecisionAsync(
        Transaction transaction,
        IChannelAdapter adapter,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (confirmationPromptOrchestrator is not null)
        {
            return await confirmationPromptOrchestrator.AcceptsChannelDecisionAsync(
                transaction, adapter, cancellationToken);
        }

        // No confirmation orchestration is wired here, so this path never asked the question and has
        // no way of establishing that the subject was shown. A sign-in transaction is unaffected —
        // it is accepted, and the caller's auto-confirmation applies to it.
        if (string.Equals(transaction.Type, TransactionTypes.Confirmation, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "A decision on a confirmation arrived on a path that asks no confirmation question — the decision is dropped. ChannelType: {ChannelType}, TransactionId: {TransactionId}",
                adapter.ChannelType,
                transaction.Id.ToString());

            return false;
        }

        return true;
    }

    /// <summary>
    /// Handles authentication confirmation (an inline-keyboard button press).
    /// </summary>
    /// <param name="processor">Processor holding the services of the event's scope.</param>
    /// <param name="result">Inbound event result with confirmation data.</param>
    /// <param name="transaction">
    /// The transaction the confirmation names, read by the caller from the STORE — past the TTL gate
    /// of the service: what happens to a transaction whose time is up is decided below, and a gate
    /// that answers "expired" by withholding it would leave the receipt nothing to be worded from.
    /// Null — there is no transaction to answer with.
    /// </param>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="transactionStore">Transaction store the single re-read after a refused write goes to.</param>
    /// <param name="timeProvider">Clock the deadline of the transaction is read against.</param>
    /// <param name="identityResolutionService">Channel identity resolution service.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed user notification is counted into.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator — the point that
    /// answers whether this channel was the one asked to show the question (SPEC-039 E41).</param>
    /// <param name="receiptText">Receipt text of the transaction — a terminal outcome or the claims
    /// completion state (SPEC-036 TPL-123).</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="eventPublisher">Bus the refusals of the press are written to — a late press, a
    /// decision already recorded, a sender not admitted (SPEC-039 E48; null — no journal).</param>
    /// <param name="completionBehaviorDispatcher">Performer of the post-completion action of the
    /// sign-in this press completes (SPEC-012 §4.9).</param>
    /// <param name="errorText">Message text shown to the user on error.</param>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — no prompt reference).</param>
    /// <param name="letterKey">What identifies the letter the confirmation is — a letter is charged one
    /// attempt however many times it arrives; null — a press of a button, charged on every press.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task HandleAuthConfirmAsync(
        ChannelInboundProcessor processor,
        ChannelAuthConfirmResult result,
        Transaction? transaction,
        IChannelAdapter adapter,
        ITransactionService transactionService,
        ITransactionStore transactionStore,
        TimeProvider timeProvider,
        IIdentityResolutionService identityResolutionService,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        IOutcomeReceiptText receiptText,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ITransactionEventPublisher? eventPublisher,
        ICompletionBehaviorDispatcher completionBehaviorDispatcher,
        string errorText,
        IConfirmationPromptLocalizer? promptLocalizer,
        IChannelPromptMessageStore? promptMessageStore,
        string? letterKey,
        CancellationToken cancellationToken)
    {
        // The identifier arrives already parsed: the adapter is the one that reads it out of the
        // channel event, so an unparseable value never reaches this point (SPEC-003 §6.3).
        var transactionId = result.TransactionId;

        if (transaction is null)
        {
            logger.LogWarning(
                "No transaction to answer the AuthConfirm with. TransactionId: {TransactionId}",
                result.TransactionId);

            await ShowUnknownTransactionAsync(
                adapter, transactionId, result.Identity, result.InboundToken, receiptText,
                promptMessageStore, displayIntentSource, logger, metrics, cancellationToken);
            return;
        }

        // Recipient-locale chain (B17): channel snapshot locale → initiation-page language → base.
        var recipientLocale = result.Identity.Locale ?? transaction.GetUiLocale();

        // Only the channel subject the transaction is bound to decides it (SPEC-007 UI-022, SPEC-039
        // N34) — compared before the expiry branch and the rate limiter, like on the start.
        if (transaction.IsBoundToAnotherSubject(result.Identity))
        {
            await RefuseDecisionOfAnotherSubjectAsync(
                result.Identity, result.InboundToken, transaction, recipientLocale, adapter,
                transactionService, receiptText, displayIntentSource, promptLocalizer, errorText,
                eventPublisher, timeProvider, logger, metrics, cancellationToken);
            return;
        }

        // The window closed before the button was pressed. The answer is the receipt of an expiry —
        // the SAME one on both sides of the cleanup pass, which is the whole point of reading past
        // the gate. No decision is written down: the transaction is already over, and the engine
        // records that after the answer (see the call below the receipt). Asked before the decision
        // gate and before the rate limiter: a transaction whose time is up spends not the user's
        // budget, and asks the surface only whether its receipt may name the subject — and only when
        // there is a subject to name (SPEC-036 TPL-123).
        if (transaction.HasRunOutOfTime(timeProvider.GetUtcNow()))
        {
            // The transaction was found, so the late press reaches the journal (SPEC-039 E48). No
            // engine code went with it — the deadline or the state itself is the reason.
            await PublishChannelRefusalAsync(
                eventPublisher, timeProvider, transactionId, adapter.ChannelType,
                ChannelInteractionAuditCodes.LinkExpired, refusalErrorCode: null, logger);

            await ShowOutcomeAsync(
                adapter, transactionId, result.Identity.ChannelUserId, result.InboundToken,
                TransactionOutcome.Expired,
                await ExpiredReceiptAsync(
                    adapter,
                    transaction,
                    // The gate of the caller values cuts off what the calling party supplied and
                    // nothing the core states about the transaction.
                    SenderSlotSource(transaction, result.Identity) with
                    {
                        CallerSlotValues = await ShownCallerSlotValuesAsync(
                            transaction, adapter, confirmationPromptOrchestrator, cancellationToken)
                    },
                    recipientLocale,
                    receiptText,
                    cancellationToken),
                promptMessageStore,
                displayIntentSource,
                // The context the expiry helper addresses its wording with — the transaction in hand.
                TransactionResolutionContext.For(transaction),
                logger, metrics, cancellationToken);

            await ExpireAfterLateDecisionAsync(transactionService, transactionId, cancellationToken);
            return;
        }

        TagCorrelatingTrace(transaction);

        // A confirmation is only answered by the channel its subject was shown in (SPEC-039 E41).
        // Nothing is changed and nothing is sent back: the channel was never asked, so it gets no
        // reply either.
        if (!await AcceptsChannelDecisionAsync(
                transaction, adapter, confirmationPromptOrchestrator, logger, cancellationToken))
        {
            return;
        }

        // A transaction that has already answered is finalized SUCCESSFULLY by the step below: the
        // idempotent branch of the write returns the recorded state without asking whether this sender
        // may act on the transaction at all, and the receipt built afterwards states that recorded
        // outcome — a disclosure. The limiter below, when the budget is spent, fails the transaction —
        // an action on it. So the gate every write of this transaction passes is applied here,
        // explicitly, before either. It is applied whatever state was read: the transaction may be
        // decided elsewhere between the read above and the write below, and a gate keyed on the state
        // read would miss exactly that case. What it checks — the allow-list and the application the
        // bot policy resolves for — does not change over the life of a transaction, so the instance
        // read above answers it. A sender the gate refuses gets the same answer the write itself would
        // have produced for them; a pressed button is answered regardless (SPEC-003 CA-192).
        if (!await IsSenderAdmittedAsync(
                transaction, result.Identity, adapter, transactionService, eventPublisher,
                timeProvider, logger, cancellationToken))
        {
            await ShowOutcomeAsync(
                adapter, transactionId, result.Identity.ChannelUserId, result.InboundToken,
                TransactionOutcome.Failed,
                Localize(promptLocalizer, errorText, recipientLocale),
                promptMessageStore,
                displayIntentSource,
                TransactionResolutionContext.For(transaction),
                logger, metrics, cancellationToken);
            return;
        }

        // The user has already confirmed and the transaction waits for missing claims (SPEC-041
        // L91): the completion mark froze the snapshot, so the press changes nothing. It is recorded,
        // and on a channel that updates its messages it is answered with the state of the wait — the
        // press gets its answer and the question message reads "confirmed, one step left"; a channel
        // that does not is sent the request of the step again instead (SPEC-003 CA-192). Before the rate limiter, whose refusal
        // would fail the transaction the user is completing.
        if (transaction.IsCompletingClaims())
        {
            await RecordClaimCompletionRefusalAsync(
                transactionId, adapter, refusalErrorCode: null, eventPublisher, timeProvider, logger);

            await AnswerRepeatedPressInTheCompletionAsync(
                processor, adapter, transaction, transaction.UpdatedAt, result.Identity,
                result.InboundToken, recipientLocale, receiptText, promptMessageStore,
                displayIntentSource, logger, metrics, cancellationToken);
            return;
        }

        // Per-user rate limiting (SPEC-002): check BEFORE ConfirmTransactionAsync. A letter is charged once:
        // the same letter arriving again goes on on the outcome of its first pass, without a second attempt.
        var budget = await TakeConfirmBudgetOnceAsync(
            processor, transaction, result.Identity, adapter, letterKey, cancellationToken);

        // Another pass of the same letter holds the budget and has not been let through: this one writes
        // nothing and sends nothing back — the letter's own route reads the transaction for its answer.
        if (budget is LetterBudgetOutcome.InProgress)
        {
            return;
        }

        if (budget is LetterBudgetOutcome.Exhausted)
        {
            // The transaction was moved to Failed, so the user is shown the error outcome — with
            // the wording of an exhausted budget rather than the channel's generic error: what
            // to do next after this refusal is to wait, and telling them to start again would
            // spend the budget they have just run out of.
            await ShowOutcomeAsync(
                adapter, transactionId, result.Identity.ChannelUserId, result.InboundToken,
                TransactionOutcome.Failed,
                Localize(promptLocalizer, MessageTemplateNaturalKeys.OutcomeTooManyAttempts, recipientLocale),
                promptMessageStore,
                displayIntentSource,
                TransactionResolutionContext.For(transaction),
                logger, metrics, cancellationToken);
            return;
        }

        // Whether the sign-in was completed BEFORE this event is taken as a value here: a transaction
        // store is free to hand out the very instance it keeps, and the finalization below would then
        // move this one to Completed as well, leaving nothing to tell the two cases apart.
        var wasCompleted = transaction.State is TransactionState.Completed;

        // The confirmation may begin a step of the claims completion, whose starter hands its message
        // to the background delivery before this press is answered. The gate holds that delivery
        // until the handler is left — by any branch below — so the message of the step follows the
        // answer in the chat instead of racing it (SPEC-041 L91).
        using var pressReplyGate = ChannelPressReplyGate.Open();

        // Confirm and complete the transaction
        var finalizeResult = await ChannelTransactionFinalizer.ConfirmAndCompleteAsync(
            result.Identity, transactionId, transactionService,
            identityResolutionService, transaction.ConcurrencyToken, logger, cancellationToken);

        if (finalizeResult.IsFailure)
        {
            // Another subject entered a transaction nobody was bound to, between the read above and this
            // write: the answer of the subject gate, reached through the race.
            if (finalizeResult.Error.Code is TransactionErrorCodes.ChannelSubjectMismatch)
            {
                await RecordSubjectMismatchAsync(transactionId, adapter, eventPublisher, timeProvider, logger);

                await ShowUnknownTransactionAsync(
                    adapter, transactionId, result.Identity, result.InboundToken, receiptText,
                    promptMessageStore: null, displayIntentSource, logger, metrics, cancellationToken);
                return;
            }

            // A refusal is read by its class, not reduced to a flag: the code is right here, and an
            // expiry that slipped through between the read and the write is the same occurrence the
            // pre-check answers, so it gets the same answer.
            var refusal = await AnswerToRefusedDecisionAsync(
                finalizeResult.Error.Code, TransactionOutcome.Confirmed, transaction, result.Identity, adapter,
                transactionStore, eventPublisher, timeProvider, recipientLocale, receiptText,
                promptLocalizer, errorText, logger, cancellationToken);

            // The transaction waits for missing claims — the pre-check's answer, reached through the
            // race, and given under the same condition; the record is already written.
            if (refusal is null)
            {
                await AnswerRepeatedPressInTheCompletionAsync(
                    processor, adapter, transaction, transaction.UpdatedAt, result.Identity,
                    result.InboundToken, recipientLocale, receiptText, promptMessageStore,
                    displayIntentSource, logger, metrics, cancellationToken);
                return;
            }

            await ShowOutcomeAsync(
                adapter, transactionId, result.Identity.ChannelUserId, result.InboundToken,
                refusal.Value.Outcome, refusal.Value.Text,
                promptMessageStore,
                displayIntentSource,
                TransactionResolutionContext.For(transaction),
                logger, metrics, cancellationToken);
            return;
        }

        // The confirmation left the transaction waiting for missing claims (SPEC-041 L91): the sign-in
        // is not over, so there is no terminal receipt yet. The press is answered with the state of
        // the wait, and the question message loses its buttons; the reference to it stays in the
        // store. What else the user sees in the chat is the business of the step starter, which has
        // already run.
        if (finalizeResult.Value.IsCompletingClaims())
        {
            await ShowClaimCompletionStateAsync(
                adapter, transaction,
                // The moment the completion was entered is the one the confirmation wrote down, read
                // off the instance the finalizer returned: the one held before the call is stale.
                finalizeResult.Value.UpdatedAt,
                result.Identity, result.InboundToken, recipientLocale, receiptText,
                promptMessageStore, displayIntentSource, logger, metrics, cancellationToken);
            return;
        }

        // Show the user the terminal outcome: the adapter picks the platform operations. A confirmed
        // outcome is a RECEIPT and comes back already localized.
        await ShowOutcomeAsync(
            adapter, transactionId, result.Identity.ChannelUserId, result.InboundToken,
            TransactionOutcome.Confirmed,
            await receiptText.RenderAsync(
                ReceiptAddressOf(TransactionOutcome.Confirmed, adapter, transaction),
                recipientLocale,
                transaction.GetUiTimeZone(),
                // The moment of the outcome is the one the completion wrote down, read off the
                // instance the finalizer returned: the one held before the call is the stale one.
                finalizeResult.Value.UpdatedAt,
                // Past the decision gate: the question stood before this user in this channel.
                SenderSlotSource(transaction, result.Identity),
                TransactionResolutionContext.For(transaction),
                cancellationToken),
            promptMessageStore,
            displayIntentSource,
            TransactionResolutionContext.For(transaction),
            logger, metrics, cancellationToken);

        // The post-completion action follows the receipt and never replaces it (SPEC-012 §4.9). A
        // repeated press on a transaction that is already completed passes the finalization on its
        // idempotent branch, so the action is performed only by the press that completed it; it is
        // handed the instance the finalizer returned — the one carrying the resolved identity.
        if (!wasCompleted)
        {
            await completionBehaviorDispatcher.DispatchAsync(finalizeResult.Value, cancellationToken);
        }
    }

    /// <summary>
    /// Attaches the channel identity to the still-Pending transaction so the browser waiting on the
    /// sign-in screen can be taken to the core's confirmation question. The transaction is not
    /// completed — the user answers on the web page; the chat gets one notice sending the sender
    /// there, and no question.
    /// </summary>
    /// <remarks>
    /// A failure is never turned into an auto-confirmation: that would silently replace the chosen
    /// confirmation surface with a silent sign-in. The transaction is left Pending and expires by TTL.
    /// What the sender is told about a refusal is read off its class (SPEC-003 CA-200): a closed
    /// window is answered by the receipt of an expiry, a decision already recorded by the one neutral
    /// text the registration declares. Silence is left for the single case it was always meant for —
    /// a sender the transaction does not admit must not be able to make the core reply. A refused
    /// attach sends no notice of the hand-over, so such a reply is the first message the sender gets
    /// rather than a second copy of an answer already given.
    /// The reply goes out WITHOUT taking the rate limit: the budget of this event was already spent
    /// by the limiter block of the caller, and taking it twice would charge one event two attempts.
    /// </remarks>
    /// <param name="identity">Channel identity snapshot of the user who started the sign-in.</param>
    /// <param name="transaction">Transaction as it was read before the attach.</param>
    /// <param name="adapter">Channel adapter the event arrived through.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="timeProvider">Clock the moment of the audit event is read off.</param>
    /// <param name="recipientLocale">Recipient locale of the receipt, which the transaction is
    /// allowed to improve (null — base language); the neutral text is localized off the snapshot
    /// alone, so that it reads the same as the one a missing transaction gets.</param>
    /// <param name="staleLinkReplyText">Declared reply to an unservable sign-in link (null — silence).</param>
    /// <param name="receiptText">Port the terminal wording comes from.</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="webConfirmationNotice">Sender of the notice of the hand-over to the core page.</param>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="eventPublisher">Bus the refusal is written to (null — no journal).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics the unserved event is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task AttachIdentityForWebConfirmationAsync(
        ChannelIdentitySnapshot identity,
        Transaction transaction,
        IChannelAdapter adapter,
        ITransactionService transactionService,
        TimeProvider timeProvider,
        string? recipientLocale,
        string? staleLinkReplyText,
        IOutcomeReceiptText receiptText,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        WebConfirmationNoticeSender webConfirmationNotice,
        IConfirmationPromptLocalizer? promptLocalizer,
        ITransactionEventPublisher? eventPublisher,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
    {
        var transactionId = transaction.Id;

        // Whether the question had already been handed over to the core page BEFORE this event — a
        // repeated start of the same sender, or the provider delivering the event again — is taken as a
        // value here: a transaction store is free to hand out the very instance it keeps, and the attach
        // below would then make this one read "awaiting" as well. One notice per sign-in, never two.
        var handedOverBefore = transaction.IsAwaitingWebConfirmation();

        var attachResult = await transactionService.AttachChannelIdentityAsync(
            transactionId,
            identity,
            transaction.ConcurrencyToken,
            cancellationToken: cancellationToken);

        if (attachResult.IsSuccess)
        {
            logger.LogInformation(
                "Channel identity accepted; the confirmation question is asked on the core web page. " +
                "TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                transactionId,
                identity.ChannelType);

            // The sender has just written to the bot and would otherwise wait for an answer in the chat
            // that never comes: the chat is told once to look at the page that asks (SPEC-012 CFG-266).
            // A sign-in and a confirmation are told alike; the template names the action by the type
            // of the transaction.
            if (!handedOverBefore)
            {
                await webConfirmationNotice.SendAsync(
                    transaction, adapter, identity.ChannelUserId, recipientLocale, cancellationToken);
            }

            return;
        }

        var refusalCode = attachResult.Error.Code;

        // The event named a transaction that was found and was not served, whichever way it refused:
        // the branch is counted before it is answered.
        metrics.RecordUnservedStart(adapter.ChannelType);

        switch (await RecordChannelRefusalAsync(
                    RefusedIdentityAttach, refusalCode, transactionId, adapter, eventPublisher, timeProvider,
                    logger))
        {
            case DecisionRefusalClass.Expired:
                // The transaction is in hand, so the expiry is worded for it in full — the same
                // receipt the same occurrence gets on every other surface.
                await ShowOutcomeAsync(
                    adapter, transactionId, identity.ChannelUserId, inboundToken: null,
                    TransactionOutcome.Expired,
                    await ExpiredReceiptAsync(
                        adapter,
                        transaction,
                        // The question of this surface is asked on the core page only AFTER the
                        // identity is attached, and it was not: nothing of the transaction was shown to
                        // this user yet, so its receipt names no subject (SPEC-036 TPL-123).
                        SenderSlotSource(transaction, identity).WithoutCallerValues(),
                        recipientLocale,
                        receiptText,
                        cancellationToken),
                    // No prompt was sent into this channel on this surface — the question lives on
                    // the core page. The record of the chat the start may have written is not
                    // consumed here: it stays for the background expiry path, which then reports
                    // the expiry into the chat once more.
                    promptMessageStore: null,
                    displayIntentSource,
                    // The context the expiry helper addresses its wording with — the transaction in hand.
                    TransactionResolutionContext.For(transaction),
                    logger, metrics, cancellationToken);
                return;

            case DecisionRefusalClass.Race:
                // What became of the transaction is not disclosed (SPEC-039 C15): the sender gets the
                // same neutral text a missing transaction gets, down to the language it is written
                // in — the snapshot locale and then the base one, never the language the transaction
                // knows, or the answer would read differently depending on what the sender is not
                // being told. The reason stays in the journal.
                await SendDeclaredReplyAsync(
                    adapter, identity, staleLinkReplyText, identity.Locale, promptLocalizer, logger,
                    metrics, cancellationToken);
                return;

            default:
                // The transaction refused the sender, or something of ours refused: either way the
                // sender is told nothing at all. A sender a transaction does not admit must not be
                // able to make the core send it a message.
                return;
        }
    }

    /// <summary>
    /// Attaches the mailbox a sign-in letter proved to the still-Pending sign-in, leaving the decision to
    /// the sign-in window: on an effective confirmation surface other than
    /// <see cref="ConfirmationSurface.None"/> the window asks the one question of the sign-in (SPEC-012
    /// §4.4.2), so the letter does not complete it. The one path of both ways a letter answers a sign-in —
    /// the "Continue signing in" of the Pull mode and the inbound mail of the Push mode.
    /// </summary>
    /// <remarks>
    /// A confirmation transaction is refused before any check: the letter of a confirmation does not take
    /// this path — its Pull letter binds the answer to its browser, its Push mail calls
    /// <see cref="AttachLetterIdentityAsync"/> directly. The rest of this path is that method.
    /// </remarks>
    /// <param name="processor">Processor holding the services of the scope of the letter.</param>
    /// <param name="transaction">The sign-in as the caller read it.</param>
    /// <param name="identity">Identity snapshot of the mailbox the letter proved.</param>
    /// <param name="adapter">The Email channel adapter.</param>
    /// <param name="letterKey">What identifies the letter (<see cref="RefuseLetterAttachAsync"/>); not empty.</param>
    /// <param name="cancellationToken">Cancels the reads before the write; the write itself is not
    /// interrupted by it.</param>
    /// <returns>The transaction with the mailbox attached; a failure — the code of the refusal, already
    /// logged and journaled.</returns>
    internal static async Task<Result<Transaction>> AttachSignInLetterIdentityAsync(
        ChannelInboundProcessor processor,
        Transaction transaction,
        ChannelIdentitySnapshot identity,
        IChannelAdapter adapter,
        string letterKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrEmpty(letterKey);

        // A confirmation is answered on the core page (SPEC-039 E29), not by the sign-in window this path
        // hands the letter to: a confirmation is refused here whichever caller routed it.
        if (string.Equals(transaction.Type, TransactionTypes.Confirmation, StringComparison.Ordinal))
        {
            await PublishChannelRefusalAsync(
                processor.EventPublisher, processor.TimeProvider, transaction.Id, adapter.ChannelType,
                ChannelInteractionAuditCodes.LinkNotAnswerable, refusalErrorCode: null, processor.Logger);

            return Result<Transaction>.Failure(TransactionErrorCodes.InvalidStateTransition, LetterNotAttached);
        }

        return await AttachLetterIdentityAsync(processor, transaction, identity, adapter, letterKey, cancellationToken);
    }

    /// <summary>
    /// Attaches the mailbox a letter proved to the still-Pending transaction without binding the answer to a
    /// browser: the answer is given by the sign-in window, or on the core page of a confirmation. The path of
    /// the letter of a sign-in (<see cref="AttachSignInLetterIdentityAsync"/>) and of the inbound mail of a
    /// confirmation in the Push mode.
    /// </summary>
    /// <remarks>
    /// The letter passes <see cref="RefuseLetterAttachAsync"/> first. A second letter of the same mailbox
    /// attaches the same subject again; a write lost to a parallel one is read once more and is a success
    /// when the transaction already waits for the web answer with this subject.
    /// </remarks>
    /// <param name="processor">Processor holding the services of the scope of the letter.</param>
    /// <param name="transaction">The transaction as the caller read it.</param>
    /// <param name="identity">Identity snapshot of the mailbox the letter proved.</param>
    /// <param name="adapter">The Email channel adapter.</param>
    /// <param name="letterKey">What identifies the letter (<see cref="RefuseLetterAttachAsync"/>); not empty.</param>
    /// <param name="cancellationToken">Cancels the reads before the write; the write itself is not
    /// interrupted by it.</param>
    /// <returns>The transaction with the mailbox attached; a failure — the code of the refusal, already
    /// logged and journaled.</returns>
    internal static async Task<Result<Transaction>> AttachLetterIdentityAsync(
        ChannelInboundProcessor processor,
        Transaction transaction,
        ChannelIdentitySnapshot identity,
        IChannelAdapter adapter,
        string letterKey,
        CancellationToken cancellationToken)
    {
        if (await RefuseLetterAttachAsync(processor, transaction, identity, adapter, letterKey, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var transactionService = processor.TransactionService;
        var transactionId = transaction.Id;

        // CancellationToken.None: the mailbox is proved, and the web answer must have somebody to sign even
        // if the request of the letter goes away.
        var attachResult = await transactionService.AttachChannelIdentityAsync(
            transactionId,
            identity,
            transaction.ConcurrencyToken,
            bindAnswerToBrowser: false,
            cancellationToken: CancellationToken.None);

        if (attachResult.IsSuccess)
        {
            processor.Logger.LogInformation(
                "Mailbox of the letter attached; the transaction is decided on the web. " +
                "TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                transactionId,
                identity.ChannelType);
            return attachResult;
        }

        var refusalCode = attachResult.Error.Code;

        if (refusalCode is TransactionErrorCodes.ConcurrencyConflict)
        {
            // The write was lost to a parallel one: when that one left the transaction waiting for the web
            // answer with this same subject — a second letter of the same mailbox — this letter has done its
            // work.
            var rereadResult = await transactionService.GetTransactionAsync(transactionId, CancellationToken.None);
            if (rereadResult.IsSuccess
                && rereadResult.Value.IsAwaitingWebConfirmation()
                && !rereadResult.Value.IsBoundToAnotherSubject(identity))
            {
                return rereadResult;
            }
        }

        await RecordLetterRefusalAsync(processor, transactionId, refusalCode, adapter);

        return attachResult;
    }

    /// <summary>
    /// Gates the letter that is about to attach its mailbox to a transaction, in the order of
    /// <see cref="HandleAuthConfirmAsync"/>: the transaction bound to another subject, out of time, no longer
    /// Pending, a sender it does not admit, in the claims completion — and then the budget of the sender. The
    /// one gate of the letter of a sign-in and the letter of a confirmation, Pull and Push alike.
    /// </summary>
    /// <remarks>
    /// No check before the rate limiter writes onto the transaction or takes the budget: neither a sender of
    /// another subject nor a letter on a transaction out of time or in the claims completion can have that
    /// transaction failed by a spent budget (SPEC-039 N34). Each refusal reaches the journal under its class
    /// (SPEC-039 E48) and is handed back by the code the attach itself would refuse with; what the sender is
    /// answered is the caller's business. The budget is the one of the confirmation operation
    /// (<see cref="UserAuthRateLimitOperations.AuthConfirm"/>), and an exhausted one fails the transaction,
    /// as it does on the press of a button. One letter costs one attempt, however many times it passes the
    /// gate — two parallel sendings of the page of one link, a redelivery of the same mail, a retry of a
    /// mail its source was asked to hand in again (<see cref="TakeConfirmBudgetOnceAsync"/>); two letters
    /// are two attempts. A pass that finds another pass of the same letter holding the budget, not yet let
    /// through by it, is refused as a write lost to a parallel one (<see cref="TransactionErrorCodes.ConcurrencyConflict"/>):
    /// it writes nothing onto the transaction, and the caller answers it as such a lost write — a link with the
    /// error page, a mail of Push with a retry unless the caller's own read of the transaction finds it ended.
    /// </remarks>
    /// <param name="processor">Processor holding the services of the scope of the letter.</param>
    /// <param name="transaction">The transaction as the caller read it.</param>
    /// <param name="identity">Identity snapshot of the mailbox the letter proved.</param>
    /// <param name="adapter">The Email channel adapter.</param>
    /// <param name="letterKey">What identifies the letter within its channel — the token of its link, the
    /// identifier of its mail; not empty.</param>
    /// <param name="cancellationToken">Cancels the reads of the gate; the write of an exhausted budget is not
    /// interrupted by it.</param>
    /// <returns>The refusal, already logged and journaled; null — the letter may attach its mailbox.</returns>
    internal static async Task<Result<Transaction>?> RefuseLetterAttachAsync(
        ChannelInboundProcessor processor,
        Transaction transaction,
        ChannelIdentitySnapshot identity,
        IChannelAdapter adapter,
        string letterKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentException.ThrowIfNullOrEmpty(letterKey);

        var transactionService = processor.TransactionService;
        var timeProvider = processor.TimeProvider;
        var eventPublisher = processor.EventPublisher;
        var logger = processor.Logger;
        var transactionId = transaction.Id;

        if (transaction.IsBoundToAnotherSubject(identity))
        {
            // A sender the transaction does not admit is journaled as such, whoever it is bound to.
            if (await SenderAdmissionRefusalAsync(
                    transaction, identity, adapter, transactionService, eventPublisher, timeProvider, logger,
                    cancellationToken) is null)
            {
                await RecordSubjectMismatchAsync(transactionId, adapter, eventPublisher, timeProvider, logger);
            }

            return Result<Transaction>.Failure(TransactionErrorCodes.ChannelSubjectMismatch, LetterNotAttached);
        }

        if (await RefuseLetterOutOfTimeAsync(processor, transaction, adapter) is { } outOfTime)
        {
            return outOfTime;
        }

        if (transaction.State is not TransactionState.Pending)
        {
            await PublishChannelRefusalAsync(
                eventPublisher, timeProvider, transactionId, adapter.ChannelType,
                ChannelInteractionAuditCodes.LinkNotAnswerable, refusalErrorCode: null, logger);

            return Result<Transaction>.Failure(TransactionErrorCodes.InvalidStateTransition, LetterNotAttached);
        }

        if (await SenderAdmissionRefusalAsync(
                transaction, identity, adapter, transactionService, eventPublisher, timeProvider, logger,
                cancellationToken) is { } admissionRefusal)
        {
            return Result<Transaction>.Failure(admissionRefusal, LetterNotAttached);
        }

        // The snapshot of a transaction in the claims completion is frozen (SPEC-041 L91): the letter changes
        // nothing, and the budget stays untouched, whose refusal would fail the transaction being completed.
        if (transaction.IsCompletingClaims())
        {
            await RecordClaimCompletionRefusalAsync(
                transactionId, adapter, refusalErrorCode: null, eventPublisher, timeProvider, logger);

            return Result<Transaction>.Failure(TransactionErrorCodes.ClaimCompletionInProgress, LetterNotAttached);
        }

        return await TakeConfirmBudgetOnceAsync(processor, transaction, identity, adapter, letterKey, cancellationToken) switch
        {
            LetterBudgetOutcome.Exhausted =>
                Result<Transaction>.Failure(TransactionErrorCodes.UserRateLimitExceeded, LetterNotAttached),

            // Answered as a write lost to a parallel one — the other pass of the letter is that write.
            LetterBudgetOutcome.InProgress =>
                Result<Transaction>.Failure(TransactionErrorCodes.ConcurrencyConflict, LetterNotAttached),

            _ => null
        };
    }

    /// <summary>
    /// Takes the sender's attempt of the budget of the confirmation operation for one pass of an event through
    /// the gate — asked past every check of the gate, so that an event refused earlier spends nothing and
    /// leaves no mark. A letter is charged once for its transaction however many times it passes: its first
    /// pass claims the budget, takes it and, when the limiter lets it through, marks the letter allowed; a
    /// later pass goes on on that mark without the budget, and a pass that finds the claim without the mark —
    /// the first pass still in progress, or refused by the budget — takes nothing and writes nothing onto the
    /// transaction; the journal is told what the transaction has become by then.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A press of a button (no letter key), a composition without the marks, or one without a rate limiter
    /// takes the budget on every pass. A mark that cannot be claimed or read — its store failed — leaves the
    /// pass charged: an extra attempt costs less than a limit left unenforced. A pass so let through still
    /// marks the letter allowed: the store may have written the claim before it failed, and a claim without
    /// the mark would answer every later pass of the letter as in progress.
    /// </para>
    /// <para>
    /// Once the budget is claimed, the pass runs to its outcome uninterrupted. A pass that still does not reach
    /// the mark — the limiter or the store of the mark failed after the claim — leaves the claim without an
    /// outcome, and the later passes of that letter are answered as in progress until the transaction ends:
    /// the letter is refused rather than let through unlimited.
    /// </para>
    /// </remarks>
    /// <param name="processor">Processor holding the services of the scope of the event.</param>
    /// <param name="transaction">The transaction the event answers.</param>
    /// <param name="identity">Identity snapshot of the sender, whose budget is charged.</param>
    /// <param name="adapter">Adapter of the channel of the event — the journal of a pass in progress.</param>
    /// <param name="letterKey">What identifies the letter; null — the event is no letter.</param>
    /// <param name="cancellationToken">Cancels the gate up to the claim; the pass that claimed is not
    /// interrupted by it.</param>
    /// <returns>The answer of the budget to this pass.</returns>
    private static async Task<LetterBudgetOutcome> TakeConfirmBudgetOnceAsync(
        ChannelInboundProcessor processor,
        Transaction transaction,
        ChannelIdentitySnapshot identity,
        IChannelAdapter adapter,
        string? letterKey,
        CancellationToken cancellationToken)
    {
        if (processor.UserAuthRateLimiter is null
            || string.IsNullOrEmpty(letterKey)
            || processor.LetterBudgetChargeMark is not { } chargeMark)
        {
            return await TakeBudgetAsync(cancellationToken);
        }

        bool claimed;
        try
        {
            claimed = await chargeMark.TryClaimChargeAsync(transaction.Id, letterKey, cancellationToken);

            if (!claimed && await chargeMark.IsAllowedAsync(transaction.Id, letterKey, cancellationToken))
            {
                return LetterBudgetOutcome.Allowed;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            processor.Logger.LogWarning(
                exception,
                "The budget mark of the letter could not be read or set; the letter is charged. TransactionId: {TransactionId}",
                transaction.Id);

            return await ChargeAsync(chargeMark, letterKey, cancellationToken);
        }

        if (!claimed)
        {
            processor.Logger.LogInformation(
                "Another pass of the same letter holds the budget and has not been let through; this pass writes nothing. TransactionId: {TransactionId}",
                transaction.Id);

            // What the transaction has become tells the journal what refused this pass (SPEC-039 E48), as for a
            // decision recorded in parallel: the other pass ended in the confirmation this letter asks for — no
            // refusal, nothing is written; the transaction ended otherwise — the class of its end; it is still
            // under way, or cannot be read — the parallel write this pass stands for.
            var current = await ReadForDecisionAsync(
                processor.TransactionStore, transaction.Id, processor.Logger, cancellationToken);
            var ending = current is null ? null : TerminalTransactionOutcome.Of(current);

            if (ending != TransactionOutcome.Confirmed)
            {
                await PublishChannelRefusalAsync(
                    processor.EventPublisher, processor.TimeProvider, transaction.Id, adapter.ChannelType,
                    ending == TransactionOutcome.Expired
                        ? ChannelInteractionAuditCodes.LinkExpired
                        : ChannelInteractionAuditCodes.LinkNotAnswerable,
                    ending is null ? TransactionErrorCodes.ConcurrencyConflict : null,
                    processor.Logger);
            }

            return LetterBudgetOutcome.InProgress;
        }

        // CancellationToken.None from here on: a claim left without its outcome would refuse the letter.
        return await ChargeAsync(chargeMark, letterKey, CancellationToken.None);

        // Takes the budget and, when the limiter lets the letter through, marks it allowed — the mark is set
        // whatever the token, since a claim left without it answers the later passes as in progress.
        async Task<LetterBudgetOutcome> ChargeAsync(ILetterBudgetChargeMark mark, string key, CancellationToken token)
        {
            if (await TakeBudgetAsync(token) is LetterBudgetOutcome.Exhausted)
            {
                return LetterBudgetOutcome.Exhausted;
            }

            try
            {
                await mark.MarkAllowedAsync(transaction.Id, key, CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                processor.Logger.LogWarning(
                    exception,
                    "The letter was let through, but its budget mark could not be set; a later pass of it may be answered as in progress. TransactionId: {TransactionId}",
                    transaction.Id);
            }

            return LetterBudgetOutcome.Allowed;
        }

        async Task<LetterBudgetOutcome> TakeBudgetAsync(CancellationToken token) =>
            await TryTakeAuthConfirmBudgetAsync(
                processor.UserAuthRateLimiter, processor.TransactionService, transaction, identity,
                processor.Logger, token)
                ? LetterBudgetOutcome.Allowed
                : LetterBudgetOutcome.Exhausted;
    }

    /// <summary>
    /// Refuses a letter whose transaction has run out of time, before anything is built for the letter —
    /// the identity of the mailbox and the channel credentials it reads — and journals it as an expired
    /// link (SPEC-039 E48). The one check of a run-out transaction for the letter of a sign-in and the
    /// letter of a confirmation alike: what the letter is answered with stays with the caller.
    /// </summary>
    /// <param name="processor">Processor holding the services of the scope of the letter.</param>
    /// <param name="transaction">The transaction the letter names, as the caller read it.</param>
    /// <param name="adapter">The channel adapter of the letter.</param>
    /// <returns>The refusal, already journaled; null — the transaction has time left.</returns>
    internal static async Task<Result<Transaction>?> RefuseLetterOutOfTimeAsync(
        ChannelInboundProcessor processor,
        Transaction transaction,
        IChannelAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(adapter);

        if (!transaction.HasRunOutOfTime(processor.TimeProvider.GetUtcNow()))
        {
            return null;
        }

        await PublishChannelRefusalAsync(
            processor.EventPublisher, processor.TimeProvider, transaction.Id, adapter.ChannelType,
            ChannelInteractionAuditCodes.LinkExpired, refusalErrorCode: null, processor.Logger);

        return Result<Transaction>.Failure(TransactionErrorCodes.TransactionExpired, LetterNotAttached);
    }

    /// <summary>
    /// Logs and journals the refused attach of the mailbox a letter proved, under the class of the code the
    /// engine refused it with (SPEC-039 E48): a transaction past its deadline — an expired link; a decision
    /// already recorded or a parallel write — a link the transaction no longer answers; a mailbox other
    /// than the bound subject or a sender the transaction does not admit — a sender not admitted. A
    /// transaction the store no longer holds is logged and not journaled, as on every entry of the channel
    /// (<see cref="RecordChannelRefusalAsync"/>). The one reading of such a refusal for the letter of a
    /// sign-in and the letter of a confirmation alike.
    /// </summary>
    /// <param name="processor">Processor holding the services of the scope of the letter.</param>
    /// <param name="transactionId">Transaction the letter names.</param>
    /// <param name="refusalCode">Code the engine refused the attach with.</param>
    /// <param name="adapter">The channel adapter of the letter.</param>
    /// <returns>A task completed once the refusal is journaled.</returns>
    internal static async Task RecordLetterRefusalAsync(
        ChannelInboundProcessor processor,
        TransactionId transactionId,
        string refusalCode,
        IChannelAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(adapter);

        if (refusalCode is TransactionErrorCodes.ChannelSubjectMismatch)
        {
            await RecordSubjectMismatchAsync(
                transactionId, adapter, processor.EventPublisher, processor.TimeProvider, processor.Logger);
        }
        else
        {
            _ = await RecordChannelRefusalAsync(
                RefusedIdentityAttach, refusalCode, transactionId, adapter, processor.EventPublisher,
                processor.TimeProvider, processor.Logger);
        }
    }

    /// <summary>
    /// Takes one attempt of the sender's budget of the confirmation operation (SPEC-002) — the press of a
    /// button and the letter of a sign-in or of a confirmation alike — and fails the transaction when the
    /// budget is exhausted.
    /// </summary>
    /// <param name="userAuthRateLimiter">Per-user rate limiter (null — no limit).</param>
    /// <param name="transactionService">Transaction service the exhausted budget fails the transaction through.</param>
    /// <param name="transaction">The transaction the event answers.</param>
    /// <param name="identity">Identity snapshot of the sender, whose budget is charged.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancels the budget check; the write of Failed is not interrupted by it.</param>
    /// <returns>true — the event may go on; false — the budget is exhausted and the transaction is moved to
    /// Failed (a lost write is logged).</returns>
    private static async Task<bool> TryTakeAuthConfirmBudgetAsync(
        IUserAuthRateLimiter? userAuthRateLimiter,
        ITransactionService transactionService,
        Transaction transaction,
        ChannelIdentitySnapshot identity,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (userAuthRateLimiter is null)
        {
            return true;
        }

        // The partition is scoped to the confirmation operation (see UserAuthRateLimitOperations).
        var userKey = NormalizeUserKey(identity);
        var rateLimitResult = await userAuthRateLimiter.TryAcquireAsync(
            UserAuthRateLimitOperations.AuthConfirm + userKey, cancellationToken);

        if (rateLimitResult.IsAllowed)
        {
            return true;
        }

        logger.LogWarning(
            "Per-user rate limit exceeded. TransactionId: {TransactionId}, UserKeyHash: {UserKeyHash}",
            transaction.Id,
            LogMasking.Fingerprint(userKey));

        var failRlResult = await transactionService.FailTransactionAsync(
            transaction.Id,
            TransactionErrorCodes.UserRateLimitExceeded,
            transaction.ConcurrencyToken,
            CancellationToken.None);

        if (failRlResult.IsFailure)
        {
            logger.LogWarning(
                "Failed to move the transaction to Failed (rate limit). TransactionId: {TransactionId}, Error: {ErrorCode}",
                transaction.Id,
                failRlResult.Error.Code);
        }

        return false;
    }

    /// <summary>
    /// Answers a sign-in start the core could not serve with the text the registration declares for
    /// it, spending one attempt of the sender's budget on the way (SPEC-003 CA-200).
    /// </summary>
    /// <remarks>
    /// The single place that both checks the declaration and takes the limit, so every branch that
    /// ends in "this start cannot be served" charges the event exactly once and stays silent under
    /// exactly the same condition. A channel that declares no text costs the budget nothing: there is
    /// nothing to send, so nothing is taken.
    /// A refused limit is not written onto the transaction: there may be none, and one that is there
    /// is either already terminal — nothing is left to fail — or waiting for missing claims (SPEC-041
    /// L91), whose sign-in the user is completing and a spent budget of a stale link must not end.
    /// </remarks>
    /// <param name="adapter">Channel adapter the reply travels through.</param>
    /// <param name="identity">Identity snapshot of the sender.</param>
    /// <param name="replyText">Declared reply text (null or blank — the channel stays silent).</param>
    /// <param name="recipientLocale">Recipient locale (null — base language).</param>
    /// <param name="userAuthRateLimiter">Per-user rate limiter (null — no limit).</param>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="transactionId">Transaction the event named, for the log entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task ReplyToUnservedStartAsync(
        IChannelAdapter adapter,
        ChannelIdentitySnapshot identity,
        string? replyText,
        string? recipientLocale,
        IUserAuthRateLimiter? userAuthRateLimiter,
        IConfirmationPromptLocalizer? promptLocalizer,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(replyText))
        {
            return;
        }

        if (userAuthRateLimiter is not null)
        {
            var userKey = NormalizeUserKey(identity);
            var rateLimitResult = await userAuthRateLimiter.TryAcquireAsync(
                UserAuthRateLimitOperations.AuthStart + userKey, cancellationToken);

            if (!rateLimitResult.IsAllowed)
            {
                logger.LogWarning(
                    "Per-user rate limit exceeded — the unserved sign-in start stays unanswered. " +
                    "TransactionId: {TransactionId}, UserKeyHash: {UserKeyHash}",
                    transactionId,
                    LogMasking.Fingerprint(userKey));
                return;
            }
        }

        await SendDeclaredReplyAsync(
            adapter, identity, replyText, recipientLocale, promptLocalizer, logger, metrics,
            cancellationToken);
    }

    /// <summary>
    /// Sends the text a registration declared for an occurrence, if it declared one. The single point
    /// where a declared reply leaves the pipeline: an undeclared and a blank text mean the same thing
    /// — the channel stays silent — and that reading lives here rather than at each call site.
    /// </summary>
    /// <param name="adapter">Channel adapter the reply travels through.</param>
    /// <param name="identity">Identity snapshot of the recipient.</param>
    /// <param name="replyText">Declared reply text (null or blank — nothing is sent).</param>
    /// <param name="recipientLocale">Recipient locale (null — base language).</param>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static Task SendDeclaredReplyAsync(
        IChannelAdapter adapter,
        ChannelIdentitySnapshot identity,
        string? replyText,
        string? recipientLocale,
        IConfirmationPromptLocalizer? promptLocalizer,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(replyText))
        {
            return Task.CompletedTask;
        }

        // Not an outcome of a transaction, so it stays a plain message and is not gated by the
        // outcome capability — a channel that shows no outcome still gets it.
        return SendUserMessageAsync(
            adapter,
            identity.ChannelUserId,
            Localize(promptLocalizer, replyText, recipientLocale),
            logger,
            metrics,
            cancellationToken);
    }

    /// <summary>
    /// Logs and journals a channel event on a transaction that waits for missing claims (SPEC-041
    /// L91): the user has already confirmed, and the completion mark froze the channel snapshot, so a
    /// start, a confirmation or a decline from an admitted sender is not acted on. It reaches the
    /// journal as a transaction that no longer accepts the event (SPEC-039 E48) — the same reason a
    /// decision recorded in parallel is written under. What the sender is answered with stays with
    /// the caller, which knows whether the event's budget is already spent.
    /// </summary>
    /// <param name="transactionId">Transaction the event named.</param>
    /// <param name="adapter">Channel adapter the event arrived through.</param>
    /// <param name="refusalErrorCode">Code the engine refused with, when a write was refused; null
    /// when the transaction as read already carried the mark.</param>
    /// <param name="eventPublisher">Bus the refusal is written to (null — no journal).</param>
    /// <param name="timeProvider">Clock the moment of the audit event is read off.</param>
    /// <param name="logger">Logger.</param>
    private static async Task RecordClaimCompletionRefusalAsync(
        TransactionId transactionId,
        IChannelAdapter adapter,
        string? refusalErrorCode,
        ITransactionEventPublisher? eventPublisher,
        TimeProvider timeProvider,
        ILogger logger)
    {
        logger.LogInformation(
            "Channel event on a transaction waiting for missing claims — the event is not acted on. " +
            "TransactionId: {TransactionId}, ChannelType: {ChannelType}, Error: {ErrorCode}",
            transactionId,
            adapter.ChannelType,
            refusalErrorCode);

        await PublishChannelRefusalAsync(
            eventPublisher, timeProvider, transactionId, adapter.ChannelType,
            ChannelInteractionAuditCodes.LinkNotAnswerable, refusalErrorCode, logger);
    }

    /// <summary>
    /// Tells whether a refused confirmation met a transaction waiting for missing claims (SPEC-041
    /// L91): the engine said so itself, or the refusal is a race and the transaction, read once
    /// more, carries the completion mark — a parallel confirmation set it between the read and the
    /// write.
    /// </summary>
    /// <param name="errorCode">Code the confirmation was refused with.</param>
    /// <param name="transactionId">Transaction the confirmation named.</param>
    /// <param name="transactionService">Transaction service the re-read goes to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the transaction waits for missing claims.</returns>
    private static async Task<bool> IsRefusedByClaimCompletionAsync(
        string errorCode,
        TransactionId transactionId,
        ITransactionService transactionService,
        CancellationToken cancellationToken)
    {
        if (errorCode is TransactionErrorCodes.ClaimCompletionInProgress)
        {
            return true;
        }

        if (DecisionRefusalClassifier.Classify(errorCode) is not DecisionRefusalClass.Race)
        {
            return false;
        }

        var current = await transactionService.GetTransactionAsync(transactionId, cancellationToken);

        return current.IsSuccess && current.Value.IsCompletingClaims();
    }

    /// <summary>
    /// Asks the engine's own admissibility rule whether the sender of a channel event may act on the
    /// transaction the event names — the channel is in the transaction's allow-list, the sender is not
    /// a bot the application rejects — and writes a refusal into the integrator's journal under
    /// <see cref="ChannelInteractionAuditCodes.SenderNotAdmitted"/> (SPEC-039 E48).
    /// </summary>
    /// <remarks>
    /// The engine applies the same rule on its writes only while the transaction is still awaiting a
    /// decision: the idempotent success of a confirmation, the failure a spent budget writes and the
    /// question sent into the channel do not pass it. So every channel path that acts on a found
    /// transaction for a sender asks this first. What the refused sender is answered with stays with
    /// the caller — a pressed button is always answered, a followed link is not.
    /// </remarks>
    /// <param name="transaction">Transaction the event named.</param>
    /// <param name="identity">Identity snapshot of the sender.</param>
    /// <param name="adapter">Channel adapter the event arrived through.</param>
    /// <param name="transactionService">Transaction service — the one implementation of the rule.</param>
    /// <param name="eventPublisher">Bus the refusal is written to (null — no journal).</param>
    /// <param name="timeProvider">Clock the moment of the audit event is read off.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the sender is admitted; <c>false</c> — refused, logged and journaled.</returns>
    private static async Task<bool> IsSenderAdmittedAsync(
        Transaction transaction,
        ChannelIdentitySnapshot identity,
        IChannelAdapter adapter,
        ITransactionService transactionService,
        ITransactionEventPublisher? eventPublisher,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
        => await SenderAdmissionRefusalAsync(
            transaction, identity, adapter, transactionService, eventPublisher, timeProvider, logger,
            cancellationToken) is null;

    /// <summary>
    /// The admission gate of <see cref="IsSenderAdmittedAsync"/> for a caller that hands the refusal on by
    /// its code rather than answering it on the spot: the same rule, the same log entry and the same
    /// journal record.
    /// </summary>
    /// <param name="transaction">Transaction the event named.</param>
    /// <param name="identity">Identity snapshot of the sender.</param>
    /// <param name="adapter">Channel adapter the event arrived through.</param>
    /// <param name="transactionService">Transaction service — the one implementation of the rule.</param>
    /// <param name="eventPublisher">Bus the refusal is written to (null — no journal).</param>
    /// <param name="timeProvider">Clock the moment of the audit event is read off.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Null — the sender is admitted; otherwise the code the rule refused it with, already
    /// logged and journaled.</returns>
    private static async Task<string?> SenderAdmissionRefusalAsync(
        Transaction transaction,
        ChannelIdentitySnapshot identity,
        IChannelAdapter adapter,
        ITransactionService transactionService,
        ITransactionEventPublisher? eventPublisher,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var admissibility = await transactionService.ValidateChannelAdmissibilityAsync(
            transaction, identity, cancellationToken);

        if (admissibility.IsSuccess)
        {
            return null;
        }

        logger.LogWarning(
            "The sender is not admitted by the transaction — the channel event is not acted on and no outcome is disclosed. TransactionId: {TransactionId}, ChannelType: {ChannelType}, Error: {ErrorCode}",
            transaction.Id,
            adapter.ChannelType,
            admissibility.Error.Code);

        await PublishChannelRefusalAsync(
            eventPublisher, timeProvider, transaction.Id, adapter.ChannelType,
            ChannelInteractionAuditCodes.SenderNotAdmitted, admissibility.Error.Code, logger);

        return admissibility.Error.Code;
    }

    /// <summary>
    /// Logs and journals a channel event of a sender other than the subject the transaction is bound
    /// to (SPEC-039 N34): <see cref="ChannelInteractionAuditCodes.SenderNotAdmitted"/> with
    /// <see cref="TransactionErrorCodes.ChannelSubjectMismatch"/> in the details (SPEC-039 E48). Nothing
    /// of the bound subject is written anywhere. What the sender is answered with stays with the caller.
    /// </summary>
    /// <param name="transactionId">Transaction the event named.</param>
    /// <param name="adapter">Channel adapter the event arrived through.</param>
    /// <param name="eventPublisher">Bus the refusal is written to (null — no journal).</param>
    /// <param name="timeProvider">Clock the moment of the audit event is read off.</param>
    /// <param name="logger">Logger.</param>
    private static async Task RecordSubjectMismatchAsync(
        TransactionId transactionId,
        IChannelAdapter adapter,
        ITransactionEventPublisher? eventPublisher,
        TimeProvider timeProvider,
        ILogger logger)
    {
        logger.LogWarning(
            "The sender is not the channel subject the transaction is bound to — the channel event is not acted on. " +
            "TransactionId: {TransactionId}, ChannelType: {ChannelType}",
            transactionId,
            adapter.ChannelType);

        await PublishChannelRefusalAsync(
            eventPublisher, timeProvider, transactionId, adapter.ChannelType,
            ChannelInteractionAuditCodes.SenderNotAdmitted, TransactionErrorCodes.ChannelSubjectMismatch, logger);
    }

    /// <summary>
    /// Answers a pressed button of a sender other than the subject the transaction is bound to
    /// (SPEC-007 UI-022, SPEC-039 N34): nothing is changed, and the prompt reference of the bound
    /// subject is not consumed.
    /// </summary>
    /// <remarks>
    /// A sender the transaction does not admit is answered by the admission gate, as it always is — its
    /// refusal is the one the journal gets. Any other is answered as a button naming a transaction that
    /// is not there (SPEC-039 C15), and journaled as a subject mismatch.
    /// </remarks>
    /// <param name="identity">Identity snapshot of the sender.</param>
    /// <param name="inboundToken">Opaque token of the live interaction (null — no interaction).</param>
    /// <param name="transaction">Transaction the button named.</param>
    /// <param name="recipientLocale">Recipient locale of the admission refusal (null — base language).</param>
    /// <param name="adapter">Channel adapter the button arrived through.</param>
    /// <param name="transactionService">Transaction service — the one implementation of the admission rule.</param>
    /// <param name="receiptText">Port the terminal wording comes from.</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="errorText">Message text shown to the user on error.</param>
    /// <param name="eventPublisher">Bus the refusal is written to (null — no journal).</param>
    /// <param name="timeProvider">Clock the moment of the audit event is read off.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task RefuseDecisionOfAnotherSubjectAsync(
        ChannelIdentitySnapshot identity,
        ChannelInboundToken? inboundToken,
        Transaction transaction,
        string? recipientLocale,
        IChannelAdapter adapter,
        ITransactionService transactionService,
        IOutcomeReceiptText receiptText,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        IConfirmationPromptLocalizer? promptLocalizer,
        string errorText,
        ITransactionEventPublisher? eventPublisher,
        TimeProvider timeProvider,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (!await IsSenderAdmittedAsync(
                transaction, identity, adapter, transactionService, eventPublisher, timeProvider, logger,
                cancellationToken))
        {
            await ShowOutcomeAsync(
                adapter, transaction.Id, identity.ChannelUserId, inboundToken,
                TransactionOutcome.Failed,
                Localize(promptLocalizer, errorText, recipientLocale),
                promptMessageStore: null,
                displayIntentSource,
                TransactionResolutionContext.For(transaction),
                logger, metrics, cancellationToken);
            return;
        }

        await RecordSubjectMismatchAsync(transaction.Id, adapter, eventPublisher, timeProvider, logger);

        await ShowUnknownTransactionAsync(
            adapter, transaction.Id, identity, inboundToken, receiptText, promptMessageStore: null,
            displayIntentSource, logger, metrics, cancellationToken);
    }

    /// <summary>
    /// Answers a sign-in start whose binding to the sender the engine refused (SPEC-039 N34). It is
    /// reached past the subject gate and past the rate-limit block, so the refusal is something the gate
    /// could not see — another subject bound first, the window closed, a decision recorded in parallel —
    /// and the budget of this event is already spent: the reply goes out without taking it again.
    /// </summary>
    /// <remarks>
    /// Each answer is the one the same occurrence gets before the binding: a subject mismatch and a
    /// transaction that no longer accepts the event get the neutral text of an unknown transaction, in
    /// the language of the snapshot alone (SPEC-039 C15); a refusal of the sender, or of ours, gets
    /// silence, as on the attach of the web-page surface.
    /// </remarks>
    /// <param name="refusalCode">Code the binding was refused with.</param>
    /// <param name="identity">Identity snapshot of the sender.</param>
    /// <param name="transactionId">Transaction the start named.</param>
    /// <param name="adapter">Channel adapter the start arrived through.</param>
    /// <param name="staleLinkReplyText">Declared reply to an unservable sign-in link (null — silence).</param>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="eventPublisher">Bus the refusal is written to (null — no journal).</param>
    /// <param name="timeProvider">Clock the moment of the audit event is read off.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics the unserved start is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task AnswerRefusedBindingAsync(
        string refusalCode,
        ChannelIdentitySnapshot identity,
        TransactionId transactionId,
        IChannelAdapter adapter,
        string? staleLinkReplyText,
        IConfirmationPromptLocalizer? promptLocalizer,
        ITransactionEventPublisher? eventPublisher,
        TimeProvider timeProvider,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
    {
        metrics.RecordUnservedStart(adapter.ChannelType);

        if (refusalCode is TransactionErrorCodes.ChannelSubjectMismatch)
        {
            await RecordSubjectMismatchAsync(transactionId, adapter, eventPublisher, timeProvider, logger);
        }
        else
        {
            var refusalClass = await RecordChannelRefusalAsync(
                RefusedSubjectBinding, refusalCode, transactionId, adapter, eventPublisher, timeProvider, logger);
            if (refusalClass is not (DecisionRefusalClass.Expired or DecisionRefusalClass.Race))
            {
                return;
            }
        }

        await SendDeclaredReplyAsync(
            adapter, identity, staleLinkReplyText, identity.Locale, promptLocalizer, logger, metrics,
            cancellationToken);
    }

    /// <summary>
    /// Records an engine refusal of a write of the sender's channel data — the operational log and the
    /// audit event of its class — and hands the class back: what the sender is answered is decided by the
    /// caller.
    /// </summary>
    /// <remarks>
    /// A late delivery of the same channel event (the transaction has already been decided or has
    /// expired) and a sender the transaction does not admit (a channel outside its allow-list, a bot, a
    /// sender refused by a restriction policy or by a registered admissibility check) are
    /// expected refusals, not operational errors — hence Warning for those and Error for the rest. An
    /// expiry is reported by two codes, not one: past the TTL the engine answers with the deadline while
    /// the transaction is still Pending, and with the state only after the cleanup pass has promoted it —
    /// the classifier reads both as one class.
    /// </remarks>
    /// <param name="refusedWrite">Name of the refused write, for the log.</param>
    /// <param name="refusalCode">Code the engine refused with.</param>
    /// <param name="transactionId">Transaction the event named.</param>
    /// <param name="adapter">Channel adapter the event arrived through.</param>
    /// <param name="eventPublisher">Bus the refusal is written to (null — no journal).</param>
    /// <param name="timeProvider">Clock the moment of the audit event is read off.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>The class of the refusal.</returns>
    private static async Task<DecisionRefusalClass> RecordChannelRefusalAsync(
        string refusedWrite,
        string refusalCode,
        TransactionId transactionId,
        IChannelAdapter adapter,
        ITransactionEventPublisher? eventPublisher,
        TimeProvider timeProvider,
        ILogger logger)
    {
        var refusalClass = DecisionRefusalClassifier.Classify(refusalCode);

        // A transaction the store no longer holds is logged and not journaled, on every entry of the
        // channel alike (SPEC-039 E48): the entry is keyed by the transaction, and there is none. The class
        // is still handed back — the sender is answered as for an expiry either way.
        if (refusalCode is TransactionErrorCodes.TransactionNotFound)
        {
            logger.LogWarning(
                "{RefusedWrite} — the transaction is no longer stored. TransactionId: {TransactionId}",
                refusedWrite,
                transactionId);
            return refusalClass;
        }

        var (level, reason, auditCode) = refusalClass switch
        {
            DecisionRefusalClass.Expired => (
                LogLevel.Warning, "the sign-in window has closed", ChannelInteractionAuditCodes.LinkExpired),
            DecisionRefusalClass.Race => (
                LogLevel.Warning, "the transaction does not accept this event",
                ChannelInteractionAuditCodes.LinkNotAnswerable),
            _ when refusalCode is TransactionErrorCodes.ChannelNotAllowed
                or TransactionErrorCodes.BotRejected
                or TransactionErrorCodes.RestrictionPolicyViolation
                or TransactionErrorCodes.SecurityPolicyViolation => (
                LogLevel.Warning, "the transaction does not admit this sender",
                ChannelInteractionAuditCodes.SenderNotAdmitted),
            _ => (
                LogLevel.Error, "the engine refused the write", ChannelInteractionAuditCodes.SenderNotAdmitted)
        };

        logger.Log(
            level,
            "{RefusedWrite} — {Reason}. TransactionId: {TransactionId}, Error: {ErrorCode}",
            refusedWrite,
            reason,
            transactionId,
            refusalCode);

        await PublishChannelRefusalAsync(
            eventPublisher, timeProvider, transactionId, adapter.ChannelType, auditCode, refusalCode, logger);

        return refusalClass;
    }

    /// <summary>
    /// Writes a refusal of the channel side of the interaction surface into the integrator's journal
    /// (SPEC-039 E48): one audit event on the transaction bus, for a transaction that WAS found.
    /// </summary>
    /// <remarks>
    /// The twin of the page's own record of the same class of occurrence, down to the treatment of an
    /// unavailable journal: a failed publish never changes what the sender is answered with, and the
    /// reason of the refusal is told to the journal alone (SPEC-039 C15).
    /// <see cref="CancellationToken.None"/> for the publish: the occurrence must reach the journal
    /// even if the platform's delivery attempt is being cancelled, or abandoning a request would
    /// suppress its own trace.
    /// </remarks>
    /// <param name="eventPublisher">Bus the event goes to (null — the composition has no journal).</param>
    /// <param name="timeProvider">Clock the moment of the event is read off.</param>
    /// <param name="transactionId">Transaction the event named.</param>
    /// <param name="channelType">Channel the event arrived through.</param>
    /// <param name="auditCode">Reason the journal reads (see <see cref="ChannelInteractionAuditCodes"/>).</param>
    /// <param name="refusalErrorCode">Code the engine refused with, when there was one.</param>
    /// <param name="logger">Logger.</param>
    private static async Task PublishChannelRefusalAsync(
        ITransactionEventPublisher? eventPublisher,
        TimeProvider timeProvider,
        TransactionId transactionId,
        string channelType,
        string auditCode,
        string? refusalErrorCode,
        ILogger logger)
    {
        if (eventPublisher is null)
        {
            return;
        }

        try
        {
            await eventPublisher.PublishAsync(
                new TransactionChannelAuditEvent
                {
                    TransactionId = transactionId,
                    OccurredAt = timeProvider.GetUtcNow(),
                    ChannelType = channelType,
                    EventCode = auditCode,
                    Details = refusalErrorCode
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to publish audit event {EventCode}. TransactionId: {TransactionId}",
                auditCode,
                transactionId);
        }
    }

    /// <summary>
    /// Tells the subscribers of the transaction bus that the sender entered the transaction and the
    /// answer is awaited in the channel (<see cref="ChannelAnswerStatuses.ChannelAnswered"/>).
    /// </summary>
    /// <remarks>
    /// An additive status of the channel: it moves no state and names the channel only — the sender
    /// is not part of it. It is a side signal of the start: a composition without a bus, or a bus
    /// that failed, leaves a warning and changes neither the answer to the user nor the result of the
    /// webhook. <see cref="CancellationToken.None"/> for the publish: the fact has happened, and an
    /// abandoned delivery attempt of the platform must not keep it from the page.
    /// </remarks>
    /// <param name="eventPublisher">Bus the status goes to (null — the composition has none).</param>
    /// <param name="timeProvider">Clock the moment of the event is read off.</param>
    /// <param name="transactionId">Transaction the sender entered.</param>
    /// <param name="channelType">Channel the start arrived through.</param>
    /// <param name="logger">Logger.</param>
    private static async Task PublishChannelAnsweredAsync(
        ITransactionEventPublisher? eventPublisher,
        TimeProvider timeProvider,
        TransactionId transactionId,
        string channelType,
        ILogger logger)
    {
        if (eventPublisher is null)
        {
            logger.LogWarning(
                "No transaction event publisher is registered; the page is not told that the channel answered. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                transactionId,
                channelType);
            return;
        }

        try
        {
            await eventPublisher.PublishAsync(
                new TransactionChannelStatusChangedEvent
                {
                    TransactionId = transactionId,
                    OccurredAt = timeProvider.GetUtcNow(),
                    ChannelType = channelType,
                    ChannelStatus = ChannelAnswerStatuses.ChannelAnswered
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to publish the channel status {ChannelStatus}. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                ChannelAnswerStatuses.ChannelAnswered,
                transactionId,
                channelType);
        }
    }

    /// <summary>
    /// Handles a direct message to the bot that names no transaction (SPEC-003 CA-201): a bare start
    /// command, free text from a sender the adapter has already vetted.
    /// </summary>
    /// <remarks>
    /// Nothing is looked up and nothing is written down — there is no transaction to look up. The
    /// answer is the one the registration declares, under the same per-user budget as any other
    /// sign-in start, and the shipped channels declare none: the mechanism exists, the behaviour of
    /// the product does not change until an installation states a text.
    /// </remarks>
    /// <param name="result">The inbound result carrying the sender.</param>
    /// <param name="adapter">Channel adapter the message arrived through.</param>
    /// <param name="replyText">Declared reply text (null — no reply).</param>
    /// <param name="userAuthRateLimiter">Per-user rate limiter (null — no limit).</param>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task HandleUnaddressedAsync(
        ChannelUnaddressedResult result,
        IChannelAdapter adapter,
        string? replyText,
        IUserAuthRateLimiter? userAuthRateLimiter,
        IConfirmationPromptLocalizer? promptLocalizer,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(replyText))
        {
            logger.LogDebug(
                "A message naming no transaction arrived; the channel declares no reply to it. ChannelType: {ChannelType}",
                adapter.ChannelType);
            return;
        }

        if (userAuthRateLimiter is not null)
        {
            var userKey = NormalizeUserKey(result.Identity);
            var rateLimitResult = await userAuthRateLimiter.TryAcquireAsync(
                UserAuthRateLimitOperations.AuthStart + userKey, cancellationToken);

            if (!rateLimitResult.IsAllowed)
            {
                logger.LogWarning(
                    "Per-user rate limit exceeded — the message naming no transaction stays unanswered. " +
                    "UserKeyHash: {UserKeyHash}",
                    LogMasking.Fingerprint(userKey));
                return;
            }
        }

        // There is no transaction, so the recipient-locale chain (B17) collapses to the channel
        // snapshot locale and then to the base language.
        await SendDeclaredReplyAsync(
            adapter, result.Identity, replyText, result.Identity.Locale, promptLocalizer, logger,
            metrics, cancellationToken);
    }

    /// <summary>
    /// Handles the user declining authentication.
    /// </summary>
    /// <param name="processor">Processor holding the services of the event's scope.</param>
    /// <param name="result">Inbound event result with decline data.</param>
    /// <param name="transaction">
    /// The transaction the decline names, read by the caller from the STORE — past the TTL gate, for
    /// the reason the twin parameter of the confirmation handler states. Null — there is no
    /// transaction to answer with.
    /// </param>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="transactionStore">Transaction store the single re-read after a refused write goes to.</param>
    /// <param name="timeProvider">Clock the deadline of the transaction is read against.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed user notification is counted into.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator — the point that
    /// answers whether this channel was the one asked to show the question (SPEC-039 E41).</param>
    /// <param name="receiptText">Receipt text of the transaction — a terminal outcome or the claims
    /// completion state (SPEC-036 TPL-123).</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="eventPublisher">Bus the refusals of the press are written to — a late press, a
    /// decision already recorded, a sender not admitted (SPEC-039 E48; null — no journal).</param>
    /// <param name="errorText">Message text shown to the user on error.</param>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — no prompt reference).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task HandleAuthDeclineAsync(
        ChannelInboundProcessor processor,
        ChannelAuthDeclineResult result,
        Transaction? transaction,
        IChannelAdapter adapter,
        ITransactionService transactionService,
        ITransactionStore transactionStore,
        TimeProvider timeProvider,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        IConfirmationPromptOrchestrator? confirmationPromptOrchestrator,
        IOutcomeReceiptText receiptText,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ITransactionEventPublisher? eventPublisher,
        string errorText,
        IConfirmationPromptLocalizer? promptLocalizer,
        IChannelPromptMessageStore? promptMessageStore,
        CancellationToken cancellationToken)
    {
        // The identifier arrives already parsed: the adapter is the one that reads it out of the
        // channel event, so an unparseable value never reaches this point (SPEC-003 §6.3).
        var transactionId = result.TransactionId;

        if (transaction is null)
        {
            logger.LogWarning(
                "No transaction to answer the AuthDecline with. TransactionId: {TransactionId}",
                result.TransactionId);

            await ShowUnknownTransactionAsync(
                adapter, transactionId, result.Identity, result.InboundToken, receiptText,
                promptMessageStore, displayIntentSource, logger, metrics, cancellationToken);
            return;
        }

        // Recipient-locale chain (B17): channel snapshot locale → initiation-page language → base.
        var recipientLocale = result.Identity.Locale ?? transaction.GetUiLocale();

        // The subject gate of the confirmation, for the same reason: failing a transaction checks the
        // state and the concurrency token, not who asks, so another subject would otherwise end the
        // sign-in of the bound one.
        if (transaction.IsBoundToAnotherSubject(result.Identity))
        {
            await RefuseDecisionOfAnotherSubjectAsync(
                result.Identity, result.InboundToken, transaction, recipientLocale, adapter,
                transactionService, receiptText, displayIntentSource, promptLocalizer, errorText,
                eventPublisher, timeProvider, logger, metrics, cancellationToken);
            return;
        }

        // The window closed before the button was pressed — the twin of the confirmation pre-check,
        // and deliberately the same answer: a decision reported after the deadline is answered by
        // what happened to the transaction, not by which button was pressed.
        if (transaction.HasRunOutOfTime(timeProvider.GetUtcNow()))
        {
            // The transaction was found, so the late press reaches the journal (SPEC-039 E48). No
            // engine code went with it — the deadline or the state itself is the reason.
            await PublishChannelRefusalAsync(
                eventPublisher, timeProvider, transactionId, adapter.ChannelType,
                ChannelInteractionAuditCodes.LinkExpired, refusalErrorCode: null, logger);

            await ShowOutcomeAsync(
                adapter, transactionId, result.Identity.ChannelUserId, result.InboundToken,
                TransactionOutcome.Expired,
                await ExpiredReceiptAsync(
                    adapter,
                    transaction,
                    // The gate of the caller values cuts off what the calling party supplied and
                    // nothing the core states about the transaction.
                    SenderSlotSource(transaction, result.Identity) with
                    {
                        CallerSlotValues = await ShownCallerSlotValuesAsync(
                            transaction, adapter, confirmationPromptOrchestrator, cancellationToken)
                    },
                    recipientLocale,
                    receiptText,
                    cancellationToken),
                promptMessageStore,
                displayIntentSource,
                // The context the expiry helper addresses its wording with — the transaction in hand.
                TransactionResolutionContext.For(transaction),
                logger, metrics, cancellationToken);

            await ExpireAfterLateDecisionAsync(transactionService, transactionId, cancellationToken);
            return;
        }

        TagCorrelatingTrace(transaction);

        // The same gate as on the confirmation (SPEC-039 E41): a channel that was not shown the
        // subject does not decide its fate either way.
        if (!await AcceptsChannelDecisionAsync(
                transaction, adapter, confirmationPromptOrchestrator, logger, cancellationToken))
        {
            return;
        }

        // The same gate of the sender as on the confirmation, for the reason stated there: failing a
        // transaction checks the state and the concurrency token, not who asks, so a sender the
        // transaction does not admit would otherwise end someone else's sign-in and be answered with
        // the receipt of that decline.
        if (!await IsSenderAdmittedAsync(
                transaction, result.Identity, adapter, transactionService, eventPublisher,
                timeProvider, logger, cancellationToken))
        {
            await ShowOutcomeAsync(
                adapter, transactionId, result.Identity.ChannelUserId, result.InboundToken,
                TransactionOutcome.Failed,
                Localize(promptLocalizer, errorText, recipientLocale),
                promptMessageStore,
                displayIntentSource,
                TransactionResolutionContext.For(transaction),
                logger, metrics, cancellationToken);
            return;
        }

        // The user has already confirmed and the transaction waits for missing claims (SPEC-041
        // L91): the completion mark froze the snapshot, so the press changes nothing — the decline
        // button of the question no longer declines — and is recorded and answered exactly as the
        // confirm button is: with the state of the wait on a channel that updates its messages, with
        // the request of the step sent again on one that does not.
        if (transaction.IsCompletingClaims())
        {
            await RecordClaimCompletionRefusalAsync(
                transactionId, adapter, refusalErrorCode: null, eventPublisher, timeProvider, logger);

            await AnswerRepeatedPressInTheCompletionAsync(
                processor, adapter, transaction, transaction.UpdatedAt, result.Identity,
                result.InboundToken, recipientLocale, receiptText, promptMessageStore,
                displayIntentSource, logger, metrics, cancellationToken);
            return;
        }

        // Decline the transaction
        var failResult = await transactionService.FailTransactionAsync(
            transactionId,
            TransactionErrorCodes.DeclinedByUser,
            transaction.ConcurrencyToken,
            cancellationToken);

        if (failResult.IsFailure)
        {
            // The refusal is read by its class, exactly as on the confirmation: the user is still
            // shown a terminal state even on error — the stale buttons must go — and which state
            // that is depends on what refused the write.
            var refusal = await AnswerToRefusedDecisionAsync(
                failResult.Error.Code, TransactionOutcome.Declined, transaction, result.Identity, adapter,
                transactionStore, eventPublisher, timeProvider, recipientLocale, receiptText,
                promptLocalizer, errorText, logger, cancellationToken);

            // The transaction waits for missing claims — the pre-check's answer, reached through the
            // race, and given under the same condition; the record is already written.
            if (refusal is null)
            {
                await AnswerRepeatedPressInTheCompletionAsync(
                    processor, adapter, transaction, transaction.UpdatedAt, result.Identity,
                    result.InboundToken, recipientLocale, receiptText, promptMessageStore,
                    displayIntentSource, logger, metrics, cancellationToken);
                return;
            }

            await ShowOutcomeAsync(
                adapter, transactionId, result.Identity.ChannelUserId, result.InboundToken,
                refusal.Value.Outcome, refusal.Value.Text,
                promptMessageStore,
                displayIntentSource,
                TransactionResolutionContext.For(transaction),
                logger, metrics, cancellationToken);
            return;
        }

        // Show the decline outcome (CA-142) — a receipt, already localized by the port.
        await ShowOutcomeAsync(
            adapter, transactionId, result.Identity.ChannelUserId, result.InboundToken,
            TransactionOutcome.Declined,
            await receiptText.RenderAsync(
                ReceiptAddressOf(TransactionOutcome.Declined, adapter, transaction),
                recipientLocale,
                transaction.GetUiTimeZone(),
                // The refusal has just been written down; its moment is the one the write recorded,
                // read off the instance that write returned.
                failResult.Value.UpdatedAt,
                // Past the decision gate: the question stood before this user in this channel.
                SenderSlotSource(transaction, result.Identity),
                TransactionResolutionContext.For(transaction),
                cancellationToken),
            promptMessageStore,
            displayIntentSource,
            TransactionResolutionContext.For(transaction),
            logger, metrics, cancellationToken);
    }

    /// <summary>
    /// Localizes a status Natural Key into the recipient's language (ICC-050); when the localizer or a
    /// translation is unavailable, returns the key's base text (<see cref="NaturalKeyContext.BaseTextOf"/>).
    /// </summary>
    /// <param name="promptLocalizer">Channel message localizer (null — base language).</param>
    /// <param name="naturalKey">Status text Natural Key (base language, English).</param>
    /// <param name="recipientLocale">Recipient locale (IETF tag; null — base language).</param>
    /// <returns>The translated text, or the key's base text when there is no translation.</returns>
    private static string Localize(
        IConfirmationPromptLocalizer? promptLocalizer,
        string naturalKey,
        string? recipientLocale)
        => promptLocalizer?.ResolveOrBaseText(naturalKey, recipientLocale)
            ?? NaturalKeyContext.BaseTextOf(naturalKey);

    /// <summary>
    /// Sends a plain message to the user through the shared outbound seam, which gates the content
    /// kind, logs a failure with the channel type and the error code, and counts it.
    /// </summary>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="channelUserId">Recipient within the channel.</param>
    /// <param name="text">Already localized message text.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static Task SendUserMessageAsync(
        IChannelAdapter adapter,
        string channelUserId,
        string text,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
    {
        var message = new ChannelMessage
        {
            ChannelUserId = channelUserId,
            Text = text
        };

        return ChannelOutbound.SendMessageAsync(adapter, message, logger, metrics, cancellationToken);
    }

    /// <summary>
    /// Answers a press on a sign-in that was already waiting for missing claims when the press
    /// arrived (SPEC-041 L91; SPEC-003 CA-192). A channel that declares the update of its messages
    /// is shown the state of the wait: the press gets its answer, and the state lands on the question
    /// message. A channel that does not is not sent the state — it would arrive as one more message
    /// under the request of the step in progress — and is sent that request again instead: on such a
    /// platform the press itself may stand in the chat as a message of the user and take the buttons
    /// of the request away. The fact is the one the channel declared, so the decision is the
    /// caller's and not the adapter's (SPEC-003 CA-183). The record of the press is the caller's
    /// business and is written before this call, whichever way it goes.
    /// </summary>
    /// <param name="processor">Processor holding the services of the event's scope.</param>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="transaction">Transaction the press names.</param>
    /// <param name="stateMoment">Moment the instance in hand was last written.</param>
    /// <param name="sender">Identity of the user who pressed.</param>
    /// <param name="inboundToken">Opaque token of the live interaction (null — no interaction).</param>
    /// <param name="recipientLocale">Recipient locale (null — base language).</param>
    /// <param name="receiptText">Port the wording of the state comes from.</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — no prompt reference).</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion of the answer.</returns>
    private static Task AnswerRepeatedPressInTheCompletionAsync(
        ChannelInboundProcessor processor,
        IChannelAdapter adapter,
        Transaction transaction,
        DateTimeOffset stateMoment,
        ChannelIdentitySnapshot sender,
        ChannelInboundToken? inboundToken,
        string? recipientLocale,
        IOutcomeReceiptText receiptText,
        IChannelPromptMessageStore? promptMessageStore,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
        => adapter.Capabilities.SupportsMessageUpdate
            ? ShowClaimCompletionStateAsync(
                adapter, transaction, stateMoment, sender, inboundToken, recipientLocale,
                receiptText, promptMessageStore, displayIntentSource, logger, metrics, cancellationToken)
            : SendPhoneRequestAgainAsync(
                processor, transaction, sender, processor.UserAuthRateLimiter,
                UserAuthRateLimitOperations.AuthConfirm, cancellationToken);

    /// <summary>
    /// Sends the phone number request of a transaction waiting in the phone step of its claims
    /// completion once more — the answer of a channel that does not update its messages to an event
    /// of the user that may have taken the buttons of the request away (SPEC-003 CA-192, CA-200).
    /// The record tying the chat to the transaction and the window of an <c>Optional</c> number are
    /// those of the first request and are left as they are. A transaction that waits in another
    /// step, or whose instance in hand was read before the completion began, gets nothing: the
    /// starter of its step is the one that speaks in the chat.
    /// </summary>
    /// <param name="processor">Processor holding the services of the event's scope.</param>
    /// <param name="transaction">Transaction the event names.</param>
    /// <param name="sender">Identity of the user the event came from.</param>
    /// <param name="userAuthRateLimiter">Per-user rate limiter the event is charged to (null — nothing
    /// is charged here: the host registered none, or the attempt of this event is already taken).</param>
    /// <param name="rateLimitOperation">Partition of the per-user rate limiter the event belongs to
    /// (<see cref="UserAuthRateLimitOperations"/>); a refusal leaves the event unanswered and does
    /// not touch the transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task SendPhoneRequestAgainAsync(
        ChannelInboundProcessor processor,
        Transaction transaction,
        ChannelIdentitySnapshot sender,
        IUserAuthRateLimiter? userAuthRateLimiter,
        string rateLimitOperation,
        CancellationToken cancellationToken)
    {
        if (transaction.GetCurrentClaimCompletionStep() is not ClaimCompletionStep.Phone)
        {
            return;
        }

        if (userAuthRateLimiter is { } rateLimiter)
        {
            var userKey = NormalizeUserKey(sender);
            var rateLimitResult = await rateLimiter.TryAcquireAsync(rateLimitOperation + userKey, cancellationToken);

            if (!rateLimitResult.IsAllowed)
            {
                processor.Logger.LogWarning(
                    "Per-user rate limit exceeded — the phone number request is not sent again. " +
                    "TransactionId: {TransactionId}, UserKeyHash: {UserKeyHash}",
                    transaction.Id.ToString(),
                    LogMasking.Fingerprint(userKey));
                return;
            }
        }

        if (await processor.PhoneNumberRequestBuilder.BuildAsync(transaction, cancellationToken) is { } request)
        {
            processor.ClaimCompletionDelivery.DeliverInBackground(transaction, request.Message);
        }
    }

    /// <summary>
    /// Shows the state of a transaction that waits for missing claims (SPEC-041 L91): the platform
    /// gets its answer to the press, and the question message loses its buttons and reads "confirmed,
    /// one step left" (SPEC-003 CA-142, CA-192). The one notice of this route that is not the last
    /// word about the transaction. The press that sent the sign-in into the completion is answered
    /// with it on every channel; a press made while the completion lasts — only on a channel that
    /// updates its messages, which <see cref="AnswerRepeatedPressInTheCompletionAsync"/> decides.
    /// </summary>
    /// <remarks>
    /// Called only from the handling of an inbound channel event: a notice sent outside of one would
    /// share the idempotency key of the terminal notice on the HTTP channel.
    /// </remarks>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="transaction">Transaction the press names — the source of the address, the slot
    /// values and the ownership context of the wording.</param>
    /// <param name="stateMoment">Moment the instance in hand was last written — the moment of the
    /// state for a receipt that declares one.</param>
    /// <param name="sender">Identity snapshot of the sender of the press — the recipient within the channel
    /// and the account the receipt names.</param>
    /// <param name="inboundToken">Opaque token of the live interaction (null — no interaction).</param>
    /// <param name="recipientLocale">Recipient locale (null — base language).</param>
    /// <param name="receiptText">Port the wording of the state comes from (SPEC-036 TPL-123).</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — no prompt reference).</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task ShowClaimCompletionStateAsync(
        IChannelAdapter adapter,
        Transaction transaction,
        DateTimeOffset stateMoment,
        ChannelIdentitySnapshot sender,
        ChannelInboundToken? inboundToken,
        string? recipientLocale,
        IOutcomeReceiptText receiptText,
        IChannelPromptMessageStore? promptMessageStore,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
        => await ShowOutcomeAsync(
            adapter, transaction.Id, sender.ChannelUserId, inboundToken,
            TransactionOutcome.AwaitingClaims,
            await receiptText.RenderAsync(
                ReceiptAddressOf(TransactionOutcome.AwaitingClaims, adapter, transaction),
                recipientLocale,
                transaction.GetUiTimeZone(),
                stateMoment,
                // Past the decision gate: the question stood before this user in this channel.
                SenderSlotSource(transaction, sender),
                TransactionResolutionContext.For(transaction),
                cancellationToken),
            promptMessageStore,
            displayIntentSource,
            TransactionResolutionContext.For(transaction),
            logger, metrics, cancellationToken);

    /// <summary>
    /// States the core's intent "show the user the state of this transaction" (SPEC-003 §6.2): one of
    /// its terminal outcomes, or the one non-terminal state — a sign-in confirmed and waiting for
    /// missing claims. For a terminal outcome the prompt reference is taken from the store with
    /// remove-on-read, exactly like the TTL path — the two paths differ only in what triggers them;
    /// for the claims completion state it is read and left in place. The desired display intent is
    /// resolved here too, so every outcome this route shows states the one intent of the deployment,
    /// whichever branch it came from.
    /// </summary>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="channelUserId">Recipient within the channel.</param>
    /// <param name="inboundToken">Opaque token of the live interaction (null — no interaction).</param>
    /// <param name="outcome">Terminal outcome to show, or <see cref="TransactionOutcome.AwaitingClaims"/>.</param>
    /// <param name="statusText">Already localized status text.</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates (null — no prompt reference).</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="resolutionContext">
    /// Ownership context the intent is resolved for — the one the caller states for the wording of
    /// this very notice, so the text and the way it is shown are answered at one level.
    /// </param>
    /// <param name="logger">Logger.</param>
    /// <param name="metrics">Component metrics a failed notification is counted into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task ShowOutcomeAsync(
        IChannelAdapter adapter,
        TransactionId transactionId,
        string channelUserId,
        ChannelInboundToken? inboundToken,
        TransactionOutcome outcome,
        string statusText,
        IChannelPromptMessageStore? promptMessageStore,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ResolutionContext resolutionContext,
        ILogger logger,
        ChannelAdapterMetrics metrics,
        CancellationToken cancellationToken)
    {
        // Resolved with the token of the call and BEFORE the stored reference is consumed: a
        // cancellation here leaves that reference in the store for the background expiry path,
        // instead of spending it on a notice this call is no longer going to build.
        var displayIntent = await displayIntentSource.ResolveAsync(resolutionContext, cancellationToken);

        ChannelMessageRef? promptMessage = null;
        ChannelMessageRef? phoneRequestMessage = null;
        if (promptMessageStore is not null)
        {
            // The claims completion state is not the last word about the transaction: the reference
            // is read and stays in the store for the expiry path and for a repeated press. Any other
            // outcome is terminal and consumes it. CancellationToken.None on the take: the
            // transaction has already reached a terminal state, so the stored reference must be
            // consumed even if the request is being cancelled.
            var stored = outcome == TransactionOutcome.AwaitingClaims
                ? await promptMessageStore.FindAsync(transactionId, cancellationToken)
                : await promptMessageStore.TakeAsync(transactionId, CancellationToken.None);
            promptMessage = stored?.Message;

            // A terminal outcome consumed the record, and the reference to a phone number request
            // goes with it: the adapter takes the request down by this notice, and nothing reads
            // the record afterwards.
            phoneRequestMessage = outcome == TransactionOutcome.AwaitingClaims ? null : stored?.PhoneRequestMessage;
        }

        var notice = new TransactionOutcomeNotice
        {
            TransactionId = transactionId,
            Outcome = outcome,
            ChannelUserId = channelUserId,
            StatusText = statusText,
            InboundToken = inboundToken,
            PromptMessage = promptMessage,
            PhoneRequestMessage = phoneRequestMessage,
            DisplayIntent = displayIntent
        };

        await ChannelOutbound.NotifyOutcomeAsync(adapter, notice, logger, metrics, cancellationToken);
    }

    /// <summary>
    /// Stitches the inbound trace to the trace that created the transaction, by hanging the creating
    /// trace's identifier on the inbound span as an attribute. Navigation in a dashboard is then a
    /// search by that attribute.
    /// </summary>
    /// <remarks>
    /// An attribute rather than a trace link: a link is built out of an <c>ActivityContext</c> and
    /// therefore needs a span id as well, while the transaction stores only a trace id. A link
    /// assembled on a zero span id would build, yet point nowhere — backends drop such a reference or
    /// render it broken.
    /// The current activity is checked against the inbound span by name on purpose: the same handlers
    /// also run on the polling loops, where no inbound span was opened and the ambient activity, if
    /// any, belongs to something else entirely and must not be tagged.
    /// An empty correlation (a host without telemetry, a transaction created before this ever worked)
    /// leaves the span untagged — there is nothing to stitch to.
    /// </remarks>
    /// <param name="transaction">Transaction the inbound event addresses.</param>
    private static void TagCorrelatingTrace(Transaction transaction)
    {
        if (string.IsNullOrEmpty(transaction.CorrelationId))
        {
            return;
        }

        if (Activity.Current is { OperationName: ChannelTelemetry.InboundActivityName } inbound)
        {
            inbound.SetTag(TransactionTelemetry.CorrelationTraceIdTag, transaction.CorrelationId);
        }
    }

    /// <summary>
    /// Creates the pipeline logger from the ILoggerFactory of the current request.
    /// </summary>
    private static ILogger GetLogger(HttpContext httpContext)
        => httpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ChannelWebhookPipeline));

    /// <summary>
    /// Finds the adapter by channel type via a linear scan over the registered adapters (there are only a few) —
    /// the one lookup the webhook, the delivery of claim completion and the phone step share.
    /// An adapter is a stateless singleton per channel type (CA-014/CA-164): a tenant does not multiply adapter
    /// instances; instead, the seam determines which credentials/client it uses during processing. The tenant
    /// is carried into processing via the ambient context (<see cref="ChannelTenantContext"/>) set by the
    /// caller before this method, not via an adapter selection parameter.
    /// Deliberately no static cache: a cache would be built from the adapters of the FIRST request and would leak
    /// between different DI containers in one process (multiple WebApplicationFactory instances in tests,
    /// a multi-tenant host) — review feedback. The scan cost is negligible next to JSON parsing and the DB.
    /// </summary>
    /// <param name="adapters">Registered adapters.</param>
    /// <param name="channelType">Channel type.</param>
    /// <returns>Adapter of the channel type or null if not registered.</returns>
    internal static IChannelAdapter? FindAdapter(IEnumerable<IChannelAdapter> adapters, string channelType)
    {
        foreach (var adapter in adapters)
        {
            if (string.Equals(adapter.ChannelType, channelType, StringComparison.Ordinal))
            {
                return adapter;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes the user key for the rate limiter.
    /// Format: {tenant}:{channel_type}:{channel_user_id} — lowercase, trimmed.
    /// Prevents bypass via letter case or whitespace.
    /// </summary>
    /// <remarks>
    /// The tenant leads the key so that tenants do not share one user's budget: the same
    /// <c>channel_user_id</c> under two tenants is two users of two installations, and a shared
    /// counter would let one tenant's traffic throttle another's. An unstated tenant takes the one
    /// fixed spelling of <see cref="TenantKey.Segment"/> — the same the identity record uses, so the
    /// counter and the record of one user always belong to the same tenant.
    /// </remarks>
    /// <param name="identity">Channel identity snapshot the key is built from.</param>
    /// <returns>Normalized partition key.</returns>
    internal static string NormalizeUserKey(ChannelIdentitySnapshot identity)
        => $"{TenantKey.Segment(identity.TenantId).Trim().ToLowerInvariant()}"
           + $":{identity.ChannelType.Trim().ToLowerInvariant()}"
           + $":{identity.ChannelUserId.Trim().ToLowerInvariant()}";

    /// <summary>
    /// What a refused write of the user's decision is answered with: which terminal state to show
    /// and the text that goes with it.
    /// </summary>
    /// <param name="Outcome">Terminal state shown to the user.</param>
    /// <param name="Text">Already localized text of that state.</param>
    private readonly record struct RefusedDecisionAnswer(TransactionOutcome Outcome, string Text);

    /// <summary>
    /// The transaction a channel decision is acted on and the store its answer may take a prompt
    /// reference from.
    /// </summary>
    /// <param name="Transaction">Transaction to act on; null — there is none to answer with.</param>
    /// <param name="PromptMessageStore">Store of sent-prompt coordinates; null — the answer carries no
    /// prompt reference.</param>
    private readonly record struct DecisionTransaction(
        Transaction? Transaction,
        IChannelPromptMessageStore? PromptMessageStore);
}
