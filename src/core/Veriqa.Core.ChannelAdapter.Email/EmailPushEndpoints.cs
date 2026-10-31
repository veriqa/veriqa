// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
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
using Veriqa.Core.Configuration;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.ChannelAdapter.Email;

/// <summary>
/// Email Push mode endpoints (SPEC-016 §5): the inbound webhook.
/// Push mode is enabled only when <see cref="EmailOptions.PushEnabled"/> = true and requires
/// an external inbound provider. At the code level, the contract and orchestration are implemented.
/// </summary>
public static partial class EmailAuthEndpoints
{
    /// <summary>
    /// Processes an incoming email from the Push mode inbound provider.
    /// Authenticates the request (tenant, mode, provider, webhook secret), parses the body, and hands the
    /// mail to <see cref="EmailInboundMailPipeline"/>, whose outcome it turns into the answer to the provider.
    /// Returns generic JSON without disclosing details to the caller.
    /// </summary>
    private static async Task<IResult> HandleEmailInboundWebhookAsync(HttpContext httpContext)
    {
        // The method accepts an inbound email webhook and hands the parsed mail to the shared handler

        var services = httpContext.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(EmailAuthEndpoints));

        // Tenant demux (CA-167/CA-168/CA-185), the shared one: an inbound mailbox of a tenant is
        // configured at the provider with the {tenant} segment in the callback URL, and that segment
        // is what states whose mail this is — the body cannot, it is not read until the request is
        // authenticated. No segment ⇒ the default implicit tenant, behaviour 1:1. A segment naming a
        // tenant the Email channel is not enabled for is refused with 403 and no details. The
        // resolver is asked for optionally: a host that mapped the route without registering the
        // channel configuration has nothing to authorize a segment against, so the demux fails closed.
        var tenantResolution = await ChannelTenantDemux.ResolveTenantAsync(
            httpContext,
            services.GetService<IConfigurationResolver>(),
            ChannelTypes.Email,
            logger,
            httpContext.RequestAborted);

        if (!tenantResolution.Allowed)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        // Everything below — the Inbound.* group, the webhook secret it is checked against, the
        // identity of the confirming mail — is resolved for this tenant (CA-162/CA-164).
        using var tenantScope = ChannelTenantContext.BeginScope(tenantResolution.TenantId);

