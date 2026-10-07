// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net.Mail;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Email.Abstractions;
using Veriqa.Core.ChannelAdapter.Email.Configuration;
using Veriqa.Core.ChannelAdapter.Email.Constants;
using Veriqa.Core.ChannelAdapter.Email.Domain;
using Veriqa.Core.ChannelAdapter.Email.Enums;
using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Contracts;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// Email adapter endpoints: Pull mode (SPEC-016 §4) and Push mode (SPEC-016 §5).
/// Registers:
/// <list type="bullet">
///   <item><description>GET  /auth/email/start — standalone pull form page (enter email → magic link).</description></item>
///   <item><description>POST /auth/email/start — accepts the email from the user, generates and sends the magic link.</description></item>
///   <item><description>GET  /auth/email/confirm — magic link confirmation page (does not change state).</description></item>
///   <item><description>POST /auth/email/confirm — actual transaction confirmation.</description></item>
///   <item><description>GET  /auth/email/push/compose — compose-helper page of Push mode (mailto + "Sign in via email link" link).</description></item>
///   <item><description>POST /api/channels/email/inbound — accepts an incoming email from the inbound provider.</description></item>
///   <item><description>POST /api/channels/email/inbound/{tenant} — the same, with the tenant stated by the route segment (tenant demux).</description></item>
/// </list>
/// </summary>
public static partial class EmailAuthEndpoints
{
    /// <summary>
    /// Registers the Email Pull mode (magic link) and Push mode (the one-tap email) endpoints.
    /// </summary>
    /// <param name="endpoints">Endpoint router.</param>
    /// <param name="rateLimitPolicyName">Rate limiting policy name for the email start endpoint (null — no limiting).</param>
    /// <returns>The router for chaining.</returns>
    public static IEndpointRouteBuilder MapEmailAuthEndpoints(
        this IEndpointRouteBuilder endpoints,
        string? rateLimitPolicyName = null)
    {
        // GET /auth/email/start — standalone pull form page (with rate limiting, as it is user-facing)
        var pullFormEndpoint = endpoints.MapGet(EmailAdapterConstants.PullStartPath, (Delegate)HandleEmailPullFormAsync);
        if (!string.IsNullOrEmpty(rateLimitPolicyName))
        {
            pullFormEndpoint.RequireRateLimiting(rateLimitPolicyName);
        }

        // Register POST /auth/email/start with rate limiting (spam protection)
        var startEndpoint = endpoints.MapPost(EmailAdapterConstants.PullStartPath, (Delegate)HandleEmailStartAsync);
        if (!string.IsNullOrEmpty(rateLimitPolicyName))
        {
            startEndpoint.RequireRateLimiting(rateLimitPolicyName);
        }

        endpoints.MapGet(EmailAdapterConstants.PullConfirmPath, (Delegate)HandleEmailConfirmGetAsync);
        endpoints.MapPost(EmailAdapterConstants.PullConfirmPath, (Delegate)HandleEmailConfirmPostEndpointAsync);

        // Push mode: compose-helper page (with rate limiting, as it is user-facing) and inbound webhook.
        // The inbound webhook is NOT limited by the user-facing rate limiting policy —
        // it is called by the inbound provider and is protected by a secret header.
        var composeEndpoint = endpoints.MapGet(EmailAdapterConstants.PushComposePath, (Delegate)HandleEmailPushComposeAsync);
        if (!string.IsNullOrEmpty(rateLimitPolicyName))
        {
            composeEndpoint.RequireRateLimiting(rateLimitPolicyName);
        }

        // The inbound route is mapped twice from ONE call: the base path (default tenant, behaviour
        // unchanged) and the path with the optional {tenant} segment — the tenant demux every channel
        // webhook route of the product goes through (CA-167/CA-168). The mapping and the extraction
        // of the segment come from the shared helper rather than from a second route literal here:
        // a channel-local copy would be a second demux, free to answer a segment differently.
        ChannelTenantDemux.MapWithOptionalTenant(
            endpoints,
            HttpMethods.Post,
            EmailAdapterConstants.InboundWebhookPath,
            (Delegate)HandleEmailInboundWebhookAsync,
            rateLimitPolicyName: null);

        return endpoints;
    }

