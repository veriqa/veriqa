// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Email.Abstractions;
using Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;
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
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// Email adapter endpoints: Pull mode (SPEC-016 §4) and Push mode (SPEC-016 §5).
/// Registers:
/// <list type="bullet">
///   <item><description>GET  /auth/email/start — standalone pull form page (enter email → magic link).</description></item>
///   <item><description>POST /auth/email/start — accepts the email from the user, generates and sends the sign-in mail (link, code or both).</description></item>
///   <item><description>POST /auth/email/code — accepts the code of a sign-in mail and decides its letter as its link would.</description></item>
///   <item><description>GET  /auth/email/confirm — magic link confirmation page (does not change state).</description></item>
///   <item><description>POST /auth/email/confirm — actual transaction confirmation.</description></item>
///   <item><description>GET  /auth/email/claim — page of a link of the claims-completion mail (does not change state).</description></item>
///   <item><description>POST /auth/email/claim — confirmation of the address that link was sent to.</description></item>
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

        // The entry of the code of a sign-in mail is limited as the start is: its attempts are counted per
        // code, and the policy keeps a client from walking codes across transactions.
        var codeEndpoint = endpoints.MapPost(EmailAdapterConstants.PullCodePath, (Delegate)HandleEmailCodeAsync);
        if (!string.IsNullOrEmpty(rateLimitPolicyName))
        {
            codeEndpoint.RequireRateLimiting(rateLimitPolicyName);
        }

        endpoints.MapGet(EmailAdapterConstants.PullConfirmPath, (Delegate)HandleEmailConfirmGetAsync);
        endpoints.MapPost(EmailAdapterConstants.PullConfirmPath, (Delegate)HandleEmailConfirmPostEndpointAsync);

        // The page a link of the claims-completion mail opens (SPEC-041 L50) — mapped as the magic-link page
        // is: its token is a secret of the link, not something a request rate would guess.
        endpoints.MapGet(EmailAdapterConstants.ClaimEmailConfirmPath, (Delegate)HandleClaimLinkGetAsync);
        endpoints.MapPost(EmailAdapterConstants.ClaimEmailConfirmPath, (Delegate)HandleClaimLinkPostAsync);

        // Push mode: the inbound webhook. It is NOT limited by the user-facing rate limiting policy —
        // it is called by the inbound provider and is protected by a secret header.
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
    /// Kind of the mail counter that holds two sign-in mails of one transaction apart. A kind of its own:
    /// the mails of the claims completion are counted under other kinds, so neither holds the other back.
    /// </summary>
    private const string SignInIntervalCounterKind = "signin-interval";

    /// <summary>
    /// The interval between two mails as the whole seconds a client is told to wait: rounded up, so the
    /// figure is never shorter than what is left of the interval and is the interval itself whenever that
    /// is a whole number of seconds. Zero — the interval is switched off.
    /// </summary>
    /// <param name="interval">Interval between two mails (<see cref="EmailOptions.MailResendInterval"/>).</param>
    /// <returns>Seconds to wait.</returns>
    private static int ResendIntervalSeconds(TimeSpan interval) =>
        interval <= TimeSpan.Zero
            ? 0
            : (int)Math.Min(int.MaxValue, Math.Ceiling(interval.TotalSeconds));

    /// <summary>
    /// Handles the Pull flow start request:
    /// accepts email and session_id, generates an action token and sends one sign-in mail — the magic link,
    /// a code, or both, as the pull methods of the transaction say (<see cref="EmailConfigKeys.PullMethods"/>).
    /// A second mail for the same transaction is not sent sooner than
    /// <see cref="EmailOptions.MailResendInterval"/> after the first: the request is refused with 429.
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
            var body = await ReadJsonBodyAsync(httpContext.Request, cancellationToken);

            rawEmail = JsonStringOf(body, EmailStartFormFields.Email);
            sessionId = JsonStringOf(body, EmailAdapterConstants.SessionIdQueryParam);
        }
        else
        {
            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorUnsupportedContentType, servedLocale) },
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        // Only a bare address (addr-spec) is taken — the rule the email block of the web step applies to an
        // entered address (SPEC-041 N70). A display name, angle brackets or a domain literal is refused
        // rather than reduced to a mailbox: the address becomes the recipient of the link and the
        // identity the sign-in ends with.
        if (string.IsNullOrWhiteSpace(rawEmail) || ClaimEmailAddress.TakeBare(rawEmail) is not { } bareEmail)
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
        // Enabled — the channels_enabled axis (SPEC-003 §17.7, CA-169/CA-170), Normalization —
        // core-transport (CFG-230): both stay global by ownership, not by omission. PullEnabled is the
        // ceiling of the core level; the choice of the tenant and the application within it is judged
        // below, once the transaction that names them is read.
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

        var normalizedEmail = EmailAddressHelper.Normalize(bareEmail, normalization);

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

        // The guard above is the ceiling of the core level; the choice of the sign-in modes belongs to the
        // tenant and the application of THIS transaction, so it is judged once the transaction is read and
        // before its state is. Pull left out by the choice is answered as Pull switched off in the core.
        var signInModes = await services.GetRequiredService<IEmailSignInModes>()
            .ResolveAsync(TransactionResolutionContext.For(transaction), cancellationToken);
        if (!signInModes.Pull)
        {
            logger.LogWarning(
                "Email Pull flow requested but Pull mode is not offered to the transaction by its sign-in modes. "
                + "TransactionId: {TransactionId}",
                transactionId);
            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorChannelUnavailable, servedLocale) },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

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

        // The interval between two sign-in mails of one transaction. The mail is reserved before a token
        // is issued and before anything is sent — a refused request leaves no token behind — by the
        // mechanism that counts the mails of the claims completion: a counter of one mail whose window
        // is the interval itself. The key is the transaction, not the address, so another address typed
        // within the interval is refused the same way. The figure handed to the client is the whole
        // interval: the store does not tell what is left of the window, and the interval is the least
        // number that is never short of it.
        var resendInterval = emailOptions.MailResendInterval;
        var retryAfterSeconds = ResendIntervalSeconds(resendInterval);

        if (EmailClaimMailCounter.OfResendInterval(SignInIntervalCounterKind, transaction.Id, resendInterval) is { } intervalCounter)
        {
            var mailCounters = services.GetRequiredService<IEmailClaimCompletionStore>();
            var refusedBy = await mailCounters.TryReserveMailAsync([intervalCounter], cancellationToken);

            if (refusedBy is not null)
            {
                httpContext.Response.Headers.RetryAfter =
                    retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

                return Results.Json(
                    new
                    {
                        success = false,
                        error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorResendTooSoon, servedLocale),
                        retryAfterSeconds
                    },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        // Generate an action token (the adapter's shared URL-safe token generator). Every sign-in mail has
        // one, the mail of a code alone too, where it is not shown: the token identifies the letter to the gate
        // of a sign-in (SPEC-016 EM-208), so the code and the link of one mail are one letter.
        var token = EmailPushTokenGenerator.GenerateUrlSafeToken();

        // TokenTtl — tenant level with the core default as fallback (SPEC-003 §17.3, CFG-220 semantics)
        var tokenExpiresAt = services.GetRequiredService<TimeProvider>().GetUtcNow().Add(
            EmailCredentialsResolver.ResolveTokenTtl(credentials, emailOptions.TokenTtl));

        // The code of a sign-in mail follows the norms of the code of the claims completion (SPEC-041 R12):
        // six digits from the cryptographic generator, kept only as a fingerprint bound to the transaction, and
        // alive no longer than the transaction is. The code and the link of one mail share that lifetime — the
        // moment the mail names as "valid until".
        var code = signInModes.PullCarriesCode ? EmailClaimSecrets.GenerateCode() : null;
        var codeAttempts = 0;

        if (code is not null)
        {
            tokenExpiresAt = EmailClaimCompletion.EarlierOf(tokenExpiresAt, transaction.ExpiresAt);

            using var attemptsScope = new ConfigResolutionScope(TransactionResolutionContext.For(transaction));
            codeAttempts = (await configurationResolver.ResolveAsync(
                EmailConfigKeys.SignInCodeAttempts,
                attemptsScope,
                ConfigDimensionValues.None,
                cancellationToken)).Value;
        }

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
        // transaction initiator context, else the one the transaction took at creation; fallback — the
        // product name
        var clientName = transaction.InitiatorContextSnapshot?.ClientApplicationName
            ?? transaction.GetApplicationName()
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
        // The mail is a text of the channel itself and names no account, whatever the type of the
        // transaction: no snapshot of the mailbox exists before the link is opened, and a snapshot the
        // transaction may already carry need not be of the address the mail goes to — that address is
        // whatever the holder of the window typed, so the mail may not name that account either.
        var promptContext = await promptContextFactory.CreateAsync(
            transaction,
            new ConfirmationPromptRecipient(ChannelTypes.Email, normalizedEmail, Identity: null),
            recipientLocale,
            MessageSurfaces.InChannel,
            cancellationToken);

        var message = new EmailLoginMessage(
            ToAddress: bareEmail,
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
            TimeZone = recipientTimeZone,
            TransactionType = transaction.Type,
            Code = code,
            CarriesLink = signInModes.PullCarriesLink
        };

        var sendResult = await sender.SendLoginEmailAsync(message, cancellationToken);

        if (!sendResult.Success)
        {
            // The code of this mail is not written: a code sent before stays live, as in the claims completion.
            logger.LogError(
                "Failed to send the sign-in email. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transactionId,
                sendResult.ErrorCode);

            return Results.Json(
                new { success = false, error = ResolveServedText(pageLocalizer, EmailAdapterMailStrings.ApiErrorSendFailed, servedLocale) },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        // The new code takes effect only now that its mail is out, and the code before it no longer does;
        // until this write the one before it is the live one. CancellationToken.None: the mail has gone, and a
        // request that goes away must not leave the recipient with a code nothing accepts. A write that fails
        // fails the request although the mail has gone, as the claims completion answers the same failure:
        // the page says the mail was not sent, the code of that mail is not accepted, and the next mail's is.
        if (code is not null)
        {
            var codeTransactionId = transactionId.Value.ToString();

            await services.GetRequiredService<IEmailClaimCompletionStore>().SetSignInCodeAsync(
                codeTransactionId,
                new EmailSignInCode(
                    EmailClaimSecrets.HashOfCode(codeTransactionId, code),
                    token,
                    tokenExpiresAt,
                    codeAttempts),
                transaction.ExpiresAt,
                CancellationToken.None);
        }

        // Publish the email-specific intermediate "sent" status (SPEC-016 §10.1) —
        // additively, without changing the transaction lifecycle state
        await PublishEmailChannelStatusAsync(
            services, transactionId.Value, EmailChannelStatuses.PullSent, logger);

        var maskedEmail = ChannelIdentityMask.MaskEmail(normalizedEmail);

        // The answer names the wait before the next mail, so the page can hold its "send again" button
        // for exactly as long as the server would refuse; zero — the interval is switched off. It says whether
        // the mail carries a code, so the page asks for it.
        return Results.Json(new EmailStartAccepted(
            Success: true,
            MaskedEmail: maskedEmail,
            RetryAfterSeconds: retryAfterSeconds,
            CodeExpected: code is not null));
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
    /// Reads the action token of the request; a request without one names no letter.
    /// </summary>
    /// <param name="services">The request service provider.</param>
    /// <param name="token">The action token as it arrived in the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The data of the token; null — no token, or one the store does not know.</returns>
    private static async Task<EmailActionToken?> ReadActionTokenAsync(
        IServiceProvider services,
        string? token,
        CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(token)
            ? null
            : await services.GetRequiredService<IEmailActionTokenStore>().GetTokenAsync(token, cancellationToken);

    /// <summary>
    /// Reads the one record of the transaction the letter names and what the pages of the letter are rendered
    /// with (<see cref="EmailLetterPage"/>). A token that names no transaction, and a transaction that cannot be
    /// read, leave the page without one: the levels that do not depend on it brand it (SPEC-007 UI-090) and
    /// the request language speaks it.
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="tokenData">The data of the action token; null — the request names no letter.</param>
    /// <returns>What the pages of this request are rendered from.</returns>
    private static async Task<EmailLetterPage> ReadLetterPageAsync(HttpContext httpContext, EmailActionToken? tokenData)
    {
        var services = httpContext.RequestServices;
        var transaction = await TryLoadTransactionAsync(services, tokenData?.TransactionId, httpContext.RequestAborted);

        // Branded for the transaction the action token names (SPEC-007 UI-101), and spoken in the language
        // the sign-in page of that transaction was rendered in rather than in the language of the mail
        // client's browser (see PreferStoredLocale).
        var (branding, texts) = await ResolvePageCustomizationAsync(
            services, TransactionResolutionContext.For(transaction), httpContext.RequestAborted);
        var requestLocale = ChannelRequestLocale.Detect(httpContext.Request.Headers.AcceptLanguage.ToString());

        return new EmailLetterPage(
            transaction,
            branding,
            texts,
            transaction is null ? requestLocale : PreferStoredLocale(transaction, requestLocale));
    }

    /// <summary>
    /// Whether the action token of the request is live — present, known, not used and not past its
    /// deadline: one reading of the token for the GET and the POST of a sign-in and of a confirmation alike.
    /// </summary>
    /// <param name="token">The action token as it arrived in the request.</param>
    /// <param name="tokenData">The data of the token; null — the store does not know it.</param>
    /// <param name="now">The server's current moment.</param>
    /// <returns>true — the token is live; false — the link is answered by its refusal
    /// (<see cref="TokenRefusalPage"/>).</returns>
    private static bool IsLiveToken(
        [NotNullWhen(true)] string? token,
        [NotNullWhen(true)] EmailActionToken? tokenData,
        DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(token)
        && tokenData is not null
        && !tokenData.IsUsed
        && !tokenData.IsExpired(now);

    /// <summary>
    /// The refusal of a link whose token is not live (<see cref="IsLiveToken"/>), titled by the type of its
    /// transaction: an unknown link, a used one, an expired one. The GET answers it with a page and no
    /// error status, the POST with the status of the refusal.
    /// </summary>
    /// <param name="localizer">Localizer of the page texts.</param>
    /// <param name="letter">What the page is rendered from.</param>
    /// <param name="tokenData">The data of the token; null — the request names no known token.</param>
    /// <param name="withErrorStatus">Whether the answer carries the status of the refusal (the POST).</param>
    /// <returns>The error page.</returns>
    private static IResult TokenRefusalPage(
        IConfirmationPromptLocalizer localizer,
        EmailLetterPage letter,
        EmailActionToken? tokenData,
        bool withErrorStatus) =>
        // One reading of the token state: each arm pairs the text of the refusal with its status. The key is
        // passed as a constant in each arm, so the locale audit sees every one of them.
        tokenData switch
        {
            null => LetterErrorPage(
                localizer, letter, EmailAdapterMailStrings.ConfirmPageTokenNotFound,
                withErrorStatus ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK),
            { IsUsed: true } => LetterErrorPage(
                localizer, letter, EmailAdapterMailStrings.ConfirmPageTokenUsed,
                withErrorStatus ? StatusCodes.Status409Conflict : StatusCodes.Status200OK),
            _ => LetterErrorPage(
                localizer, letter, EmailAdapterMailStrings.ConfirmPageTokenExpired,
                withErrorStatus ? StatusCodes.Status410Gone : StatusCodes.Status200OK)
        };

    /// <summary>
    /// The error page of a letter: titled as the request of its transaction is — a confirmation as one,
    /// because the sign-in title would name a sign-in the user was not asked for.
    /// </summary>
    /// <param name="localizer">Localizer of the page texts.</param>
    /// <param name="letter">What the page is rendered from.</param>
    /// <param name="errorKey">The error text (a Natural Key).</param>
    /// <param name="statusCode">The status of the answer.</param>
    /// <returns>The error page.</returns>
    private static IResult LetterErrorPage(
        IConfirmationPromptLocalizer localizer,
        EmailLetterPage letter,
        string errorKey,
        int statusCode) =>
        Results.Content(
            BuildConfirmPageHtml(localizer, letter.Locale, letter.Branding, letter.Texts,
                isError: true,
                errorMessage: errorKey,
                isConfirmationLetter: letter.IsConfirmation),
            "text/html",
            Encoding.UTF8,
            statusCode);

    /// <summary>
    /// The Email channel adapter of the composition; null — it is not registered.
    /// </summary>
    /// <param name="services">The service provider of the request or of the mail's scope.</param>
    /// <returns>The adapter, or null.</returns>
    private static IChannelAdapter? EmailAdapterOf(IServiceProvider services) =>
        services.GetRequiredService<IEnumerable<IChannelAdapter>>()
            .FirstOrDefault(a => string.Equals(a.ChannelType, ChannelTypes.Email, StringComparison.Ordinal));

    /// <summary>
    /// Renders the magic link confirmation page (GET — does not change transaction state). The link of a
    /// sign-in and the link of a confirmation transaction get the same request page: it asks nothing, names
    /// the application and offers the continue button and "Cancel", which only closes the tab; opened by the
    /// browser that took part in the transaction, it continues by itself (<see cref="EmailLinkSameBrowser"/>).
    /// Only the words naming the action and what the continue button leads to differ by type: the POST of a
    /// sign-in attaches the mailbox without completing it — the sign-in window asks the one question of the
    /// sign-in and decides it — and the POST of a confirmation hands the user over to the core page that asks
    /// what is being confirmed (<see cref="HandOverConfirmationAsync"/>). When the effective confirmation
    /// surface of a sign-in is <see cref="ConfirmationSurface.None"/> (SPEC-012 §4.4.2), it automatically
    /// completes the transaction when the link is followed, without an additional user action (fast link). A
    /// sign-in that the fast link leaves waiting for missing claims with the form on this device is not spent
    /// by the GET: the page offers one "Continue" button instead (SPEC-041 N78,
    /// <see cref="AnswerClaimCompletionAsync"/>).
    /// </summary>
    private static async Task<IResult> HandleEmailConfirmGetAsync(
        HttpContext httpContext,
        string? token)
    {
        // Served page localization (ICC-050): the confirmation page and its status/error texts render in the
        // language of the transaction, else of the request; a missing translation degrades to English (the key).
        var services = httpContext.RequestServices;
        var pageLocalizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
        var tokenData = await ReadActionTokenAsync(services, token, httpContext.RequestAborted);
        var letter = await ReadLetterPageAsync(httpContext, tokenData);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        // An unknown, used or expired link is answered by its refusal, titled by the type of its transaction;
        // the GET answers it with a page and no error status.
        if (!IsLiveToken(token, tokenData, now))
        {
            return TokenRefusalPage(pageLocalizer, letter, tokenData, withErrorStatus: false);
        }

        // Whether the transaction can still be answered decides only what the page offers beyond the request:
        // the fast link and the page sending itself. The request page is the same either way, and its button
        // is answered by the refusal of the write.
        var live = letter.Transaction is { } stored && !stored.HasRunOutOfTime(now) ? stored : null;

        // A confirmation transaction never takes the fast link: a confirmation whose effective surface
        // resolves to None would otherwise end right here, on a GET, with its subject never shown
        // (SPEC-039 E29). An unreadable transaction does not take it either — the safe default is the
        // request page, so that a GET does not implicitly complete the sign-in.
        if (!letter.IsConfirmation
            && live is not null
            && EmailAdapterOf(services) is { } adapter
            && await ShouldAutoConfirmAsync(services, live, httpContext.RequestAborted))
        {
            // Effective Email surface = None: GET automatically completes the transaction
            // (fast link, SPEC-012 §4.4.2 / SPEC-016 §4.4). The usage stays in the operational log
            // and is not published as a channel audit event: that event type carries no outcome of
            // its own, so the audit journal records the whole class as a failure — an informational
            // fact would read as one (SPEC-016 EM-176, SPEC-011 N37). We delegate to the handler of the
            // POST of a sign-in — the single completion point.
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EmailAuthEndpoints)).LogInformation(
                "Email confirm: effective surface is None — GET completes the transaction without a confirming action. TransactionId: {TransactionId}",
                tokenData.TransactionId);

            return await RenderLetterPageAsync(
                httpContext,
                token,
                letter,
                await ConfirmSignInLetterAsync(httpContext, token, tokenData, letter, adapter, fromFastLink: true));
        }

        // The user opened the email and followed the link — publish the "opened" status
        // (SPEC-016 §10.1) only on the first open: repeat views and prefetch by
        // mail scanners do not duplicate the signal. The token is not consumed here (EM-010).
        var openedFirstTime = await services.GetRequiredService<IEmailActionTokenStore>()
            .TryMarkOpenedAsync(token, httpContext.RequestAborted);
        if (openedFirstTime
            && TransactionId.TryParse(tokenData.TransactionId, out var openedTransactionId))
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EmailAuthEndpoints));
            await PublishEmailChannelStatusAsync(
                services,
                openedTransactionId,
                EmailChannelStatuses.PullOpened,
                logger);
        }

        // The request page — one for both types; the GET writes nothing to the transaction (SPEC-016
        // EM-011): a mail scanner following links must not attach the identity, nor become the browser
        // the answer of a confirmation is bound to. The browser that took part in the transaction is not
        // asked to press the button: where the transaction holds its fingerprint and address, the page
        // sends the form by itself (EmailLinkSameBrowser).
        var html = BuildConfirmPageHtml(pageLocalizer, letter.Locale, letter.Branding, letter.Texts,
            token: token,
            isConfirmationRequest: letter.IsConfirmation,
            appName: ApplicationNameOf(letter.Transaction),
            continuesInSameBrowser: EmailLinkSameBrowser.IsOffered(live),
            cspNonce: httpContext.Items[HttpContextItemKeys.CspNonce] as string);

        return Results.Content(html, "text/html", Encoding.UTF8);
    }

    /// <summary>
    /// The name of the application a transaction is signed in to, as the pages a link of a letter name it;
    /// the configured default name when the transaction states none or is unknown.
    /// </summary>
    /// <param name="transaction">The transaction of the letter; null — none is known.</param>
    /// <returns>The name of the application.</returns>
    private static string ApplicationNameOf(Transaction? transaction) =>
        transaction?.InitiatorContextSnapshot?.ClientApplicationName
            ?? transaction?.GetApplicationName()
            ?? EmailAdapterConfigDefaults.DefaultClientName;

    /// <summary>
    /// Prefers the language the sign-in page was rendered in — stored on the transaction as
    /// <c>OidcContext.UiLocale</c> — over the Accept-Language of the current request.
    /// </summary>
    /// <remarks>
    /// The magic link is opened from a mail client, whose browser may send a different Accept-Language
    /// than the browser that started the sign-in; detecting from the header alone then renders this page
    /// in another language than the sign-in window. The stored locale also carries an explicit
    /// <c>ui_locales</c> choice made on the RP side, which no header reflects. The action token has no
    /// locale of its own but does carry the transaction id, so the stored locale is reachable.
    /// </remarks>
    /// <param name="transaction">The transaction the action token names.</param>
    /// <param name="acceptLanguageLocale">Locale detected from Accept-Language (the fallback).</param>
    /// <returns>The locale to render the page in, or null for the base language.</returns>
    private static string? PreferStoredLocale(Transaction transaction, string? acceptLanguageLocale)
    {
        var uiLocale = transaction.GetUiLocale();
        return string.IsNullOrWhiteSpace(uiLocale) ? acceptLanguageLocale : uiLocale;
    }

    /// <summary>
    /// Determines whether the letter of a sign-in completes it — the GET of the magic link (fast link) and
    /// the inbound mail of the Push mode alike — or only attaches the mailbox and leaves the decision to the
    /// sign-in window. Derived from the effective confirmation surface (SPEC-012 §4.4.2):
    /// <c>None</c> → the letter completes the sign-in; otherwise (<c>OnWebPage</c> — the Email default,
    /// or a clamped <c>InChannel</c>) → the letter attaches the mailbox and the window asks the question
    /// (the GET shows the request page and does not consume the token, EM-010/011).
    /// Returns <c>true</c> ⇔ the effective Email surface = <c>None</c>.
    /// </summary>
    /// <param name="services">The service provider of the request or of the mail's scope.</param>
    /// <param name="transaction">The transaction (the resolution context source).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the letter completes the sign-in; <c>false</c> — the window decides it.</returns>
    internal static async ValueTask<bool> ShouldAutoConfirmAsync(
        IServiceProvider services,
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        // The Email adapter is not registered — safe default: do NOT auto-confirm
        // (show the confirmation page, in the spirit of the method's safe default when unavailable).
        if (EmailAdapterOf(services) is not { } emailAdapter)
        {
            return false;
        }

        var surface = await services.GetRequiredService<IEffectiveConfirmationSurfaceResolver>()
            .ResolveEffectiveSurfaceAsync(transaction, emailAdapter, cancellationToken);
        return surface is ConfirmationSurface.None;
    }

    /// <summary>
    /// Handles the POST of the served confirmation page — the continue button of its request (SPEC-016
    /// EM-011). The request is read once: the token, and the transaction the token names as the store
    /// holds it (<see cref="ReadLetterPageAsync"/>), whose type routes the letter whatever its deadline. An
    /// unknown, used or expired link is answered by its refusal, titled by that type. A POST the request page
    /// sent by itself goes on as the button only from the browser of the transaction, whatever its type
    /// (<see cref="AnswerUnrecognisedBrowserAsync"/>). For a sign-in the letter attaches the mailbox without
    /// completing the transaction, and the sign-in is decided on the sign-in window — the page offers no
    /// refusal, the window does (<see cref="ConfirmSignInLetterAsync"/>). For a confirmation transaction the
    /// letter hands the user over to the core page (<see cref="HandOverConfirmationAsync"/>). The request is one
    /// action of the channel: the admission of the sender is decided once for the gate of the letter and the
    /// write that follows it (<see cref="ChannelInboundProcessor.OpenAction"/>).
    /// </summary>
    private static async Task<IResult> HandleEmailConfirmPostEndpointAsync(HttpContext httpContext)
    {
        using var action = ChannelInboundProcessor.OpenAction();

        var services = httpContext.RequestServices;
        var pageLocalizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
        var token = await ExtractTokenAsync(httpContext);
        var tokenData = await ReadActionTokenAsync(services, token, httpContext.RequestAborted);
        var letter = await ReadLetterPageAsync(httpContext, tokenData);

        if (!IsLiveToken(token, tokenData, services.GetRequiredService<TimeProvider>().GetUtcNow()))
        {
            return TokenRefusalPage(pageLocalizer, letter, tokenData, withErrorStatus: true);
        }

        if (await AnswerUnrecognisedBrowserAsync(httpContext, token, tokenData, letter) is { } requestPage)
        {
            return requestPage;
        }

        if (EmailAdapterOf(services) is not { } adapter)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EmailAuthEndpoints)).LogError(
                "Email adapter not found in DI while handling confirm. TransactionId: {TransactionId}",
                tokenData.TransactionId);

            return LetterErrorPage(
                pageLocalizer, letter, EmailAdapterMailStrings.ConfirmPageError, StatusCodes.Status500InternalServerError);
        }

        return await RenderLetterPageAsync(
            httpContext,
            token,
            letter,
            await DecideLetterAsync(httpContext, token, tokenData, letter, adapter));
    }

    /// <summary>
    /// Decides a live letter once it is taken as the action of the user — the continue button of the page of
    /// its link, or its code entered on the page the address was typed into (SPEC-016 EM-205, EM-206): the one
    /// set of branches both ways go through, so a code does what the link of the same letter does.
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="token">The action token of the letter, live.</param>
    /// <param name="tokenData">The data of the action token.</param>
    /// <param name="letter">What the pages of the letter are rendered from, its transaction among them.</param>
    /// <param name="adapter">The Email channel adapter.</param>
    /// <returns>What the letter came to.</returns>
    private static async Task<EmailLetterOutcome> DecideLetterAsync(
        HttpContext httpContext,
        string token,
        EmailActionToken tokenData,
        EmailLetterPage letter,
        IChannelAdapter adapter) =>
        // A confirmation is decided on the core page and nowhere else (SPEC-039 R36): whatever the request
        // says, it only hands the user over — past the deadline of the transaction too, where the hand-over
        // refuses it.
        letter.IsConfirmation
            ? await HandOverConfirmationAsync(httpContext, token, tokenData, letter.Transaction, adapter)
            : await ConfirmSignInLetterAsync(httpContext, token, tokenData, letter, adapter, fromFastLink: false);

    /// <summary>
    /// Answers the page of the link of a letter with what the letter came to: the error page of a refusal, the
    /// line sending the user back to the sign-in window, the receipt of a completed sign-in, the "Continue" page
    /// of the fast link, the page of the claims completion, or the redirect to the question of a confirmation.
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="token">The action token of the letter — posted by the "Continue" page of the fast link.</param>
    /// <param name="letter">What the pages of the letter are rendered from.</param>
    /// <param name="outcome">What the letter came to.</param>
    /// <returns>The answer of the page.</returns>
    private static async Task<IResult> RenderLetterPageAsync(
        HttpContext httpContext,
        string token,
        EmailLetterPage letter,
        EmailLetterOutcome outcome)
    {
        var services = httpContext.RequestServices;
        var pageLocalizer = services.GetRequiredService<IConfirmationPromptLocalizer>();
        var cspNonce = httpContext.Items[HttpContextItemKeys.CspNonce] as string;

        return outcome switch
        {
            EmailLetterOutcome.Refused refused => LetterErrorPage(
                pageLocalizer,
                letter,
                refused.LetterUsed ? EmailAdapterMailStrings.ConfirmPageTokenUsed : EmailAdapterMailStrings.ConfirmPageError,
                refused.StatusCode),
            EmailLetterOutcome.SignInContinued => Results.Content(
                BuildConfirmPageHtml(pageLocalizer, letter.Locale, letter.Branding, letter.Texts,
                    isSignInContinued: true,
                    cspNonce: cspNonce),
                "text/html",
                Encoding.UTF8),
            EmailLetterOutcome.Completed completed => Results.Content(
                BuildConfirmPageHtml(pageLocalizer, letter.Locale, letter.Branding, letter.Texts,
                    isSuccess: true,
                    outcomeText: completed.ReceiptText),
                "text/html",
                Encoding.UTF8),
            EmailLetterOutcome.ClaimFormContinue => Results.Content(
                BuildConfirmPageHtml(pageLocalizer, letter.Locale, letter.Branding, letter.Texts,
                    token: token,
                    isClaimFormContinue: true),
                "text/html",
                Encoding.UTF8),

            // CancellationToken.None: the letter is spent — the form must be opened even if the browser goes
            // away, the way the confirmation itself is finished.
            EmailLetterOutcome.ClaimFormOnThisDevice form =>
                (services.GetService<IClaimCompletionCrossDevicePage>() is { } crossDevicePage
                    ? await crossDevicePage.TryOpenAsync(httpContext, form.TransactionId, CancellationToken.None)
                    : null)
                ?? StartDevicePage(pageLocalizer, letter),
            EmailLetterOutcome.ContinueOnStartDevice => StartDevicePage(pageLocalizer, letter),
            EmailLetterOutcome.HandedOverToQuestion handedOver => SeeOtherToQuestion(httpContext, handedOver.TransactionId),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Not an outcome of a letter.")
        };
    }

    /// <summary>
    /// The page sending the user of a sign-in confirmed into the claims completion to the device the sign-in
    /// was started on.
    /// </summary>
    /// <param name="pageLocalizer">Localizer of the page texts.</param>
    /// <param name="letter">What the pages of the letter are rendered from.</param>
    /// <returns>The page.</returns>
    private static IResult StartDevicePage(IConfirmationPromptLocalizer pageLocalizer, EmailLetterPage letter) =>
        Results.Content(
            BuildConfirmPageHtml(pageLocalizer, letter.Locale, letter.Branding, letter.Texts,
                isSuccess: true,
                outcomeText: StartDeviceText(pageLocalizer, letter)),
            "text/html",
            Encoding.UTF8);

    /// <summary>
    /// The line sending the user to the device the sign-in was started on. It is the page's own text, not a
    /// receipt of the outcome: an entry overrides it here, where the page picks it, while the page builder
    /// renders it as it is given.
    /// </summary>
    /// <param name="pageLocalizer">Localizer of the page texts.</param>
    /// <param name="letter">What the pages of the letter are rendered from.</param>
    /// <returns>The line.</returns>
    private static string StartDeviceText(IConfirmationPromptLocalizer pageLocalizer, EmailLetterPage letter) =>
        letter.Texts.Localize(
            pageLocalizer,
            EmailAdapterMailStrings.ConfirmPageContinueOnStartDevice,
            ResolveServedHtmlLang(pageLocalizer, letter.Locale));

    /// <summary>
    /// The path of the core page that asks what a confirmation is about, for the confirmation of this request
    /// — the page lives in this same application, so the path carries its path base.
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="transactionId">The confirmation transaction.</param>
    /// <returns>The path of the question.</returns>
    private static string QuestionPath(HttpContext httpContext, TransactionId transactionId) =>
        $"{httpContext.Request.PathBase}{CoreConfirmPageAnswerMark.PagePath}"
        + $"?{EmailAdapterConstants.SessionIdQueryParam}={Uri.EscapeDataString(transactionId.ToString())}";

    /// <summary>
    /// Sends the browser that handed a confirmation over to its question: 303, the POST of the letter is
    /// answered by a GET of the question.
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="transactionId">The confirmation transaction.</param>
    /// <returns>The redirect.</returns>
    private static IResult SeeOtherToQuestion(HttpContext httpContext, TransactionId transactionId)
    {
        httpContext.Response.Headers.Location = QuestionPath(httpContext, transactionId);

        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    /// <summary>
    /// Answers the POST the request page sent by itself (<see cref="EmailLinkSameBrowser"/>) when the browser
    /// is not the one that took part in the transaction: the request page again, with its button and without
    /// the script that sends it, and nothing written — for a sign-in and a confirmation alike. A POST without
    /// the mark of that sending is a press of the button, and so is one whose browser is recognised: both go
    /// on as the button does.
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="token">The action token of the request, live.</param>
    /// <param name="tokenData">The data of the action token.</param>
    /// <param name="letter">What the pages of the letter are rendered from.</param>
    /// <returns>The request page; null — the POST goes on as the press of the button.</returns>
    private static async Task<IResult?> AnswerUnrecognisedBrowserAsync(
        HttpContext httpContext,
        string token,
        EmailActionToken tokenData,
        EmailLetterPage letter)
    {
        var services = httpContext.RequestServices;

        if (!httpContext.Request.HasFormContentType)
        {
            return null;
        }

        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        if (string.IsNullOrEmpty(form[EmailAdapterConstants.SameBrowserMarkField]))
        {
            return null;
        }

        var matched = EmailLinkSameBrowser.Matches(
            letter.Transaction,
            form[EmailAdapterConstants.SameBrowserFingerprintField],
            httpContext.Connection.RemoteIpAddress);

        // Only whether the browser was recognised: the fingerprint is neither logged nor kept (SPEC-025 HOP-092).
        services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EmailAuthEndpoints)).LogInformation(
            "Email confirm: the request page of the letter continued by itself. TransactionId: {TransactionId}, BrowserRecognised: {BrowserRecognised}",
            tokenData.TransactionId,
            matched);

        if (matched)
        {
            return null;
        }

        var html = BuildConfirmPageHtml(
            services.GetRequiredService<IConfirmationPromptLocalizer>(), letter.Locale, letter.Branding, letter.Texts,
            token: token,
            isConfirmationRequest: letter.IsConfirmation,
            appName: ApplicationNameOf(letter.Transaction),
            cspNonce: httpContext.Items[HttpContextItemKeys.CspNonce] as string);
        return Results.Content(html, "text/html", Encoding.UTF8);
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
    /// Hands a CONFIRMATION transaction over to the core page that asks its subject (SPEC-039 E29): the
    /// continue button of the request page of the letter — pressed, or sent by the page itself from the
    /// browser of the transaction — attaches the mailbox identity, binds the answer to this browser and
    /// sends it to the question.
    /// </summary>
    /// <remarks>
    /// This adapter asks no question of its own about a confirmation: it carries no in-channel
    /// confirmation surface, and its own page words a sign-in (E28). Following the magic link is
    /// therefore the user ARRIVING and not deciding — the decision is taken on the one surface that
    /// shows what is being decided, and only in the browser that arrived by the letter: the attachment
    /// binds the answer to the browser (<c>bindAnswerToBrowser</c>) and this browser alone is handed the
    /// mark the core page asks for. A browser that holds the sign-in window without the letter is not
    /// the one that proved the mailbox.
    /// <para>
    /// The token is deliberately NOT spent here: it carries no decision, and spending it would cost the
    /// user the way back to the question after closing the tab.
    /// </para>
    /// </remarks>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="token">The action token of the letter, live.</param>
    /// <param name="tokenData">The data of the live action token.</param>
    /// <param name="transaction">The confirmation transaction the token names, as the store holds it — past
    /// its deadline too, which the hand-over refuses.</param>
    /// <param name="adapter">The Email channel adapter.</param>
    /// <returns>The hand-over to the core page, or the refusal of the letter.</returns>
    private static async Task<EmailLetterOutcome> HandOverConfirmationAsync(
        HttpContext httpContext,
        string token,
        EmailActionToken tokenData,
        Transaction transaction,
        IChannelAdapter adapter)
    {
        var services = httpContext.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(EmailAuthEndpoints));

        // The identity below reads the tenant's Email credentials, and the tenant of this confirmation is
        // the one the transaction states — the scope opens for the snapshot and closes with it (CA-164).
        using var tenantScope = ChannelTenantContext.BeginScope(transaction.GetTenantId());

        var handOver = await HandOverToQuestionAsync(services, token, tokenData, transaction, adapter, logger);

        if (handOver.IsFailure)
        {
            // A transaction that no longer accepts this event — decided, past its TTL, bound to another
            // mailbox, a sender not admitted, a parallel write of another sender — is an expected
            // refusal here, hence Warning and not Error for all of them alike; its class is already in the
            // journal. The page keeps its existing error wording: no new text and no detail of the
            // refusal, which is not this user's business.
            logger.LogWarning(
                "Email confirm: the channel identity was not attached for the core confirmation page. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transaction.Id.ToString(),
                handOver.Error.Code);

            return new EmailLetterOutcome.Refused(StatusCodes.Status409Conflict);
        }

        // The mark is handed to this browser alone — the one the letter was answered in.
        services.GetRequiredService<CoreConfirmPageAnswerMark>()
            .Issue(httpContext, handOver.Value, services.GetRequiredService<TimeProvider>().GetUtcNow());

        return new EmailLetterOutcome.HandedOverToQuestion(transaction.Id);
    }

    /// <summary>
    /// Attaches the mailbox of the letter to the confirmation transaction with the answer bound to the browser
    /// of the letter, unless it is attached that way already. The letter passes the gate of the letter of a
    /// sign-in, its budget included, and every refusal reaches the journal under its class (SPEC-039 E48) by
    /// the same mechanism (<see cref="ChannelWebhookPipeline.RefuseLetterOutOfTimeAsync"/>,
    /// <see cref="ChannelWebhookPipeline.RefuseLetterAttachAsync"/>,
    /// <see cref="ChannelWebhookPipeline.RecordLetterRefusalAsync"/>).
    /// </summary>
    /// <remarks>
    /// A second press of the same letter — in this browser or another — arrives at the same question with
    /// the mark of its own, while a letter to another address is refused by the subject binding
    /// (SPEC-039 N34). The attachment is written only while the transaction is not yet handed over to the
    /// mailbox of this letter (<see cref="IsHandedOverByLetter"/>): a repeated press writes nothing and
    /// only hands the mark out, and takes no budget. A transaction out of time is refused by its record,
    /// before the identity of the mailbox is built. A press that lost the write to a parallel press of the
    /// same letter reads the transaction again and is answered the same way; one that lost it to a write of
    /// the same mailbox that did not bind the answer to a browser attaches once more with the fresh
    /// concurrency token.
    /// </remarks>
    /// <param name="services">The request service provider.</param>
    /// <param name="token">The action token of the letter, live — what identifies the letter to its budget.</param>
    /// <param name="tokenData">The data of the live action token.</param>
    /// <param name="transaction">The confirmation transaction, as the store holds it.</param>
    /// <param name="adapter">The Email channel adapter.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The transaction handed over to the mailbox of the letter; a failure — the code of the refusal,
    /// already journaled.</returns>
    private static async Task<Result<Transaction>> HandOverToQuestionAsync(
        IServiceProvider services,
        string token,
        EmailActionToken tokenData,
        Transaction transaction,
        IChannelAdapter adapter,
        ILogger logger)
    {
        var timeProvider = services.GetRequiredService<TimeProvider>();

        if (IsHandedOverByLetter(transaction, tokenData, timeProvider.GetUtcNow()))
        {
            return Result<Transaction>.Success(transaction);
        }

        var processor = services.GetRequiredService<ChannelInboundProcessor>();

        if (await ChannelWebhookPipeline.RefuseLetterOutOfTimeAsync(processor, transaction, adapter) is { } outOfTime)
        {
            return outOfTime;
        }

        var transactionService = services.GetRequiredService<ITransactionService>();
        var snapshot = await BuildPullIdentitySnapshotAsync(services, tokenData, transaction.GetTenantId(), logger);

        // The gate and the budget of the letter of a sign-in, in the same order (SPEC-016 EM-206): one
        // attempt per letter, however many presses of it reach the gate.
        if (await ChannelWebhookPipeline.RefuseLetterAttachAsync(
                processor, transaction, snapshot, adapter, token, CancellationToken.None) is { } refusal)
        {
            return refusal;
        }

        // CancellationToken.None: the identity must be attached even if the browser goes away —
        // otherwise the user comes back to a question that has nobody to answer it.
        var attachResult = await transactionService.AttachChannelIdentityAsync(
            transaction.Id,
            snapshot,
            transaction.ConcurrencyToken,
            bindAnswerToBrowser: true,
            cancellationToken: CancellationToken.None);

        if (attachResult.IsSuccess)
        {
            return attachResult;
        }

        if (attachResult.Error.Code is TransactionErrorCodes.ConcurrencyConflict)
        {
            // The write was lost to a parallel press: when that press handed the transaction over to
            // this same mailbox, this press arrives at the question all the same.
            var rereadResult = await transactionService.GetTransactionAsync(transaction.Id, CancellationToken.None);
            if (rereadResult.IsSuccess
                && IsHandedOverByLetter(rereadResult.Value, tokenData, timeProvider.GetUtcNow()))
            {
                return rereadResult;
            }

            if (rereadResult.IsSuccess
                && rereadResult.Value.IsAwaitingWebConfirmation()
                && IsSubjectOfLetter(rereadResult.Value, tokenData)
                && rereadResult.Value.SubjectBinding is not { AnswerBrowserBound: true })
            {
                // The parallel write attached this same mailbox without handing the answer to a browser
                // (an Email Push of the same letter): one more attempt with the fresh token.
                attachResult = await transactionService.AttachChannelIdentityAsync(
                    transaction.Id,
                    snapshot,
                    rereadResult.Value.ConcurrencyToken,
                    bindAnswerToBrowser: true,
                    cancellationToken: CancellationToken.None);
                if (attachResult.IsSuccess)
                {
                    return attachResult;
                }
            }
        }

        await ChannelWebhookPipeline.RecordLetterRefusalAsync(
            processor, transaction.Id, attachResult.Error.Code, adapter);

        return attachResult;
    }

    /// <summary>
    /// Whether the confirmation transaction is already handed over to the mailbox of the letter: it waits
    /// for the web answer, has time left, and the identity of this mailbox is attached with the answer
    /// bound to the browser of a letter. Nothing is left to write then — only the mark to hand out.
    /// </summary>
    /// <param name="transaction">The confirmation transaction the token names.</param>
    /// <param name="tokenData">The action token of the letter.</param>
    /// <param name="now">The server's current moment.</param>
    /// <returns>true — a press of the letter hands out the mark without a write.</returns>
    private static bool IsHandedOverByLetter(Transaction transaction, EmailActionToken tokenData, DateTimeOffset now) =>
        transaction.IsAwaitingWebConfirmation()
        && !transaction.HasRunOutOfTime(now)
        && transaction.SubjectBinding is { AnswerBrowserBound: true }
        && IsSubjectOfLetter(transaction, tokenData);

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
    /// Handles the live magic link of a sign-in.
    /// Called both from the POST endpoint (the "Continue signing in" of the request of a sign-in) and from
    /// GET when the effective surface is None (fast link). On an effective surface other than None the POST
    /// only attaches the mailbox and the sign-in window decides the sign-in
    /// (<see cref="ContinueSignInAsync"/>). The fast link, and a POST whose surface has turned to None since
    /// the page was served, confirm the transaction: a sign-in the confirmation leaves waiting for missing
    /// claims is answered by the page of the claims completion (<see cref="AnswerClaimCompletionAsync"/>), a
    /// completed one — by the receipt of the outcome.
    /// </summary>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="token">The action token of the request, live.</param>
    /// <param name="tokenData">The data of the action token.</param>
    /// <param name="letter">What the pages of the letter are rendered from, its transaction among them.</param>
    /// <param name="adapter">The Email channel adapter.</param>
    /// <param name="fromFastLink">The request is the GET of the fast link, not a POST of the page.</param>
    /// <returns>What the letter came to.</returns>
    private static async Task<EmailLetterOutcome> ConfirmSignInLetterAsync(
        HttpContext httpContext,
        string token,
        EmailActionToken tokenData,
        EmailLetterPage letter,
        IChannelAdapter adapter,
        bool fromFastLink)
    {
        var services = httpContext.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(EmailAuthEndpoints));

        // The identifier is read out of the mail action token — an external input, so it is parsed
        // here and handed to the pipeline already typed.
        if (!TransactionId.TryParse(tokenData.TransactionId, out var confirmTransactionId))
        {
            logger.LogWarning(
                "Email confirm: invalid TransactionId in the action token: {TransactionId}",
                tokenData.TransactionId);

            return new EmailLetterOutcome.Refused(StatusCodes.Status400BadRequest);
        }

        // Important: mark the token used AFTER the pipeline completes successfully.
        // On a transient error the pipeline throws an exception, and the token remains available
        // for a retry. After successful completion — the token is consumed atomically
        // via MarkUsedAsync (SPEC-016 §4.3: single use of the magic link).
        // Even if MarkUsedAsync does not run (crash), reuse is safe,
        // because the transaction is already in a terminal state (Completed) and ConfirmTransactionAsync
        // returns an error on a repeated confirmation.

        // Without the record of the letter there is nothing to read the surface or the tenant off, and the
        // letter is not taken as a decision. The fast link reaches this point only with a live record.
        var transaction = letter.Transaction;
        if (transaction is null)
        {
            logger.LogWarning(
                "Email confirm: the transaction of the letter could not be read. TransactionId: {TransactionId}",
                tokenData.TransactionId);

            return new EmailLetterOutcome.Refused(StatusCodes.Status409Conflict);
        }

        // Tenant of the transaction this page is about to confirm: the record of the letter states it on a web
        // path, where no route segment and no inbound secret can (CA-164). The scope opens here, before the
        // identity snapshot below resolves the tenant's Email credentials, and holds for the rest of the
        // request.
        using var tenantScope = ChannelTenantContext.BeginScope(transaction.GetTenantId());

        // A sign-in already confirmed into the claims completion by this letter — by its fast link, which
        // showed the page with "Continue", or by a parallel request — is not confirmed a second time: it is
        // answered by the page of the completion straight away (SPEC-041 N78). A sign-in confirmed otherwise
        // goes on below, as the letter of any sign-in that is no longer pending: its write is refused, and the
        // letter is answered by the state the sign-in is in.
        if (!transaction.HasRunOutOfTime(services.GetRequiredService<TimeProvider>().GetUtcNow())
            && IsClaimCompletionOfLetter(transaction, tokenData))
        {
            return await AnswerClaimCompletionAsync(httpContext, token, transaction, fromFastLink);
        }

        // The "Continue signing in" of the request of a sign-in. Whether the letter completes the sign-in is
        // read off the surface again here, not taken from the page: on any surface but None the window asks
        // the one question of the sign-in (SPEC-012 §4.4.2), so the letter only attaches the mailbox.
        if (!fromFastLink && !await ShouldAutoConfirmAsync(services, transaction, CancellationToken.None))
        {
            return await ContinueSignInAsync(httpContext, token, tokenData, transaction, adapter, logger);
        }

        var snapshot = await BuildPullIdentitySnapshotAsync(services, tokenData, transaction.GetTenantId(), logger);

        // Build the inbound result to pass into the pipeline
        var inboundResult = new ChannelAuthConfirmResult
        {
            TransactionId = confirmTransactionId,
            Identity = snapshot
        };

        var processor = services.GetRequiredService<ChannelInboundProcessor>();

        // Process through the pipeline (transaction confirmation and completion)
        // CancellationToken.None: transaction completion must happen even if the client cancelled the request.
        // The token is marked used AFTER the pipeline completes successfully — on a failure it remains
        // available for a retry within the link's TTL
        try
        {
            // Email sends no in-channel prompt, so the outcome reaches the user only through the
            // sign-in page — the letter's path asks no question in the chat. The terminal wording comes
            // from the core through the receipt port; the error text stays the channel's own. The token of
            // the link identifies the letter: a second request of the same link costs no second attempt.
            await processor.ProcessLetterAsync(
                inboundResult,
                adapter,
                new ChannelReplyTexts(MessageTemplateNaturalKeys.OutcomeErrorInChannel),
                token,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error handling Email confirm through the pipeline. TransactionId: {TransactionId}",
                tokenData.TransactionId);

            return new EmailLetterOutcome.Refused(StatusCodes.Status500InternalServerError);
        }

        // The processing (ChannelInboundProcessor) does not return a success flag — we check the actual
        // result by reading the transaction state from the store (SPEC-016 §4.3).
        // The magic-link token is marked used ONLY when the transaction has actually
        // completed (Completed). On any failure (expired transaction, rate-limit,
        // concurrency conflict, completion error) the token remains available for a retry
        // within the link's TTL.
        var verifyTxResult = await services.GetRequiredService<ITransactionService>().GetTransactionAsync(
            confirmTransactionId,
            CancellationToken.None);

        // The confirmation went through and left the sign-in waiting for missing claims (SPEC-041 L91): a
        // success of this page, answered by the page of the completion. A sign-in a concurrent confirmation
        // took into the completion is no success of this letter and is refused below, the letter unspent.
        if (verifyTxResult.IsSuccess && IsClaimCompletionOfLetter(verifyTxResult.Value, tokenData))
        {
            return await AnswerClaimCompletionAsync(httpContext, token, verifyTxResult.Value, fromFastLink);
        }

        // Completed is a success of THIS letter only when the letter's mailbox is who completed it: a
        // sign-in completed by another subject — another letter, a chat — refused this confirmation
        // (SPEC-039 N34), and its receipt is not this page's to show. The letter stays unspent.
        var isCompleted = verifyTxResult.IsSuccess
            && verifyTxResult.Value.State is TransactionState.Completed
            && IsSubjectOfLetter(verifyTxResult.Value, tokenData);

        if (!isCompleted)
        {
            logger.LogWarning(
                "Email confirm pipeline did not finish the transaction in Completed for the mailbox of the letter. TransactionId: {TransactionId}, FinalState: {FinalState}",
                tokenData.TransactionId,
                verifyTxResult.IsSuccess ? verifyTxResult.Value.State.ToString() : "Unknown");

            return new EmailLetterOutcome.Refused(StatusCodes.Status409Conflict);
        }

        // Mark the token as used only after the transaction completes successfully (SPEC-016 §4.3).
        // If MarkUsedAsync returned false — the token was removed or concurrently marked by another thread;
        // the transaction is already Completed, but a repeated confirmation with the same magic link must be shown
        // as "the link is already used", so the UI and logs reflect the real outcome.
        var marked = await services.GetRequiredService<IEmailActionTokenStore>().MarkUsedAsync(token, CancellationToken.None);
        if (!marked)
        {
            return new EmailLetterOutcome.Refused(StatusCodes.Status409Conflict, LetterUsed: true);
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
                ChannelTypes.Email,
                verifyTxResult.Value.InitiatorContextSnapshot?.DeviceType),
            letter.Locale,
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

        return new EmailLetterOutcome.Completed(confirmedText);
    }

    /// <summary>
    /// Answers the "Continue signing in" of a sign-in the sign-in window decides — an effective surface other
    /// than None (SPEC-012 §4.4.2): the mailbox of the letter is attached and the sign-in keeps waiting for
    /// the answer of the window (<see cref="ChannelWebhookPipeline.AttachSignInLetterIdentityAsync"/>).
    /// </summary>
    /// <remarks>
    /// The letter has done its work once the mailbox is attached — it proved the mailbox, and the question
    /// is asked in the window, not here — so it is spent then, and the page sends the user back to the
    /// window. A letter a parallel request spent first is answered as used. A sign-in out of time is refused
    /// by its record before the identity of the mailbox is built, as the letter of a confirmation is
    /// (<see cref="ChannelWebhookPipeline.RefuseLetterOutOfTimeAsync"/>). A refusal leaves the letter unspent
    /// and is answered by the error of the page, with no detail of the refusal, which is not this user's
    /// business.
    /// </remarks>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="token">The action token of the letter, valid and not yet used.</param>
    /// <param name="tokenData">The data of the action token.</param>
    /// <param name="transaction">The sign-in the letter names, as the store holds it.</param>
    /// <param name="adapter">The Email channel adapter.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The sign-in passed on to the sign-in window, or the refusal of the letter — used or not attached.</returns>
    private static async Task<EmailLetterOutcome> ContinueSignInAsync(
        HttpContext httpContext,
        string token,
        EmailActionToken tokenData,
        Transaction transaction,
        IChannelAdapter adapter,
        ILogger logger)
    {
        var services = httpContext.RequestServices;
        var processor = services.GetRequiredService<ChannelInboundProcessor>();

        // CancellationToken.None: the user has acted, and the mailbox must be attached even if the browser
        // goes away — the window waits for it.
        var attachResult = await ChannelWebhookPipeline.RefuseLetterOutOfTimeAsync(processor, transaction, adapter)
            ?? await ChannelWebhookPipeline.AttachSignInLetterIdentityAsync(
                processor,
                transaction,
                await BuildPullIdentitySnapshotAsync(services, tokenData, transaction.GetTenantId(), logger),
                adapter,
                token,
                CancellationToken.None);

        if (attachResult.IsFailure)
        {
            logger.LogWarning(
                "Email confirm: the mailbox of the letter was not attached to the sign-in. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                transaction.Id.ToString(),
                attachResult.Error.Code);

            return new EmailLetterOutcome.Refused(StatusCodes.Status409Conflict);
        }

        if (!await services.GetRequiredService<IEmailActionTokenStore>().MarkUsedAsync(token, CancellationToken.None))
        {
            return new EmailLetterOutcome.Refused(StatusCodes.Status409Conflict, LetterUsed: true);
        }

        return new EmailLetterOutcome.SignInContinued();
    }

    /// <summary>
    /// Whether the sign-in was confirmed into the claims completion by this letter: it waits for missing
    /// claims, or the last of them came in and it is being finished (SPEC-041 L91), and its channel snapshot
    /// is the Pull confirmation of the address the letter was sent to.
    /// </summary>
    /// <remarks>
    /// Letters go out to any address while the sign-in is pending, and the form token goes to the device that
    /// confirmed the sign-in (SPEC-041 N78). The holder of another letter — to another address, or of a
    /// sign-in confirmed in a chat — confirmed nothing, so the page of the completion is not theirs.
    /// </remarks>
    /// <param name="transaction">The transaction of the letter.</param>
    /// <param name="tokenData">The action token of the letter.</param>
    /// <returns>true — the answer of the page is the one of the claims completion.</returns>
    private static bool IsClaimCompletionOfLetter(Transaction transaction, EmailActionToken tokenData) =>
        (transaction.IsCompletingClaims()
            || (transaction.State is TransactionState.Confirmed && transaction.ClaimCompletion is not null))
        && IsSubjectOfLetter(transaction, tokenData)
        && transaction.ChannelIdentitySnapshot is { AdditionalClaims: { } claims }
        && claims.TryGetValue(EmailAdapterConstants.ClaimVeriqaEmailMode, out var mode)
        && string.Equals(mode, EmailAdapterConstants.ModePull, StringComparison.Ordinal);

    /// <summary>
    /// Whether the channel snapshot of the transaction names the mailbox the letter was sent to — the
    /// subject (channel type and user identifier) the engine binds a transaction to (SPEC-039 N34).
    /// </summary>
    /// <param name="transaction">The transaction of the letter.</param>
    /// <param name="tokenData">The action token of the letter.</param>
    /// <returns>true — the transaction was answered by the mailbox of this letter.</returns>
    private static bool IsSubjectOfLetter(Transaction transaction, EmailActionToken tokenData) =>
        transaction.ChannelIdentitySnapshot is { } snapshot
        && string.Equals(snapshot.ChannelType, ChannelTypes.Email, StringComparison.Ordinal)
        && string.Equals(snapshot.ChannelUserId, tokenData.NormalizedEmail, StringComparison.Ordinal);

    /// <summary>
    /// Answers the confirmation page of a sign-in confirmed into the claims completion (SPEC-041 L52, N78).
    /// The form of the web step is shown here when its surface is the device that confirmed the sign-in;
    /// otherwise the page sends the user back to the device the sign-in was started on.
    /// </summary>
    /// <remarks>
    /// The confirmation is a success of the letter, so the letter is spent — except on the GET of the fast
    /// link while the form waits here: a mail gateway that follows links would otherwise spend the letter and
    /// be given the form token. That GET gets one "Continue" button instead, which posts the letter to this
    /// page; the letter is spent and the token issued on that POST only. A letter a parallel request spent
    /// first is answered as used, so of two requests only one gets the form.
    /// </remarks>
    /// <param name="httpContext">The request HTTP context.</param>
    /// <param name="token">The action token of the letter, valid and not yet used.</param>
    /// <param name="transaction">The transaction confirmed into the claims completion by this letter
    /// (<see cref="IsClaimCompletionOfLetter"/>) — the only one the page of the completion may be opened for.</param>
    /// <param name="fromFastLink">The request is the GET of the fast link.</param>
    /// <returns>The form on this device, the "Continue" page of the fast link, the refusal of a used letter or
    /// the way to the device the sign-in was started on.</returns>
    private static async Task<EmailLetterOutcome> AnswerClaimCompletionAsync(
        HttpContext httpContext,
        string token,
        Transaction transaction,
        bool fromFastLink)
    {
        var services = httpContext.RequestServices;

        var formOnThisDevice = transaction.GetCurrentClaimCompletionStep() is ClaimCompletionStep.WebStep
            && transaction.ClaimCompletion is { Surface: WebStepSurface.CrossDevicePage };

        if (fromFastLink && formOnThisDevice)
        {
            return new EmailLetterOutcome.ClaimFormContinue();
        }

        var tokenStore = services.GetRequiredService<IEmailActionTokenStore>();
        if (!await tokenStore.MarkUsedAsync(token, CancellationToken.None))
        {
            return new EmailLetterOutcome.Refused(StatusCodes.Status409Conflict, LetterUsed: true);
        }

        // The form is opened where the letter is answered, and the way to the device the sign-in was started
        // on is the answer when it cannot be (RenderLetterPageAsync and the entry of the code).
        return formOnThisDevice
            ? new EmailLetterOutcome.ClaimFormOnThisDevice(transaction.Id)
            : new EmailLetterOutcome.ContinueOnStartDevice();
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
    /// <param name="texts">Overrides of the page's built-in texts, resolved for its transaction.</param>
    /// <param name="isError">Flag of an error page.</param>
    /// <param name="isSuccess">Flag of a successful confirmation.</param>
    /// <param name="errorMessage">The error message (a Natural Key; localized here).</param>
    /// <param name="token">The action token (for the form of the page).</param>
    /// <param name="outcomeText">
    /// Terminal receipt of the transaction, ALREADY resolved and localized by the core
    /// (SPEC-036 TPL-123): the page states where the outcome is shown and renders what it is given.
    /// Required by the success branch and unused by the others; that branch also shows the line of a
    /// sign-in confirmed into the claims completion, resolved by the caller.
    /// </param>
    /// <param name="isClaimFormContinue">
    /// Flag of the page of the fast link while the form of the claims completion waits on this device: one
    /// "Continue" button posting the action token, nothing else (SPEC-041 N78).
    /// </param>
    /// <param name="isConfirmationRequest">
    /// Flag of the request page when the link is of a confirmation transaction: the same page as the request
    /// of a sign-in, worded for a confirmation — its title, line and continue button — with no subject and
    /// no sign-in wording (SPEC-039 E28).
    /// </param>
    /// <param name="isConfirmationLetter">
    /// Flag of a page answering the letter of a confirmation transaction with anything other than its
    /// request page — a refusal of its link or of the hand-over: the body is the one the other flags pick, and the title
    /// is the one of the request page of a confirmation, because the sign-in title would name a sign-in the
    /// user was not asked for.
    /// </param>
    /// <param name="isSignInContinued">
    /// Flag of the page a sign-in request turns into once "Continue signing in" passed the sign-in on: the
    /// line sending the user back to the sign-in window and a "Close" button.
    /// </param>
    /// <param name="appName">
    /// Name of the application the transaction is requested in — the request page only, which is the page
    /// no flag but <paramref name="isConfirmationRequest"/> names: a live link of a transaction.
    /// </param>
    /// <param name="continuesInSameBrowser">
    /// Flag of the request page whose transaction holds what the browser of the transaction is recognised
    /// by: the page carries the script that hides the button and sends the form by itself
    /// (<see cref="EmailLinkSameBrowser"/>). Ignored on any other page.
    /// </param>
    /// <param name="cspNonce">
    /// CSP nonce of the request, for the scripts of the request page and of
    /// <paramref name="isSignInContinued"/>; without one the scripts go out without the attribute.
    /// </param>
    /// <returns>The HTML string of the page.</returns>
    private static string BuildConfirmPageHtml(
        IConfirmationPromptLocalizer localizer,
        string? servedLocale,
        CorePageBranding? branding,
        CorePageTexts texts,
        bool isError = false,
        bool isSuccess = false,
        string? errorMessage = null,
        string? token = null,
        string? outcomeText = null,
        bool isClaimFormContinue = false,
        bool isConfirmationRequest = false,
        bool isConfirmationLetter = false,
        bool isSignInContinued = false,
        string? appName = null,
        bool continuesInSameBrowser = false,
        string? cspNonce = null)
    {
        // The method builds the magic link confirmation page, localized into the request language

        var htmlLang = ResolveServedHtmlLang(localizer, servedLocale);

        // The page no other flag names is the request of a transaction — a sign-in or, under
        // isConfirmationRequest, a confirmation; it and the page a sign-in request turns into after
        // "Continue signing in" carry the button that closes the tab.
        var closesTab = isSignInContinued
            || !(isSuccess || isError || isClaimFormContinue);

        var titleKey = isConfirmationRequest || isConfirmationLetter
            ? EmailAdapterMailStrings.ConfirmationContinueTitle
            : closesTab
                ? EmailAdapterMailStrings.SignInRequestTitle
                : EmailAdapterMailStrings.ConfirmPageTitle;
        var titleText = texts.Localize(localizer, titleKey, htmlLang);

        string statusBlock;
        string buttonBlock;
        var sameBrowserScript = string.Empty;

        if (isSuccess)
        {
            statusBlock = $"""
                <div class="veriqa-status success">{System.Net.WebUtility.HtmlEncode(outcomeText)}</div>
                """;
            buttonBlock = string.Empty;
        }
        else if (isSignInContinued)
        {
            statusBlock = $"""
                <p class="veriqa-instruction">{System.Net.WebUtility.HtmlEncode(texts.Localize(localizer, EmailAdapterMailStrings.SignInRequestContinued, htmlLang))}</p>
                """;
            buttonBlock = CorePageCloseAction.Row(
                texts.Localize(localizer, CorePageCloseAction.CloseText, htmlLang),
                texts.Localize(localizer, CorePageCloseAction.CloseHint, htmlLang));
        }
        else if (isClaimFormContinue)
        {
            // The claims completion: the confirmation is already given, and the button only spends the letter
            // and opens the form. It is a POST for the reason the request page below is: a scanner following
            // links must not do it.
            statusBlock = string.Empty;
            var encodedContinueToken = System.Net.WebUtility.HtmlEncode(token ?? string.Empty);
            buttonBlock = $"""
                <div class="veriqa-actions">
                  <form method="post">
                    <input type="hidden" name="{EmailAdapterConstants.TokenQueryParam}" value="{encodedContinueToken}" />
                    <button type="submit" class="veriqa-btn">{System.Net.WebUtility.HtmlEncode(texts.Localize(localizer, EmailAdapterMailStrings.ConfirmPageContinue, htmlLang))}</button>
                  </form>
                </div>
                """;
        }
        else if (isError)
        {
            var errorText = texts.Localize(localizer, errorMessage ?? EmailAdapterMailStrings.ConfirmPageError, htmlLang);
            statusBlock = $"""
                <div class="veriqa-status error">{System.Net.WebUtility.HtmlEncode(errorText)}</div>
                """;
            buttonBlock = string.Empty;
        }
        else
        {
            // The request of a transaction, one page for a sign-in and a confirmation: the page asks
            // nothing — the one question is the sign-in window's, or the core page's for a confirmation. The
            // line of a sign-in names the application, so that a recipient who did not request it knows the
            // page is to be closed; the line of a confirmation names none — the relying party words it for
            // its application through AuthPage.Texts (SPEC-016 EM-206). Otherwise only the words naming the
            // action differ by type. The continue button posts
            // to the current URL (GET and POST are mapped to a single path) — no action attribute, which
            // also works when the application is hosted under a sub-path (PathBase), where a root-relative
            // action would send the POST past the application. It is a POST, never a GET link, so a mail
            // scanner following links cannot pass the transaction on. "Cancel" only closes the tab and
            // changes nothing.
            var requestTextKey = isConfirmationRequest
                ? EmailAdapterMailStrings.ConfirmationRequestText
                : EmailAdapterMailStrings.SignInRequestText;
            var continueKey = isConfirmationRequest
                ? EmailAdapterMailStrings.ConfirmationRequestContinue
                : EmailAdapterMailStrings.SignInRequestContinue;
            var requestText = texts.Localize(localizer, requestTextKey, htmlLang)
                .Replace(ClaimLinkAppSlot, appName ?? EmailAdapterConfigDefaults.DefaultClientName, StringComparison.Ordinal);
            statusBlock = $"""
                <p class="veriqa-instruction">{System.Net.WebUtility.HtmlEncode(requestText)}</p>
                """;
            var encodedFormToken = System.Net.WebUtility.HtmlEncode(token ?? string.Empty);
            var cancel = CorePageCloseAction.BesideForm(
                texts.Localize(localizer, EmailAdapterMailStrings.CancelButton, htmlLang),
                texts.Localize(localizer, CorePageCloseAction.CloseHint, htmlLang));
            buttonBlock = $"""
                <div class="veriqa-actions">
                  <form method="post" id="{EmailLinkSameBrowser.FormId}">
                    <input type="hidden" name="{EmailAdapterConstants.TokenQueryParam}" value="{encodedFormToken}" />
                    <button type="submit" class="veriqa-btn" id="{EmailLinkSameBrowser.ButtonId}">{System.Net.WebUtility.HtmlEncode(texts.Localize(localizer, continueKey, htmlLang))}</button>
                  </form>
                  {cancel}
                </div>
                """;
            sameBrowserScript = continuesInSameBrowser ? EmailLinkSameBrowser.Script(cspNonce) : string.Empty;
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="{{htmlLang}}" {{CorePageHead.BuildThemeAttribute(branding)}}>
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0" />
              <title>{{System.Net.WebUtility.HtmlEncode(titleText)}}</title>
              {{CorePageHead.BuildServicePageHead(branding, closesTab ? CorePageCloseAction.Css : null)}}
            </head>
            <body>
              <main class="veriqa-card">
                <h1 class="veriqa-title">{{System.Net.WebUtility.HtmlEncode(titleText)}}</h1>
                {{statusBlock}}
                {{buttonBlock}}
              </main>
              {{(closesTab ? CorePageCloseAction.Script(cspNonce) : string.Empty)}}
              {{sameBrowserScript}}
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

    /// <summary>
    /// The answer of an accepted Pull start.
    /// </summary>
    /// <param name="Success">Always <c>true</c>.</param>
    /// <param name="MaskedEmail">The address the mail went to, masked.</param>
    /// <param name="RetryAfterSeconds">Seconds before the next mail is accepted; zero — no interval.</param>
    /// <param name="CodeExpected">The mail carries a code, and the page asks for it.</param>
    private sealed record EmailStartAccepted(
        [property: JsonPropertyName(EmailPullJsonFields.Success)] bool Success,
        [property: JsonPropertyName(EmailPullJsonFields.MaskedEmail)] string MaskedEmail,
        [property: JsonPropertyName(EmailPullJsonFields.RetryAfterSeconds)] int RetryAfterSeconds,
        [property: JsonPropertyName(EmailPullJsonFields.CodeExpected)] bool CodeExpected);

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