        // Enabled — the channels_enabled axis (§17.7); PushEnabled — core-transport (CFG-230).
        // Read through the adapter's guarded entry point (TryReadEmailOptions): the webhook is a live
        // path just like the pages, and here an unhandled exception would also lose the sign-in mail.
        var emailOptions = TryReadEmailOptions(services, logger);
        if (emailOptions is null)
        {
            // A configuration an operator can repair — the same transient class as a failed completion
            // of the mail, so the same answer: 503 + Retry-After keeps the mail queued at the provider. A 404
            // here would read as a permanent rejection and drop the sign-in mail for good.
            httpContext.Response.Headers.RetryAfter = EmailAdapterConstants.InboundRetryAfterSeconds;
            return Results.Json(
                new { success = false },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!emailOptions.Enabled || !emailOptions.PushEnabled)
        {
            // The snapshot is fixed at its first read, so a Push mode switched on by a configuration
            // reload after that read is invisible to it (SPEC-016 EM-054). Only the current PushEnabled
            // tells a mode that is really off (404) from one the host was not started with — the latter
            // is the same transient class as a missing processor below, so the same answer: 503 +
            // Retry-After keeps the sign-in mail queued at the provider. Enabled stays the snapshot's:
            // it states the channel registration, which a reload does not change.
            if (emailOptions.Enabled && IsPushEnabledInCurrentSettings(services, logger))
            {
                logger.LogWarning(
                    "Email inbound webhook: Push is enabled in the current configuration but disabled in the "
                    + "settings snapshot this host serves from — Push was most likely switched on after the host "
                    + "started. The channel composition is fixed at startup; restart the host to serve inbound mail.");
                httpContext.Response.Headers.RetryAfter = EmailAdapterConstants.InboundRetryAfterSeconds;
                return Results.Json(
                    new { success = false },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            logger.LogWarning("Email inbound webhook called but the adapter or Push mode is disabled.");
            return Results.Json(
                new { success = false },
                statusCode: StatusCodes.Status404NotFound);
        }

        // Inbound.* is a tenant-credential group field — resolve it through the seam (CA-162).
        // A resolution failure yields 404 without disclosing the reason to the caller (no configuration
        // leak into the response body, core-rules §10).
        var credentialsResult = await EmailCredentialsResolver.ResolveAsync(
            services.GetRequiredService<IConfigurationResolver>(),
            logger,
            httpContext.RequestAborted);
        if (credentialsResult.IsFailure)
        {
            logger.LogWarning(
                "Email inbound webhook: failed to resolve the tenant Email credentials. Error: {ErrorCode}",
                credentialsResult.Error.Code);
            return Results.Json(
                new { success = false },
                statusCode: StatusCodes.Status404NotFound);
        }

        var inbound = credentialsResult.Value.Inbound;

        // The webhook endpoint is active only when Provider=Webhook. For other inbound providers
        // (ImapPolling/SmtpRelay/Custom) the webhook secret is neither validated nor required by the validator,
        // so the endpoint is explicitly unavailable (404) — otherwise it would mask a configuration error
        // with constant 401 "secret not configured" responses (review feedback).
        if (inbound.Provider is not EmailInboundProvider.Webhook)
        {
            logger.LogWarning(
                "Email inbound webhook called but the inbound provider is not Webhook (Provider={Provider}).",
                inbound.Provider);
            return Results.Json(
                new { success = false },
                statusCode: StatusCodes.Status404NotFound);
        }

        // Validate the webhook secret (protection against request forgery, CA-033)
        if (!ValidateInboundWebhookSecret(httpContext.Request, inbound, logger))
        {
            return Results.Json(
                new { success = false },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // Precondition: the inbound processor must actually be in the container. Resolved optionally,
        // and here rather than at the point of use, because the PushEnabled guard above reads an IOptions
        // snapshot that materializes on first access and may therefore pick up a configuration reloaded
        // after startup, while the processor registration is fixed at startup and stays under PushEnabled
        // (it cannot be registered unconditionally — its required ITransactionEventPublisher comes from
        // the transaction engine, so that would stop a Pull-only host wired without it from starting).
        // Checking it up front keeps the mismatch free of side effects: the dedup lookup and the
        // mail_received status of the shared handler must not run for a request we are about to refuse.
        var processor = services.GetService<IEmailInboundProcessor>();
        if (processor is null)
        {
            // A transient failure awaiting recovery — the same class as a failed completion of the mail, so the
            // same answer: 503 + Retry-After, which keeps the mail queued at the provider instead of
            // dropping it. A 404 here would read as a permanent rejection and lose the sign-in mail for
            // good, and the operator's configuration does say Push is on.
            logger.LogWarning(
                "Email inbound webhook: Push is enabled in the configuration but no inbound processor is "
                + "registered — Push was most likely switched on after the host started. The channel "
                + "composition is fixed at startup; restart the host to serve inbound mail.");
            httpContext.Response.Headers.RetryAfter = EmailAdapterConstants.InboundRetryAfterSeconds;
            return Results.Json(
                new { success = false },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Hard limit on the webhook body size (protection against unbounded buffering / DoS).
        // An honest Content-Length is rejected immediately with 413; chunked/forged is handled by capped reading in the parser.
        if (httpContext.Request.ContentLength > EmailAdapterConstants.MaxInboundBodyBytes)
        {
            logger.LogWarning(
                "Email inbound webhook: request body exceeds the {Limit} byte limit.",
                EmailAdapterConstants.MaxInboundBodyBytes);
            return Results.Json(
                new { success = false },
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        // Parse the request body into InboundEmailMessage (tolerant of missing optional fields)
        InboundEmailMessage? message;
        try
        {
            message = await ParseInboundMessageAsync(
                httpContext.Request,
                services.GetRequiredService<TimeProvider>(),
                httpContext.RequestAborted);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Email inbound webhook: malformed JSON in the request body.");
            return Results.Json(
                new { success = false },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (message is null || string.IsNullOrWhiteSpace(message.MessageId))
        {
            logger.LogWarning("Email inbound webhook: required message-id field is missing.");
            return Results.Json(
                new { success = false },
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Everything from the dedup lookup on is the shared handler of an inbound mail — the same one
        // a mailbox read by polling goes through; the webhook only turns its outcome into the answer
        // to the provider.
        var outcome = await EmailInboundMailPipeline.HandleAsync(
            services, processor, inbound, message, httpContext.RequestAborted);

        switch (outcome)
        {
            case EmailInboundMailOutcome.Completed:
                return Results.Json(new { success = true });

            case EmailInboundMailOutcome.RetryLater:
                // An internal transient failure awaiting recovery: 503 + Retry-After makes the inbound
                // provider retry delivery — a 200 OK would lose the email.
                httpContext.Response.Headers.RetryAfter = EmailAdapterConstants.InboundRetryAfterSeconds;
                return Results.Json(
                    new { success = false },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            default:
                // Rejected: the mail confirmed nothing, or a redelivery of it cannot help.
                return Results.Json(new { success = false });
        }
    }

    /// <summary>
    /// Answers whether Push mode is enabled in the current Email configuration — the value a reload after
    /// the <see cref="IOptions{TOptions}"/> snapshot was taken has already reached.
    /// </summary>
    /// <remarks>
    /// The read is guarded the same way as <c>TryReadEmailOptions</c>: a reloaded section that no longer
    /// binds or validates must not throw out of the webhook. Such a read answers <see langword="false"/>,
    /// so the snapshot's own answer stands — a switch-on that cannot be read is not reported as one.
    /// </remarks>
    /// <param name="services">Request service provider.</param>
    /// <param name="logger">Logger of the failed read.</param>
    /// <returns><see langword="true"/> — the current configuration has Push mode enabled.</returns>
    private static bool IsPushEnabledInCurrentSettings(IServiceProvider services, ILogger logger)
    {
        try
        {
            return services.GetService<IOptionsMonitor<EmailOptions>>()?.CurrentValue.PushEnabled is true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Email inbound webhook: the current Email settings could not be read, so the settings snapshot decides whether Push is enabled.");

            return false;
        }
    }

    /// <summary>
    /// Validates the inbound email webhook secret using a fixed-time comparison (CA-033).
    /// </summary>
    /// <param name="request">The webhook HTTP request.</param>
    /// <param name="inbound">Inbound processing settings.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>true — the secret is valid.</returns>
    private static bool ValidateInboundWebhookSecret(
        HttpRequest request,
        EmailInboundOptions inbound,
        ILogger logger)
    {
        // The method compares the secret header against the configured value in constant time
        var configuredSecret = inbound.WebhookSecretToken;

        // If the secret is not configured — reject the request (inbound webhook protection)
        if (string.IsNullOrEmpty(configuredSecret))
        {
            logger.LogError(
                "Email inbound webhook: secret is not configured. Request rejected. Header {Header}.",
                EmailAdapterConstants.WebhookSecretHeader);
            return false;
        }

        var headerValue = request.Headers[EmailAdapterConstants.WebhookSecretHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(headerValue))
        {
            logger.LogWarning(
                "Email inbound webhook: header {Header} is missing.",
                EmailAdapterConstants.WebhookSecretHeader);
            return false;
        }

        // Compare fixed-length SHA-256 hashes (32 bytes): the comparison is always constant-length,
        // which eliminates leaking the secret length via an early return and preserves constant time (CA-033).
        Span<byte> expectedHash = stackalloc byte[32];
        Span<byte> actualHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(configuredSecret), expectedHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(headerValue), actualHash);

        var isValid = CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        if (!isValid)
        {
            logger.LogWarning("Email inbound webhook: invalid secret.");
        }

        return isValid;
    }

    /// <summary>
    /// Parses the webhook request body into <see cref="InboundEmailMessage"/>.
    /// Tolerant of missing optional fields.
    /// </summary>
    /// <param name="request">The webhook HTTP request.</param>
    /// <param name="timeProvider">Clock supplying the receipt moment when the payload omits it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The parsed message, or null if the JSON root is not an object. For an empty or
    /// malformed body, a <see cref="JsonException"/> is thrown (handled by the calling code).
    /// </returns>
    private static async Task<InboundEmailMessage?> ParseInboundMessageAsync(
        HttpRequest request,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // The method reads provider-agnostic JSON and assembles an InboundEmailMessage.
        // We read the body with a hard size limit — protection against unbounded buffering when Content-Length
        // is missing or forged (chunked transfer). The chunked copy under a limit is the component's
        // shared one; the limit and the exception it raises are this endpoint's own: exceeding the
        // limit → JsonException (→ 400).
        await using var bounded = new MemoryStream();
        await BoundedBodyReader.CopyAsync(
            request.Body,
            bounded,
            EmailAdapterConstants.MaxInboundBodyBytes,
            static () => new JsonException("Inbound webhook body exceeds the maximum allowed size."),
            cancellationToken);

        bounded.Position = 0;
        using var document = await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (root.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        var messageId = ReadString(root, EmailInboundJsonFields.MessageId);
        var from = ReadString(root, EmailInboundJsonFields.From);
        var to = ReadString(root, EmailInboundJsonFields.To);
        var subject = ReadString(root, EmailInboundJsonFields.Subject);

        var receivedAt = ReadDateTimeOffset(root, EmailInboundJsonFields.ReceivedAt)
            ?? timeProvider.GetUtcNow();

        return new InboundEmailMessage(
            MessageId: messageId ?? string.Empty,
            FromAddress: from ?? string.Empty,
            EnvelopeSender: ReadString(root, EmailInboundJsonFields.EnvelopeSender),
            ToAddress: to ?? string.Empty,
            Subject: subject ?? string.Empty,
            Body: ReadString(root, EmailInboundJsonFields.Body),
            ReceivedAt: receivedAt,
            SpfResult: ReadString(root, EmailInboundJsonFields.Spf),
            DkimResult: ReadString(root, EmailInboundJsonFields.Dkim),
            DmarcResult: ReadString(root, EmailInboundJsonFields.Dmarc),
            ProviderVerifiedSender: ReadString(root, EmailInboundJsonFields.ProviderVerifiedSender));
    }

    /// <summary>
    /// Safely reads a JSON string property (with a ValueKind check).
    /// </summary>
    /// <param name="element">The JSON object.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The string value or null.</returns>
    private static string? ReadString(JsonElement element, string propertyName)
    {
        // The method checks ValueKind before calling GetString (protection against malformed data)
        if (element.TryGetProperty(propertyName, out var value)
            && value.ValueKind is JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    /// <summary>
    /// Safely reads a <see cref="DateTimeOffset"/> value from a JSON string property.
    /// </summary>
    /// <param name="element">The JSON object.</param>
    /// <param name="propertyName">The property name.</param>
    /// <returns>The value, or null if absent or unparseable.</returns>
    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        // The method parses an ISO-8601 string into a DateTimeOffset
        var raw = ReadString(element, propertyName);
        if (raw is not null
            && DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