    /// <summary>
    /// The single guarded entry point to the Email settings for every handler of the adapter
    /// (SPEC-012 CFG-210, SPEC-016 EM-054).
    /// <para>
    /// <see cref="IOptions{TOptions}"/> binds and validates its section at the FIRST read rather than
    /// at start-up, and nothing on the start-up path reads this one. A deployment-side edit that breaks
    /// <c>Veriqa:Channels:Email:*</c> on a running host therefore lands on whichever handler reads it first —
    /// and a guard placed on one handler alone does not survive, because a neighbouring handler on the
    /// same route materializes the broken section before it. The read is a whole bind-and-validate pass:
    /// a validation rule reports an <see cref="OptionsValidationException"/>, while a value that does not
    /// convert to its declared property type fails earlier, inside the binder, as an
    /// <see cref="InvalidOperationException"/> — so the guard is as wide as the read.
    /// </para>
    /// <para>
    /// There is no earlier snapshot to serve here (<see cref="IOptions{TOptions}"/> keeps exactly one),
    /// so the degradation is the channel-unavailable answer each caller already has for a channel it
    /// cannot serve — never an exception escaping into a pipeline that has no exception handler.
    /// </para>
    /// </summary>
    /// <param name="services">Request service provider.</param>
    /// <param name="logger">Logger of the degradation fact.</param>
    /// <param name="transactionId">Transaction of the request, when it is already known at the read.</param>
    /// <returns>The Email settings, or <see langword="null"/> when the section could not be read.</returns>
    private static EmailOptions? TryReadEmailOptions(
        IServiceProvider services,
        ILogger logger,
        TransactionId? transactionId = null)
    {
        try
        {
            return services.GetRequiredService<IOptions<EmailOptions>>().Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Email settings could not be read, so the Email channel cannot be served for this request. TransactionId: {TransactionId}",
                transactionId);

            return null;
        }
    }

    /// <summary>
    /// Handles the Pull flow start request:
    /// accepts email and session_id, generates an action token, sends the magic link.
    /// </summary>
    private static async Task<IResult> HandleEmailStartAsync(HttpContext httpContext)
    {
        // The method handles the user's email input and triggers sending the magic link

        var services = httpContext.RequestServices;
        var cancellationToken = httpContext.RequestAborted;
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(EmailAuthEndpoints));

        // API error localization (ICC-050): the { success, error } JSON keeps its shape; only the error
        // text follows the request language (Accept-Language), degrading to English when unavailable.
        var pageLocalizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
        var servedLocale = ChannelRequestLocale.Detect(httpContext.Request.Headers.AcceptLanguage.ToString());

        // Read the parameters from the body (form or JSON)
        string? rawEmail;
        string? sessionId;

        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            rawEmail = form[EmailStartFormFields.Email];
            sessionId = form[EmailAdapterConstants.SessionIdQueryParam];
        }
        else if (httpContext.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) is true)
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(
                httpContext.Request.Body, cancellationToken: cancellationToken);

            rawEmail = body.RootElement.TryGetProperty(EmailStartFormFields.Email, out var emailEl)
                && emailEl.ValueKind is System.Text.Json.JsonValueKind.String
                ? emailEl.GetString()
                : null;

            sessionId = body.RootElement.TryGetProperty(EmailAdapterConstants.SessionIdQueryParam, out var sidEl)
                && sidEl.ValueKind is System.Text.Json.JsonValueKind.String
                ? sidEl.GetString()
                : null;
        }
        else
        {
            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorUnsupportedContentType, servedLocale) },
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        // Validate the email
        if (string.IsNullOrWhiteSpace(rawEmail) || !IsValidEmail(rawEmail))
        {
            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorInvalidEmail, servedLocale) },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Validate session_id
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorMissingSessionId, servedLocale) },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Get transactionId from session_id
        var transactionId = ParseTransactionId(sessionId);
        if (transactionId is null)
        {
            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorInvalidSessionId, servedLocale) },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Check the Email adapter configuration.
        // Enabled — the channels_enabled axis (SPEC-003 §17.7, CA-169/CA-170), PullEnabled and
        // Normalization — core-transport (CFG-230): both stay global by ownership, not by omission.
        // The read goes through the adapter's guarded entry point, which turns an unreadable section
        // into the channel-unavailable answer instead of an exception (see TryReadEmailOptions).
        var emailOptions = TryReadEmailOptions(services, logger, transactionId);
        if (emailOptions is null)
        {
            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorChannelUnavailable, servedLocale) },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!emailOptions.Enabled || !emailOptions.PullEnabled)
        {
            logger.LogWarning(
                "Email Pull flow requested but the adapter or Pull mode is disabled. TransactionId: {TransactionId}",
                transactionId);
            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorChannelUnavailable, servedLocale) },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Normalize the email according to the normalization settings (EM-041–EM-043) —
        // the same logic as in the Push branch, so that Pull/Push identities do not diverge.
        // This one setting is read live while the guard above stays on the IOptions snapshot, which is
        // fixed at its first read, and
        // the split is deliberate: normalization decides which identity an address maps to and the
        // Push path (DefaultEmailInboundProcessor) reads it from the monitor, whereas the channel
        // mode is not hot-switchable at all (SPEC-016 EM-054) — the channel service composition is
        // fixed at startup, so a mode changed under a running host would let a request through to
        // services that do not exist.
        // A reload that makes the Email settings invalid must not turn this live read into a 500 for the
        // user: the monitor re-binds the section and re-runs the validator, and the core contour has no
        // exception-handler middleware. Degrade to the snapshot the guard above already runs on, so the
        // request keeps the identity mapping it had before the broken edit. The guard is as wide as the
        // read: a value that does not convert to its declared property type fails inside the binder,
        // before any validation rule runs, so catching only the validation half would leave the 500 in.
        EmailNormalizationOptions normalization;
        try
        {
            normalization = services.GetRequiredService<IOptionsMonitor<EmailOptions>>().CurrentValue.Normalization;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Email settings could not be read after a reload; normalizing on the last valid settings.");

            normalization = emailOptions.Normalization;
        }

        var normalizedEmail = EmailAddressHelper.Normalize(rawEmail, normalization);

        // Check that the transaction exists
        var transactionService = services.GetRequiredService<ITransactionService>();
        var txResult = await transactionService.GetTransactionAsync(transactionId.Value, cancellationToken);

        if (txResult.IsFailure)
        {
            logger.LogWarning(
                "Transaction not found for the Email start request. SessionId: {SessionId}",
                sessionId);
            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorSessionInvalid, servedLocale) },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var transaction = txResult.Value;

        // The tenant of THIS sign-in is stated by the transaction, and every credential read below —
        // the outbound sender, the public base URL, the token lifetime — is made for it (CA-162/CA-164).
        // The scope opens before the first of those reads and closes with the request; a transaction
        // that states no tenant opens it for the default implicit one, which is the self-hosted N=1
        // behaviour unchanged.
        using var tenantScope = ChannelTenantContext.BeginScope(transaction.GetTenantId());

        // Check that the transaction is in the Pending state
        if (transaction.State is not TransactionState.Pending)
        {
            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorSessionAlreadyProcessed, servedLocale) },
                statusCode: StatusCodes.Status409Conflict);
        }

        // Check that the Email channel is allowed for this transaction
        if (transaction.AllowedChannelTypes.Count > 0
            && !transaction.AllowedChannelTypes.Contains(ChannelTypes.Email))
        {
            logger.LogWarning(
                "The Email channel is not allowed for the transaction. TransactionId: {TransactionId}, AllowedChannels: {AllowedChannels}",
                transactionId,
                string.Join(", ", transaction.AllowedChannelTypes));
            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorChannelNotAllowedForSession, servedLocale) },
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Resolve the tenant-credential group through the canonical resolver (CA-162): PublicBaseUrl, Outbound,
        // Inbound and TokenTtl belong to the tenant level; for N=1 the resolver returns the core
        // level from the same IOptionsMonitor — behavior 1:1 with the previous global read.
        // It is resolved AFTER the transaction is loaded, because the transaction is what names the
        // tenant these credentials belong to — resolving earlier would read the core level regardless
        // of whose sign-in this is.
        var configurationResolver = services.GetRequiredService<IConfigurationResolver>();
        var credentialsResult = await EmailCredentialsResolver.ResolveAsync(
            configurationResolver, logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            logger.LogWarning(
                "Failed to resolve the tenant Email credentials for the Pull start request. Error: {ErrorCode}",
                credentialsResult.Error.Code);
            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorChannelUnavailable, servedLocale) },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var credentials = credentialsResult.Value;

        // Generate an action token (the adapter's shared URL-safe token generator)
        var token = GenerateUrlSafeToken();

        // TokenTtl — tenant level with the core default as fallback (SPEC-003 §17.3, CFG-220 semantics)
        var tokenExpiresAt = services.GetRequiredService<TimeProvider>().GetUtcNow().Add(
            EmailCredentialsResolver.ResolveTokenTtl(credentials, emailOptions.TokenTtl));

        // Store the token in the store
        var tokenStore = services.GetRequiredService<IEmailActionTokenStore>();
        var tokenData = new EmailActionToken(
            TransactionId: transactionId.Value.ToString(),
            NormalizedEmail: normalizedEmail,
            ExpiresAt: tokenExpiresAt);

        await tokenStore.StoreTokenAsync(token, tokenData, cancellationToken);

        // Send the magic link by email
        var sender = services.GetRequiredService<IEmailOutboundSender>();

        // The client application name in the email (SPEC-017, ICC-010): DisplayName from the
        // transaction initiator context; fallback — the product name if context capture is disabled
        var clientName = transaction.InitiatorContextSnapshot?.ClientApplicationName
            ?? EmailAdapterConfigDefaults.DefaultClientName;

        // Recipient locale for the email = the language of the request that started the Pull flow
        // (SPEC-017 ICC-050; TASK-063 §8, assumption 4): the mail goes out before confirmation, so
        // ChannelIdentitySnapshot.Locale does not exist yet, and the recipient is in practice the
        // same person who opened the auth window. When Accept-Language is unavailable, fall back to
        // the initiation-page language stored in the transaction (recipient-locale chain, B17);
        // null after both — the base language (English).
        var recipientLocale = ChannelRequestLocale.Detect(
            httpContext.Request.Headers.AcceptLanguage.ToString())
            ?? transaction.GetUiLocale();

        // The zone has one source and no chain of its own: the transaction, where the relying party's
        // value or the deployment's default was written at creation. A browser states none — the
        // request carries preferred LANGUAGES and nothing about the zone the reader lives in.
        var recipientTimeZone = transaction.GetUiTimeZone();

        // Initiator context for the email (SPEC-017, delta of SPEC-016 §9.3): built by the pipeline
        // factory from the transaction and handed to the mail AS A CONTEXT — the mail is a confirmation
        // text of its own, so the initiator slots are declared and worded by its own message, and a
        // context without details leaves them unfilled. The detected locale is handed to the factory,
        // which puts it on the context — the single carrier from there on.
        var promptContextFactory = services.GetRequiredService<IConfirmationPromptContextFactory>();
        var promptContext = await promptContextFactory.CreateAsync(
            transaction,
            ChannelTypes.Email,
            normalizedEmail,
            recipientLocale,
            cancellationToken);

        var message = new EmailLoginMessage(
            ToAddress: rawEmail.Trim(),
            ClientName: clientName,
            ActionToken: token,
            ExpiresAt: tokenExpiresAt,
            PublicBaseUrl: credentials.PublicBaseUrl,
            Locale: recipientLocale)
        {
            // The ownership travels with the send: the mail is worded by a levelled message, and the
            // context the prompt factory assembled for THIS transaction is the address that wording is
            // resolved at (SPEC-036 TPL-116).
            Ownership = promptContext.Ownership,
            InitiatorContext = promptContext,
            TimeZone = recipientTimeZone
        };

        var sendResult = await sender.SendLoginEmailAsync(message, cancellationToken);

        if (!sendResult.Success)
        {
            logger.LogError(
                "Failed to send the magic link email. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transactionId,
                sendResult.ErrorCode);

            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorSendFailed, servedLocale) },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // Publish the email-specific intermediate "sent" status (SPEC-016 §10.1) —
        // additively, without changing the transaction lifecycle state
        await PublishEmailChannelStatusAsync(
            services, transactionId.Value, EmailChannelStatuses.PullSent, logger);

        var maskedEmail = EmailAddressHelper.Mask(normalizedEmail);

        return Results.Json(new { success = true, maskedEmail });
    }

    /// <summary>
    /// Publishes an email-specific intermediate transaction status via
    /// <see cref="ITransactionEventPublisher"/> (SPEC-016 §10.1).
    /// The status describes a fact that has already happened (email sent/received, etc.),
    /// so publication uses <see cref="CancellationToken.None"/> — a request abort
    /// must not lose the notification. A publication failure does not interrupt the main flow.
    /// </summary>
    /// <param name="services">The request service provider.</param>
    /// <param name="transactionId">The transaction identifier.</param>
    /// <param name="channelStatus">The email-specific status code.</param>
    /// <param name="logger">The logger.</param>
    internal static Task PublishEmailChannelStatusAsync(
        IServiceProvider services,
        TransactionId transactionId,
        string channelStatus,
        ILogger logger)
    {
        // The method additively notifies the UI about an intermediate step of the email flow.
        // The moment of the event comes from the registered clock, the same one the rest of the
        // adapter reads, so a test that moves time moves this stamp with it.
        return PublishEmailEventAsync(
            services,
            new TransactionChannelStatusChangedEvent
            {
                TransactionId = transactionId,
                OccurredAt = services.GetRequiredService<TimeProvider>().GetUtcNow(),
                ChannelType = ChannelTypes.Email,
                ChannelStatus = channelStatus
            },
            channelStatus,
            logger);
    }

    /// <summary>
    /// The single point for publishing Email channel events (statuses and audit) via
    /// <see cref="ITransactionEventPublisher"/>. Events describe facts that have happened:
    /// publication uses <see cref="CancellationToken.None"/>, and a failure does not interrupt
    /// the main flow (a warning is logged).
    /// </summary>
    /// <remarks>
    /// Intentionally email-private (review feedback): once a second channel with
    /// intermediate statuses appears, this publication (together with the swallow-and-log
    /// policy and deduplication) should be lifted into a shared channel pipeline layer,
    /// rather than copying the helper into the new channel's adapter.
    /// </remarks>
    /// <param name="services">The request service provider.</param>
    /// <param name="transactionEvent">The event to publish.</param>
    /// <param name="eventLabel">The status/event code for the log message.</param>
    /// <param name="logger">The logger.</param>
    private static async Task PublishEmailEventAsync(
        IServiceProvider services,
        TransactionEvent transactionEvent,
        string eventLabel,
        ILogger logger)
    {
        // The method publishes the event without letting a publication failure break authentication
        var eventPublisher = services.GetService<ITransactionEventPublisher>();
        if (eventPublisher is null)
        {
            return;
        }

        try
        {
            await eventPublisher.PublishAsync(transactionEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to publish Email channel event {EventLabel}. TransactionId: {TransactionId}",
                eventLabel,
                transactionEvent.TransactionId);
        }
    }

    /// <summary>
    /// Renders the magic link confirmation page (GET — does not change transaction state).
    /// When the effective confirmation surface is <see cref="ConfirmationSurface.None"/>
    /// (SPEC-012 §4.4.2), it automatically completes the transaction when the link
    /// is followed, without an additional user action (fast link).
    /// </summary>
    private static async Task<IResult> HandleEmailConfirmGetAsync(
        HttpContext httpContext,
        string? token)
    {
        // Served page localization (ICC-050): the confirmation page and its status/error texts render
        // in the request language (Accept-Language); a missing translation degrades to English (the key).
        var pageLocalizer = httpContext.RequestServices.GetRequiredService<IConfirmationPromptLocalizer>();
        var servedLocale = ChannelRequestLocale.Detect(httpContext.Request.Headers.AcceptLanguage.ToString());

        if (string.IsNullOrWhiteSpace(token))
        {
            // No token — no transaction to name an ownership context, so the page is branded by the
            // core level alone (SPEC-007 UI-090).
            var noTokenBranding = await ResolvePageBrandingAsync(
                httpContext.RequestServices, transactionIdValue: null, httpContext.RequestAborted);
            var errorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, noTokenBranding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageTokenNotFound);
            return Results.Content(errorHtml, "text/html", Encoding.UTF8);
        }

        var tokenStore = httpContext.RequestServices.GetRequiredService<IEmailActionTokenStore>();
        var tokenData = await tokenStore.GetTokenAsync(token, httpContext.RequestAborted);

        // The action token carries the transaction, so every page below — the confirmation itself and
        // each of its error states — is branded for that transaction's application and ui_config
        // record (SPEC-007 UI-101). A token that names none leaves the core level alone (UI-090).
        var branding = await ResolvePageBrandingAsync(
            httpContext.RequestServices, tokenData?.TransactionId, httpContext.RequestAborted);

        string html;

        if (tokenData is null)
        {
            html = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageTokenNotFound);
        }
        else if (tokenData.IsUsed)
        {
            html = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageTokenUsed);
        }
        else if (tokenData.IsExpired(httpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()))
        {
            html = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageTokenExpired);
        }
        else if (await TryHandOverConfirmationAsync(
            httpContext, tokenData, pageLocalizer, servedLocale, branding) is { } handedOver)
        {
            // A confirmation transaction is handed over to the core page BEFORE the fast link is
            // considered: a confirmation whose effective surface resolves to None would otherwise end
            // right here, on a GET, with its subject never shown (SPEC-039 E29).
            return handedOver;
        }
        else if (await IsEmailAutoConfirmAsync(httpContext, tokenData))
        {
            // Effective Email surface = None: GET automatically completes the transaction
            // (fast link, SPEC-012 §4.4.2 / SPEC-016 §4.4). The usage stays in the operational log
            // and is not published as a channel audit event: that event type carries no outcome of
            // its own, so the audit journal records the whole class as a failure — an informational
            // fact would read as one (SPEC-016 EM-176, SPEC-011 N37). We delegate to the POST
            // handler — the single completion point.
            var autoConfirmLogger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(EmailAuthEndpoints));
            autoConfirmLogger.LogInformation(
                "Email confirm: effective surface is None — GET completes the transaction without a confirming action. TransactionId: {TransactionId}",
                tokenData.TransactionId);

            return await HandleEmailConfirmPostAsync(httpContext, token);
        }
        else
        {
            // Continue in the language the sign-in page was rendered in, not in the language of the
            // mail client's browser (see ResolveStoredLocaleAsync).
            servedLocale = await ResolveStoredLocaleAsync(httpContext, tokenData, servedLocale);

            // The user opened the email and followed the link — publish the "opened" status
            // (SPEC-016 §10.1) only on the first open: repeat views and prefetch by
            // mail scanners do not duplicate the signal. The token is not consumed here (EM-010).
            var openedFirstTime = await tokenStore.TryMarkOpenedAsync(token, httpContext.RequestAborted);
            if (openedFirstTime
                && TransactionId.TryParse(tokenData.TransactionId, out var openedTransactionId))
            {
                var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(EmailAuthEndpoints));
                await PublishEmailChannelStatusAsync(
                    httpContext.RequestServices,
                    openedTransactionId,
                    EmailChannelStatuses.PullOpened,
                    logger);
            }

            html = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: false,
                token: token);
        }

        return Results.Content(html, "text/html", Encoding.UTF8);
    }

    /// <summary>
    /// Prefers the language the sign-in page was rendered in — stored on the transaction as
    /// <c>OidcContext.UiLocale</c> — over the Accept-Language of the current request.
    /// </summary>
    /// <remarks>
    /// The magic link is opened from a mail client, whose browser may send a different Accept-Language
    /// than the browser that started the sign-in; detecting from the header alone then renders this page
    /// in another language than the sign-in window. The stored locale also carries an explicit
    /// <c>ui_locales</c> choice made on the RP side, which no header reflects. The action token has no
    /// locale of its own but does carry the transaction id, so the stored locale is reachable. An
    /// unreadable transaction is not an error here — the page degrades to the header detection, the same
    /// fallback as a request without Accept-Language.
    /// </remarks>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="tokenData">The data of a valid action token.</param>
    /// <param name="acceptLanguageLocale">Locale detected from Accept-Language (the fallback).</param>
    /// <returns>The locale to render the page in, or null for the base language.</returns>
    private static async Task<string?> ResolveStoredLocaleAsync(
        HttpContext httpContext,
        EmailActionToken tokenData,
        string? acceptLanguageLocale)
    {
        if (!TransactionId.TryParse(tokenData.TransactionId, out var transactionId))
        {
            return acceptLanguageLocale;
        }

        var transactionService = httpContext.RequestServices.GetRequiredService<ITransactionService>();
        var txResult = await transactionService.GetTransactionAsync(
            transactionId, httpContext.RequestAborted);

        if (txResult.IsFailure)
        {
            return acceptLanguageLocale;
        }

        var uiLocale = txResult.Value.GetUiLocale();
        return string.IsNullOrWhiteSpace(uiLocale) ? acceptLanguageLocale : uiLocale;
    }

    /// <summary>
    /// Loads the transaction by the action token and determines whether auto-confirmation (fast link)
    /// is needed based on the effective confirmation surface. If the transaction is
    /// unavailable — the safe default is <c>false</c> (confirmation page), so that GET does not
    /// implicitly complete the sign-in.
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="tokenData">The data of a valid action token.</param>
    /// <returns><c>true</c> — auto-confirmation; <c>false</c> — confirmation page.</returns>
    private static async Task<bool> IsEmailAutoConfirmAsync(HttpContext httpContext, EmailActionToken tokenData)
    {
        if (!TransactionId.TryParse(tokenData.TransactionId, out var transactionId))
        {
            return false;
        }

        var transactionService = httpContext.RequestServices.GetRequiredService<ITransactionService>();
        var txResult = await transactionService.GetTransactionAsync(
            transactionId, httpContext.RequestAborted);

        if (txResult.IsFailure)
        {
            return false;
        }

        return await ShouldAutoConfirmAsync(
            httpContext.RequestServices, txResult.Value, httpContext.RequestAborted);
    }

    /// <summary>
    /// Determines whether a GET on the magic link should auto-confirm the transaction (fast link).
    /// Derived from the effective confirmation surface (SPEC-012 §4.4.2):
    /// <c>None</c> → auto-confirmation (fast link); otherwise (<c>OnWebPage</c> — the Email default,
    /// or a clamped <c>InChannel</c>) → confirm page (GET does not consume the token, EM-010/011).
    /// Returns <c>true</c> ⇔ the effective Email surface = <c>None</c>.
    /// </summary>
    /// <param name="services">The request service provider.</param>
    /// <param name="transaction">The transaction (the resolution context source).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — auto-confirmation via fast link; <c>false</c> — confirmation page.</returns>
    private static async ValueTask<bool> ShouldAutoConfirmAsync(
        IServiceProvider services,
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        var surfaceResolver = services.GetRequiredService<IEffectiveConfirmationSurfaceResolver>();
        var emailAdapter = services.GetRequiredService<IEnumerable<IChannelAdapter>>()
            .FirstOrDefault(a => string.Equals(a.ChannelType, ChannelTypes.Email, StringComparison.Ordinal));

        // The Email adapter is not registered — safe default: do NOT auto-confirm
        // (show the confirmation page, in the spirit of the method's safe default when unavailable).
        if (emailAdapter is null)
        {
            return false;
        }

        var surface = await surfaceResolver.ResolveEffectiveSurfaceAsync(
            transaction, emailAdapter, cancellationToken);
        return surface is ConfirmationSurface.None;
    }

    /// <summary>
    /// Handles the answer posted from the served confirmation page: "Yes" confirms the transaction,
    /// "No" declines it. Both answers arrive on the same path and are told apart by the answer field.
    /// </summary>
    private static async Task<IResult> HandleEmailConfirmPostEndpointAsync(HttpContext httpContext)
    {
        var token = await ExtractTokenAsync(httpContext);

        // A confirmation is decided on the core page and nowhere else (SPEC-039 R36): this route
        // answers for sign-ins. Neither the transaction nor the token is touched — the request is
        // refused with the page's existing error wording, and no new text is introduced for it.
        if (await IsConfirmationTransactionTokenAsync(httpContext, token))
        {
            var services = httpContext.RequestServices;
            services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(EmailAuthEndpoints))
                .LogWarning(
                    "Email confirm POST addresses a confirmation transaction; the answer is refused — "
                    + "a confirmation is decided on the core page, where its subject is shown.");

            var pageLocalizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
            var servedLocale = ChannelRequestLocale.Detect(
                httpContext.Request.Headers.AcceptLanguage.ToString());

            // The core level alone brands it: naming the transaction of the token would make the look
            // of a refusal a readable state of somebody else's transaction.
            var branding = await ResolvePageBrandingAsync(
                services, transactionIdValue: null, httpContext.RequestAborted);

            var refusedHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageError);

            return Results.Content(refusedHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status400BadRequest);
        }

        if (await IsDeclineAnswerAsync(httpContext))
        {
            return await HandleEmailDeclineAsync(httpContext, token);
        }

        return await HandleEmailConfirmPostAsync(httpContext, token);
    }

    /// <summary>
    /// Reads the answer field and reports whether the user declined. Anything other than the decline
    /// value — including no field at all — is a confirmation, so the fast link (which posts only the
    /// token) is unaffected.
    /// </summary>
    private static async Task<bool> IsDeclineAnswerAsync(HttpContext httpContext)
    {
        if (!httpContext.Request.HasFormContentType)
        {
            return false;
        }

        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        var answer = form[EmailAdapterConstants.AnswerFormField].FirstOrDefault();

        return string.Equals(answer, EmailAdapterConstants.AnswerDecline, StringComparison.Ordinal);
    }

    /// <summary>
    /// Handles the "No" answer on the served confirmation page: validates the action token, moves the
    /// transaction to its terminal declined state and consumes the token.
    /// </summary>
    /// <remarks>
    /// The refusal uses the same reason code as an in-channel decline
    /// (<c>TransactionErrorCodes.DeclinedByUser</c> in <c>ChannelWebhookPipeline.HandleAuthDeclineAsync</c>):
    /// one meaning, one code, one audit trail. Unlike the confirmation there is nothing to report back to
    /// the client from here — this page is opened from a mail client, not from the browser that started
    /// the sign-in; that browser learns the outcome from the transaction state it is already watching.
    /// The token is consumed either way: a declined link must not be replayable.
    /// </remarks>
    private static async Task<IResult> HandleEmailDeclineAsync(HttpContext httpContext, string? token)
    {
        var services = httpContext.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(EmailAuthEndpoints));

        var pageLocalizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
        var servedLocale = ChannelRequestLocale.Detect(httpContext.Request.Headers.AcceptLanguage.ToString());

        if (string.IsNullOrWhiteSpace(token))
        {
            // No token — no transaction to name an ownership context (SPEC-007 UI-090).
            var noTokenBranding = await ResolvePageBrandingAsync(
                services, transactionIdValue: null, httpContext.RequestAborted);
            var errorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, noTokenBranding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageTokenNotFound);
            return Results.Content(errorHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status400BadRequest);
        }

        var tokenStore = services.GetRequiredService<IEmailActionTokenStore>();
        var tokenData = await tokenStore.GetTokenAsync(token, httpContext.RequestAborted);

        // Branded for the transaction the action token names (SPEC-007 UI-101); a token that names
        // none leaves the core level alone (UI-090).
        var branding = await ResolvePageBrandingAsync(
            services, tokenData?.TransactionId, httpContext.RequestAborted);

        if (tokenData is null
            || tokenData.IsUsed
            || tokenData.IsExpired(services.GetRequiredService<TimeProvider>().GetUtcNow()))
        {
            var errorMessage = tokenData switch
            {
                null => EmailAdapterMailStrings.ConfirmPageTokenNotFound,
                { IsUsed: true } => EmailAdapterMailStrings.ConfirmPageTokenUsed,
                _ => EmailAdapterMailStrings.ConfirmPageTokenExpired
            };

            var statusCode = tokenData switch
            {
                null => StatusCodes.Status400BadRequest,
                { IsUsed: true } => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status410Gone
            };

            var errorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: errorMessage);
            return Results.Content(errorHtml, "text/html", Encoding.UTF8, statusCode);
        }

        servedLocale = await ResolveStoredLocaleAsync(httpContext, tokenData, servedLocale);

        if (!TransactionId.TryParse(tokenData.TransactionId, out var transactionId))
        {
            logger.LogWarning(
                "Email decline: invalid TransactionId in the action token: {TransactionId}",
                tokenData.TransactionId);

            var errorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageError);
            return Results.Content(errorHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status400BadRequest);
        }

        // CancellationToken.None: the refusal must be recorded even if the client goes away mid-request.
        var transactionService = services.GetRequiredService<ITransactionService>();
        var txResult = await transactionService.GetTransactionAsync(transactionId, CancellationToken.None);

        if (txResult.IsFailure)
        {
            var errorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageTokenExpired);
            return Results.Content(errorHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status410Gone);
        }

        var failResult = await transactionService.FailTransactionAsync(
            transactionId,
            TransactionErrorCodes.DeclinedByUser,
            txResult.Value.ConcurrencyToken,
            CancellationToken.None);

        if (failResult.IsFailure)
        {
            logger.LogWarning(
                "Email decline: failed to move the transaction to Failed. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                tokenData.TransactionId,
                failResult.Error.Code);

            var errorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageError);
            return Results.Content(errorHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status409Conflict);
        }

        // The link is spent regardless of the answer — a "No" must not leave a replayable magic link.
        await tokenStore.MarkUsedAsync(token, CancellationToken.None);

        logger.LogInformation(
            "Email sign-in declined by the user on the confirmation page. TransactionId: {TransactionId}",
            tokenData.TransactionId);

        // Same inversion as on the confirmation branch: the wording is resolved first, the page
        // renders it. The transaction type comes from the transaction read before the refusal.
        var declinedText = await services.GetRequiredService<IOutcomeReceiptText>().RenderAsync(
            new OutcomeReceiptAddress(
                TransactionOutcome.Declined,
                OutcomeReceiptSurfaces.EmailConfirmPage,
                txResult.Value.Type,
                txResult.Value.ConfirmationSnapshot?.ActionType,
                ChannelTypes.Email),
            servedLocale,
            // The zone the transaction states, beside the locale the page is served in. The moment of
            // the outcome is the one the refusal was written down at, read off the instance that write
            // returned.
            txResult.Value.GetUiTimeZone(),
            failResult.Value.UpdatedAt,
            // The values of the transaction, as every surface that words a receipt passes them
            // (SPEC-036 TPL-123, TPL-124). In today's delivery its caller values are empty: a
            // confirmation is refused on this page and decided on the core page (SPEC-039 R36), and a
            // sign-in states no caller values.
            TransactionSlotSource.From(txResult.Value),
            TransactionResolutionContext.For(txResult.Value),
            CancellationToken.None);

        var declinedHtml = BuildConfirmPageHtml(
            pageLocalizer, servedLocale, branding, isDeclined: true, outcomeText: declinedText);
        return Results.Content(declinedHtml, "text/html", Encoding.UTF8);
    }

    /// <summary>
    /// Builds the Email identity snapshot of a valid magic-link token (SPEC-016 §6.1) — the identity
    /// the transaction is answered by, whether the answer is given here or on the core page.
    /// </summary>
    /// <remarks>
    /// One composition for both, because it IS one identity: a snapshot assembled twice would be two
    /// descriptions of the same mailbox, free to drift in a field the resolution of identity reads.
    /// The metadata carries Outbound.Provider — a tenant-credential field, hence the resolver (CA-162) —
    /// and a resolution failure must not break an otherwise valid answer: it is best-effort diagnostics
    /// (CA-005 already allows dropping it), so it degrades to null.
    /// CancellationToken.None: from here on the user has already acted, and the identity must be built
    /// even if the browser goes away. The seam reports cancellation by throwing rather than by a failed
    /// Result, so RequestAborted here would bypass that degradation and abort over missing diagnostics.
    /// </remarks>
    /// <param name="services">The request service provider.</param>
    /// <param name="tokenData">The data of a valid action token.</param>
    /// <param name="tenantId">Tenant of the transaction; null — the default implicit tenant.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The Email identity snapshot.</returns>
    private static async Task<ChannelIdentitySnapshot> BuildPullIdentitySnapshotAsync(
        IServiceProvider services,
        EmailActionToken tokenData,
        string? tenantId,
        ILogger logger)
    {
        var credentialsResult = await EmailCredentialsResolver.ResolveAsync(
            services.GetRequiredService<IConfigurationResolver>(),
            logger,
            CancellationToken.None);

        if (credentialsResult.IsFailure)
        {
            logger.LogWarning(
                "Failed to resolve the tenant Email credentials for the Pull confirm metadata; "
                + "the confirmation proceeds without RawMetadata. Error: {ErrorCode}",
                credentialsResult.Error.Code);
        }

        return new ChannelIdentitySnapshot
        {
            TenantId = tenantId,
            ChannelType = ChannelTypes.Email,
            IsBot = false,
            ChannelUserId = tokenData.NormalizedEmail,
            DisplayName = tokenData.NormalizedEmail,
            Username = EmailAddressHelper.GetLocalPart(tokenData.NormalizedEmail),
            Email = tokenData.NormalizedEmail,
            // Pull proves control over the mailbox via the magic link
            EmailVerified = true,
            CapturedAt = services.GetRequiredService<TimeProvider>().GetUtcNow(),
            AdapterVersion = EmailAdapterConstants.AdapterVersion,
            RawMetadata = credentialsResult.IsSuccess
                ? BuildPullRawMetadata(credentialsResult.Value)
                : null,
            AdditionalClaims = BuildEmailAdditionalClaims(EmailAdapterConstants.ModePull)
        };
    }

    /// <summary>
    /// Hands a CONFIRMATION transaction over to the core page that asks its subject (SPEC-039 E29),
    /// attaching the mailbox identity on the way. Returns null when the token names no such
    /// transaction — every other kind goes on through the remaining branches of the caller.
    /// </summary>
    /// <remarks>
    /// This adapter asks no question of its own about a confirmation: it carries no in-channel
    /// confirmation surface, and its own page words a sign-in (E28). Following the magic link is
    /// therefore the user ARRIVING and not deciding — the decision is taken on the one surface that
    /// shows what is being decided.
    /// <para>
    /// The token is deliberately NOT spent here: it carries no decision any more (the second point of
    /// consent on its POST is closed by the type guard, not by the state of the token), and spending
    /// it would cost the user the way back to the question after closing the tab.
    /// </para>
    /// </remarks>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="tokenData">The data of a valid action token.</param>
    /// <param name="pageLocalizer">Served page localizer.</param>
    /// <param name="servedLocale">Locale the served page renders in.</param>
    /// <param name="branding">Branding of the served page.</param>
    /// <returns>The response handing the user over, or null when this is not a confirmation.</returns>
    private static async Task<IResult?> TryHandOverConfirmationAsync(
        HttpContext httpContext,
        EmailActionToken tokenData,
        IConfirmationPromptLocalizer pageLocalizer,
        string? servedLocale,
        CorePageBranding? branding)
    {
        var services = httpContext.RequestServices;

        if (!TransactionId.TryParse(tokenData.TransactionId, out var transactionId))
        {
            return null;
        }

        var transactionService = services.GetRequiredService<ITransactionService>();
        var txResult = await transactionService.GetTransactionAsync(
            transactionId, httpContext.RequestAborted);

        if (txResult.IsFailure
            || !string.Equals(txResult.Value.Type, TransactionTypes.Confirmation, StringComparison.Ordinal))
        {
            return null;
        }

        var transaction = txResult.Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EmailAuthEndpoints));

        // The identity the answer on the core page will be signed by. Attached only when there is none
        // yet: a second open of the same letter (or a prefetch by a mail scanner) finds it in place and
        // simply arrives at the same question.
        if (transaction.ChannelIdentitySnapshot is null)
        {
            // The identity below reads the tenant's Email credentials, and the tenant of this
            // confirmation is the one the transaction states — the scope opens for the snapshot and
            // closes with it (CA-164).
            using var tenantScope = ChannelTenantContext.BeginScope(transaction.GetTenantId());

            var snapshot = await BuildPullIdentitySnapshotAsync(
                services, tokenData, transaction.GetTenantId(), logger);

            // CancellationToken.None: the identity must be attached even if the browser goes away —
            // otherwise the user comes back to a question that has nobody to answer it.
            var attachResult = await transactionService.AttachChannelIdentityAsync(
                transaction.Id, snapshot, transaction.ConcurrencyToken, CancellationToken.None);

            if (attachResult.IsFailure)
            {
                // A transaction that no longer accepts this event — decided, past its TTL, a sender
                // not admitted, a parallel write — is an expected refusal here, hence Warning and not
                // Error for all of them alike. The page keeps its existing error wording: no new text
                // and no detail of the refusal, which is not this user's business.
                logger.LogWarning(
                    "Email confirm: the channel identity was not attached for the core confirmation page. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                    transaction.Id.ToString(),
                    attachResult.Error.Code);

                var errorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                    isError: true,
                    errorMessage: EmailAdapterMailStrings.ConfirmPageError);
                return Results.Content(errorHtml, "text/html", Encoding.UTF8,
                    StatusCodes.Status409Conflict);
            }
        }

        // The page lives in this same application, so the redirect carries the path base of it.
        var questionUrl =
            $"{httpContext.Request.PathBase}{EmailAdapterConstants.CoreConfirmPagePath}"
            + $"?{EmailAdapterConstants.SessionIdQueryParam}={Uri.EscapeDataString(transaction.Id.ToString())}";

        return Results.Redirect(questionUrl);
    }

    /// <summary>
    /// Answers whether this POST addresses a CONFIRMATION transaction, which this route does not
    /// decide the fate of (SPEC-039 R36, E29).
    /// </summary>
    /// <remarks>
    /// The route takes its token from the form OR from the query string, so a bare
    /// <c>POST …/confirm?token=…</c> is a complete request — and a holder of the letter (a forwarded
    /// mail, a shared mailbox, the log of a mail gateway) would confirm or DECLINE a confirmation
    /// without ever seeing what it was about. The guard stands at the branching point, before the
    /// answer is read, so it closes both answers with one decision; the sign-in path passes it
    /// untouched, which is why the shared handler below is not the place for it.
    /// </remarks>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="token">The action token of the request.</param>
    /// <returns><c>true</c> — the token names a confirmation and this route must not answer for it.</returns>
    private static async Task<bool> IsConfirmationTransactionTokenAsync(HttpContext httpContext, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var services = httpContext.RequestServices;
        var tokenData = await services.GetRequiredService<IEmailActionTokenStore>()
            .GetTokenAsync(token, httpContext.RequestAborted);

        if (tokenData is null || !TransactionId.TryParse(tokenData.TransactionId, out var transactionId))
        {
            return false;
        }

        var txResult = await services.GetRequiredService<ITransactionService>()
            .GetTransactionAsync(transactionId, httpContext.RequestAborted);

        return txResult.IsSuccess
            && string.Equals(txResult.Value.Type, TransactionTypes.Confirmation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts the action token from the request (form body → query string fallback).
    /// </summary>
    private static async Task<string?> ExtractTokenAsync(HttpContext httpContext)
    {
        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
            return form[EmailAdapterConstants.TokenQueryParam];
        }

        return httpContext.Request.Query[EmailAdapterConstants.TokenQueryParam];
    }

    /// <summary>
    /// Handles the magic link confirmation with the given token.
    /// Called both from the POST endpoint (form) and from GET when the effective surface is None
    /// (fast link).
    /// </summary>
    private static async Task<IResult> HandleEmailConfirmPostAsync(HttpContext httpContext, string? token)
    {
        var services = httpContext.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(EmailAuthEndpoints));

        // Served page localization (ICC-050): confirm-result page renders in the request language.
        var pageLocalizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
        var servedLocale = ChannelRequestLocale.Detect(httpContext.Request.Headers.AcceptLanguage.ToString());

        if (string.IsNullOrWhiteSpace(token))
        {
            // No token — no transaction to name an ownership context (SPEC-007 UI-090).
            var noTokenBranding = await ResolvePageBrandingAsync(
                services, transactionIdValue: null, httpContext.RequestAborted);
            var errorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, noTokenBranding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageTokenNotFound);
            return Results.Content(errorHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status400BadRequest);
        }

        var tokenStore = services.GetRequiredService<IEmailActionTokenStore>();
        var tokenData = await tokenStore.GetTokenAsync(token, httpContext.RequestAborted);

        // Branded for the transaction the action token names (SPEC-007 UI-101); a token that names
        // none leaves the core level alone (UI-090).
        var branding = await ResolvePageBrandingAsync(
            services, tokenData?.TransactionId, httpContext.RequestAborted);

        if (tokenData is null)
        {
            var errorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageTokenNotFound);
            return Results.Content(errorHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status400BadRequest);
        }

        if (tokenData.IsUsed)
        {
            var errorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageTokenUsed);
            return Results.Content(errorHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status409Conflict);
        }

        if (tokenData.IsExpired(services.GetRequiredService<TimeProvider>().GetUtcNow()))
        {
            var errorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageTokenExpired);
            return Results.Content(errorHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status410Gone);
        }

        // The token is valid — from here on the page speaks the language the sign-in page was
        // rendered in, not the language of the mail client's browser (see ResolveStoredLocaleAsync).
        servedLocale = await ResolveStoredLocaleAsync(httpContext, tokenData, servedLocale);

        // The identifier is read out of the mail action token — an external input, so it is parsed
        // here and handed to the pipeline already typed.
        if (!TransactionId.TryParse(tokenData.TransactionId, out var confirmTransactionId))
        {
            logger.LogWarning(
                "Email confirm: invalid TransactionId in the action token: {TransactionId}",
                tokenData.TransactionId);

            var invalidIdHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageError);
            return Results.Content(invalidIdHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status400BadRequest);
        }

        // Important: mark the token used AFTER the pipeline completes successfully.
        // On a transient error the pipeline throws an exception, and the token remains available
        // for a retry. After successful completion — the token is consumed atomically
        // via MarkUsedAsync (SPEC-016 §4.3: single use of the magic link).
        // Even if MarkUsedAsync does not run (crash), reuse is safe,
        // because the transaction is already in a terminal state (Completed) and ConfirmTransactionAsync
        // returns an error on a repeated confirmation.

        // Tenant of the transaction this page is about to confirm: the transaction is what states it
        // on a web path, where no route segment and no inbound secret can (CA-164). The scope opens
        // here, before the identity snapshot below resolves the tenant's Email credentials, and holds
        // for the rest of the request.
        // A failed read degrades to the default tenant rather than refusing: the pipeline below reads
        // the transaction again and reports the real outcome, and the tenant only separates identity
        // records and rate-limit budgets — it is not an access boundary.
        var transactionService = services.GetRequiredService<ITransactionService>();
        var tenantTxResult = await transactionService.GetTransactionAsync(
            confirmTransactionId,
            CancellationToken.None);
        var confirmTenantId = tenantTxResult.IsSuccess ? tenantTxResult.Value.GetTenantId() : null;

        using var tenantScope = ChannelTenantContext.BeginScope(confirmTenantId);

        var snapshot = await BuildPullIdentitySnapshotAsync(services, tokenData, confirmTenantId, logger);

        // Build the inbound result to pass into the pipeline
        var inboundResult = new ChannelAuthConfirmResult
        {
            TransactionId = confirmTransactionId,
            Identity = snapshot
        };

        // Get the Email adapter
        var adapters = services.GetRequiredService<IEnumerable<IChannelAdapter>>();
        var adapter = adapters.FirstOrDefault(a => a.ChannelType is ChannelTypes.Email);

        if (adapter is null)
        {
            logger.LogError(
                "Email adapter not found in DI while handling confirm. TransactionId: {TransactionId}",
                tokenData.TransactionId);

            var errorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageError);
            return Results.Content(errorHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status500InternalServerError);
        }

        var identityResolutionService = services.GetRequiredService<IIdentityResolutionService>();
        var userAuthRateLimiter = services.GetService<IUserAuthRateLimiter>();
        // Required, unlike the rate limiter above: the pipeline counts every failed user notification,
        // and an absent instrument would leave this path silently uncounted.
        var metrics = services.GetRequiredService<ChannelAdapterMetrics>();

        // Process through the pipeline (transaction confirmation and completion)
        // CancellationToken.None: transaction completion must happen even if the client cancelled the request.
        // The token is marked used AFTER the pipeline completes successfully — on a failure it remains
        // available for a retry within the link's TTL
        try
        {
            // Email sends no in-channel prompt, so the outcome reaches the user only through the
            // sign-in page — hence no prompt store is passed either. The terminal wording comes from
            // the core through the receipt port; the error text stays the channel's own.
            await ChannelWebhookPipeline.ProcessInboundResultAsync(
                inboundResult,
                adapter,
                transactionService,
                services.GetRequiredService<ITransactionStore>(),
                services.GetRequiredService<TimeProvider>(),
                identityResolutionService,
                logger,
                metrics,
                userAuthRateLimiter,
                confirmationPromptOrchestrator: null,
                promptLocalizer: null,
                promptMessageStore: null,
                services.GetRequiredService<IOutcomeReceiptText>(),
                services.GetRequiredService<IOutcomeNoticeDisplayIntentSource>(),
                services.GetService<ITransactionEventPublisher>(),
                new ChannelReplyTexts(MessageTemplateNaturalKeys.OutcomeErrorInChannel),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error handling Email confirm through the pipeline. TransactionId: {TransactionId}",
                tokenData.TransactionId);

            var pipelineErrorHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageError);
            return Results.Content(pipelineErrorHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status500InternalServerError);
        }

        // The pipeline (ProcessInboundResultAsync) does not return a success flag — we check the actual
        // result by reading the transaction state from the store (SPEC-016 §4.3).
        // The magic-link token is marked used ONLY when the transaction has actually
        // completed (Completed). On any failure (expired transaction, rate-limit,
        // concurrency conflict, completion error) the token remains available for a retry
        // within the link's TTL.
        var verifyTxResult = await transactionService.GetTransactionAsync(
            confirmTransactionId,
            CancellationToken.None);

        var isCompleted = verifyTxResult.IsSuccess
            && verifyTxResult.Value.State is TransactionState.Completed;

        if (!isCompleted)
        {
            logger.LogWarning(
                "Email confirm pipeline did not finish the transaction in Completed. TransactionId: {TransactionId}, FinalState: {FinalState}",
                tokenData.TransactionId,
                verifyTxResult.IsSuccess ? verifyTxResult.Value.State.ToString() : "Unknown");
        }

        if (!isCompleted)
        {
            var failHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageError);
            return Results.Content(failHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status409Conflict);
        }

        // Mark the token as used only after the transaction completes successfully (SPEC-016 §4.3).
        // If MarkUsedAsync returned false — the token was removed or concurrently marked by another thread;
        // the transaction is already Completed, but a repeated confirmation with the same magic link must be shown
        // as "the link is already used", so the UI and logs reflect the real outcome.
        var marked = await tokenStore.MarkUsedAsync(token, CancellationToken.None);
        if (!marked)
        {
            var usedHtml = BuildConfirmPageHtml(pageLocalizer, servedLocale, branding,
                isError: true,
                errorMessage: EmailAdapterMailStrings.ConfirmPageTokenUsed);
            return Results.Content(usedHtml, "text/html", Encoding.UTF8,
                StatusCodes.Status409Conflict);
        }

        // The terminal wording is resolved BEFORE the synchronous page builder is called: the receipt
        // is a message of the mechanism now (SPEC-036 TPL-123), the transaction type comes from the
        // transaction just verified, and the page only renders what it is handed.
        var confirmedText = await services.GetRequiredService<IOutcomeReceiptText>().RenderAsync(
            new OutcomeReceiptAddress(
                TransactionOutcome.Confirmed,
                OutcomeReceiptSurfaces.EmailConfirmPage,
                verifyTxResult.Value.Type,
                verifyTxResult.Value.ConfirmationSnapshot?.ActionType,
                ChannelTypes.Email),
            servedLocale,
            // The zone the transaction states, beside the locale the page is served in. The moment of
            // the outcome is the one the completion wrote down: the transaction was re-read after the
            // pipeline finished, so this instance carries it.
            verifyTxResult.Value.GetUiTimeZone(),
            verifyTxResult.Value.UpdatedAt,
            // The values of the transaction, as every surface that words a receipt passes them
            // (SPEC-036 TPL-123, TPL-124). In today's delivery its caller values are empty: a
            // confirmation is refused on this page and decided on the core page (SPEC-039 R36), and a
            // sign-in states no caller values.
            TransactionSlotSource.From(verifyTxResult.Value),
            TransactionResolutionContext.For(verifyTxResult.Value),
            CancellationToken.None);

        var successHtml = BuildConfirmPageHtml(
            pageLocalizer, servedLocale, branding, isSuccess: true, outcomeText: confirmedText);
        return Results.Content(successHtml, "text/html", Encoding.UTF8);
    }

    /// <summary>
    /// Resolves a served-page Natural Key into the request language (ICC-050); a missing localizer
    /// translation degrades to the key's base text (the base-language English text).
    /// </summary>
    private static string ResolveServedText(
        IConfirmationPromptLocalizer localizer,
        string naturalKey,
        string? servedLocale)
        => localizer.ResolveOrBaseText(naturalKey, servedLocale);

    /// <summary>
    /// Resolves the <c>&lt;html lang&gt;</c> tag for a served page (TASK-069): the declared language must
    /// match the one actually rendered. When the request locale has a loaded translation set, its
    /// normalized tag (or primary subtag) is used; otherwise the page is English.
    /// </summary>
    private static string ResolveServedHtmlLang(IConfirmationPromptLocalizer localizer, string? servedLocale)
    {
        var normalized = ChannelLocaleTag.Normalize(servedLocale);
        if (normalized is null)
        {
            return EmailAdapterConstants.DefaultHtmlLang;
        }

        var available = localizer.AvailableLocales;
        if (available.Contains(normalized))
        {
            return normalized;
        }

        var dash = normalized.IndexOf('-');
        if (dash > 0 && available.Contains(normalized[..dash]))
        {
            return normalized[..dash];
        }

        return EmailAdapterConstants.DefaultHtmlLang;
    }

    /// <summary>
    /// Generates the magic link confirmation HTML page.
    /// </summary>
    /// <param name="localizer">Channel message localizer (served strings → request language, ICC-050).</param>
    /// <param name="servedLocale">Detected request locale (IETF tag; null — base language).</param>
    /// <param name="branding">Effective branding of the generated page (null — neutral canon).</param>
    /// <param name="isError">Flag of an error page.</param>
    /// <param name="isSuccess">Flag of a successful confirmation.</param>
    /// <param name="errorMessage">The error message (a Natural Key; localized here).</param>
    /// <param name="token">The action token (for the confirmation form).</param>
    /// <param name="isDeclined">Flag of a declined confirmation.</param>
    /// <param name="outcomeText">
    /// Terminal receipt of the transaction, ALREADY resolved and localized by the core
    /// (SPEC-036 TPL-123): the page states where the outcome is shown and renders what it is given.
    /// Required by the two outcome branches and unused by the others.
    /// </param>
    /// <returns>The HTML string of the page.</returns>
    private static string BuildConfirmPageHtml(
        IConfirmationPromptLocalizer localizer,
        string? servedLocale,
        CorePageBranding? branding,
        bool isError = false,
        bool isSuccess = false,
        string? errorMessage = null,
        string? token = null,
        bool isDeclined = false,
        string? outcomeText = null)
    {
        // The method builds the magic link confirmation page, localized into the request language

        var htmlLang = ResolveServedHtmlLang(localizer, servedLocale);

        string statusBlock;
        string buttonBlock;

        if (isSuccess)
        {
            statusBlock = $"""
                <div class="veriqa-status success">{System.Net.WebUtility.HtmlEncode(outcomeText)}</div>
                """;
            buttonBlock = string.Empty;
        }
        else if (isDeclined)
        {
            // Neutral styling, not the error one: the user chose this outcome, nothing went wrong.
            statusBlock = $"""
                <div class="veriqa-status">{System.Net.WebUtility.HtmlEncode(outcomeText)}</div>
                """;
            buttonBlock = string.Empty;
        }
        else if (isError)
        {
            var errorText = ResolveServedText(localizer, errorMessage ?? EmailAdapterMailStrings.ConfirmPageError, servedLocale);
            statusBlock = $"""
                <div class="veriqa-status error">{System.Net.WebUtility.HtmlEncode(errorText)}</div>
                """;
            buttonBlock = string.Empty;
        }
        else
        {
            statusBlock = $"""
                <p class="veriqa-instruction">{System.Net.WebUtility.HtmlEncode(ResolveServedText(localizer, EmailAdapterMailStrings.ConfirmPageQuestion, servedLocale))}</p>
                """;
            // Both answers post to the current URL (GET and POST are mapped to a single path) — no action
            // attribute, which also works when the application is hosted under a sub-path (PathBase), where
            // a root-relative action would send the POST past the application. The answer itself travels in
            // a hidden field: "No" is a POST too, never a GET link, so a mail scanner following links
            // cannot decline a sign-in on the user's behalf.
            var encodedFormToken = System.Net.WebUtility.HtmlEncode(token ?? string.Empty);
            buttonBlock = $"""
                <div class="veriqa-actions">
                  <form method="post">
                    <input type="hidden" name="{EmailAdapterConstants.TokenQueryParam}" value="{encodedFormToken}" />
                    <button type="submit" class="veriqa-btn">{System.Net.WebUtility.HtmlEncode(ResolveServedText(localizer, EmailAdapterMailStrings.ConfirmPageAnswerYes, servedLocale))}</button>
                  </form>
                  <form method="post">
                    <input type="hidden" name="{EmailAdapterConstants.TokenQueryParam}" value="{encodedFormToken}" />
                    <input type="hidden" name="{EmailAdapterConstants.AnswerFormField}" value="{EmailAdapterConstants.AnswerDecline}" />
                    <button type="submit" class="veriqa-btn veriqa-btn-decline">{System.Net.WebUtility.HtmlEncode(ResolveServedText(localizer, EmailAdapterMailStrings.ConfirmPageAnswerNo, servedLocale))}</button>
                  </form>
                </div>
                """;
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="{{htmlLang}}">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <title>{{System.Net.WebUtility.HtmlEncode(ResolveServedText(localizer, EmailAdapterMailStrings.ConfirmPageTitle, servedLocale))}}</title>
              {{CorePageHead.BuildServicePageHead(branding)}}
            </head>
            <body>
              <main class="veriqa-card">
                <h1 class="veriqa-title">{{System.Net.WebUtility.HtmlEncode(ResolveServedText(localizer, EmailAdapterMailStrings.ConfirmPageTitle, servedLocale))}}</h1>
                {{statusBlock}}
                {{buttonBlock}}
              </main>
              {{CorePageAttribution.BuildLine(htmlLang)}}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Converts session_id (a TransactionId string) into a <see cref="TransactionId"/>.
    /// session_id is the direct string representation of TransactionId (Base62, 43 characters).
    /// </summary>
    /// <param name="sessionId">The session_id string.</param>
    /// <returns>The transaction identifier or null on a decoding error.</returns>
    private static TransactionId? ParseTransactionId(string sessionId)
    {
        // The method uses TransactionId.TryParse — session_id is the toString() of TransactionId
        if (TransactionId.TryParse(sessionId, out var transactionId))
        {
            return transactionId;
        }

        return null;
    }

    /// <summary>
    /// Validates the correctness of an email address.
    /// </summary>
    /// <param name="email">The email address to validate.</param>
    /// <returns>true if the address is valid.</returns>
    private static bool IsValidEmail(string email)
    {
        // The method uses .NET MailAddress to validate the email format
        try
        {
            _ = new MailAddress(email.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the additional OIDC claims of the Email channel (SPEC-016 §6.3):
    /// veriqa_channel, veriqa_email_mode.
    /// The verified-email fact is not among them: it is a security-significant statement, so it
    /// travels as the typed <c>ChannelIdentitySnapshot.EmailVerified</c> field, declared by the
    /// contract and visible on review, rather than as a free-form claim name reserved by the core.
    /// </summary>
    /// <param name="mode">The channel mode: pull or push.</param>
    /// <returns>An immutable dictionary of additional claims.</returns>
    internal static IReadOnlyDictionary<string, string> BuildEmailAdditionalClaims(string mode)
    {
        return new Dictionary<string, string>(VeriqaClaimTypes.NameComparer)
        {
            [EmailAdapterConstants.ClaimVeriqaChannel] = ChannelTypes.Email,
            [EmailAdapterConstants.ClaimVeriqaEmailMode] = mode
        }.AsReadOnly();
    }

    /// <summary>
    /// Builds the RawMetadata of the Pull-mode snapshot (SPEC-016 §6.1): the mode and the delivery provider.
    /// </summary>
    /// <param name="credentials">The effective tenant-credential group: the delivery provider is a
    /// tenant-credential field (SPEC-003 §17.3), so it must come from the seam, not from global options.</param>
    /// <returns>The JSON metadata.</returns>
    private static System.Text.Json.JsonElement? BuildPullRawMetadata(EmailTenantCredentials credentials)
    {
        // The method collects safe (non-PII) metadata of the Pull confirmation
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EmailAdapterConstants.MetadataKeyMode] = EmailAdapterConstants.ModePull,
            [EmailAdapterConstants.MetadataKeyDeliveryProvider] = credentials.Outbound.Provider.ToString()
        };

        return System.Text.Json.JsonSerializer.SerializeToElement(metadata);
    }

}

/// <summary>
/// Form field names for the Email Start endpoint.
/// </summary>
internal static class EmailStartFormFields
{
    /// <summary>
    /// The user's email address field.
    /// </summary>
    public const string Email = "email";
}
