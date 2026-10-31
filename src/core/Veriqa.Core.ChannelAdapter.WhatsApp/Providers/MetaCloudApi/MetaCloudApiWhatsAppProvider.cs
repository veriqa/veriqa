// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.ChannelAdapter.WhatsApp.Abstractions;
using Veriqa.Core.ChannelAdapter.WhatsApp.Configuration;
using Veriqa.Core.ChannelAdapter.WhatsApp.Constants;
using Veriqa.Core.TransactionEngine.Common;

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Providers.MetaCloudApi;

/// <summary>
/// WhatsApp message delivery provider via the Meta Cloud API (Graph API).
/// The official integration path — requires a WhatsApp Business Account. Owns the Meta side of the inbound
/// path as well: the GET Hub Challenge handshake, the <c>X-Hub-Signature-256</c> signature of a POST, and the
/// webhook JSON format (<see cref="MetaCloudApiWebhookReader"/>).
/// </summary>
internal sealed class MetaCloudApiWhatsAppProvider : IWhatsAppProvider
{
    /// <summary>
    /// HTTP client factory for Graph API calls.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// WhatsApp adapter settings — kept ONLY for the infrastructure-level Graph API endpoint fields
    /// (<c>GraphApiBaseUrl</c>/<c>GraphApiVersion</c>), which are not tenant-credential (audit verdict OK).
    /// Tenant-credential fields (<c>PhoneNumberId</c>/<c>AccessToken</c>) are resolved per operation via the seam.
    /// </summary>
    private readonly WhatsAppOptions _options;

    /// <summary>
    /// Per-tenant channel credential provider (seam CA-165). Tenant-credential fields
    /// (<c>PhoneNumberId</c>/<c>AccessToken</c>) are resolved per operation by the ambient tenant, never captured
    /// in the constructor — self-hosted (tenant=null) stays the default N=1 path (1:1 behavior).
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<MetaCloudApiWhatsAppProvider> _logger;

    /// <summary>
    /// Creates an instance of <see cref="MetaCloudApiWhatsAppProvider"/>.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="options">Adapter settings (infrastructure-level Graph API endpoint fields only).</param>
    /// <param name="resolver">Canonical layer resolver (source of the tenant's credentials).</param>
    /// <param name="logger">Logger.</param>
    public MetaCloudApiWhatsAppProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<WhatsAppOptions> options,
        IConfigurationResolver resolver,
        ILogger<MetaCloudApiWhatsAppProvider> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ProviderType => WhatsAppProviderNames.MetaCloudApi;

    /// <inheritdoc />
    public async Task<bool> SendTextMessageAsync(
        string recipientUserId,
        string messageText,
        CancellationToken cancellationToken = default)
    {
        // The method sends a text message via the Graph API.
        var requestBody = new
        {
            messaging_product = MetaCloudApiConstants.MessagingProduct,
            recipient = recipientUserId,
            type = MetaCloudApiConstants.OutboundTypeText,
            text = new
            {
                body = messageText
            }
        };

        return await SendGraphMessageAsync(
            requestBody,
            recipientUserId,
            MetaCloudApiConstants.OutboundTypeText,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> SendInteractiveButtonsMessageAsync(
        string recipientUserId,
        string bodyText,
        string confirmButtonId,
        string confirmButtonTitle,
        string declineButtonId,
        string declineButtonTitle,
        CancellationToken cancellationToken = default)
        => SendReplyButtonsMessageAsync(
            recipientUserId,
            bodyText,
            [
                new WhatsAppReplyOption(confirmButtonId, confirmButtonTitle),
                new WhatsAppReplyOption(declineButtonId, declineButtonTitle)
            ],
            cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// The request contact info message carries its own button and no other (Meta, business-scoped user
    /// IDs), so the reply button of the secondary action goes as a second interactive message right after
    /// it; its body repeats the button text, the one text the step gives it.
    /// </remarks>
    public async Task<bool> SendContactRequestAsync(
        string recipientUserId,
        string bodyText,
        string secondaryButtonId,
        string secondaryButtonTitle,
        CancellationToken cancellationToken = default)
    {
        var contactRequestBody = new
        {
            messaging_product = MetaCloudApiConstants.MessagingProduct,
            recipient = recipientUserId,
            type = MetaCloudApiConstants.OutboundTypeInteractive,
            interactive = new
            {
                type = MetaCloudApiConstants.InteractiveTypeRequestContactInfo,
                body = new
                {
                    text = bodyText
                },
                action = new
                {
                    name = MetaCloudApiConstants.InteractiveTypeRequestContactInfo
                }
            }
        };

        var requestSent = await SendGraphMessageAsync(
            contactRequestBody,
            recipientUserId,
            MetaCloudApiConstants.InteractiveTypeRequestContactInfo,
            cancellationToken);

        if (!requestSent)
        {
            return false;
        }

        return await SendReplyButtonsMessageAsync(
            recipientUserId,
            secondaryButtonTitle,
            [new WhatsAppReplyOption(secondaryButtonId, secondaryButtonTitle)],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> SendLinkButtonMessageAsync(
        string recipientUserId,
        string bodyText,
        string buttonText,
        string url,
        CancellationToken cancellationToken = default)
    {
        // The address may carry a claim form token (SPEC-041 N78): it goes to the platform in the body and
        // nowhere else — the send log names the message type and the recipient fingerprint only.
        var requestBody = new
        {
            messaging_product = MetaCloudApiConstants.MessagingProduct,
            recipient = recipientUserId,
            type = MetaCloudApiConstants.OutboundTypeInteractive,
            interactive = new
            {
                type = MetaCloudApiConstants.InteractiveTypeCtaUrl,
                body = new
                {
                    text = bodyText
                },
                action = new
                {
                    name = MetaCloudApiConstants.InteractiveTypeCtaUrl,
                    parameters = new
                    {
                        display_text = buttonText,
                        url
                    }
                }
            }
        };

        return SendGraphMessageAsync(
            requestBody,
            recipientUserId,
            MetaCloudApiConstants.InteractiveTypeCtaUrl,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> SendReplyButtonsMessageAsync(
        string recipientUserId,
        string bodyText,
        IReadOnlyList<WhatsAppReplyOption> buttons,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buttons);

        var requestBody = new
        {
            messaging_product = MetaCloudApiConstants.MessagingProduct,
            recipient = recipientUserId,
            type = MetaCloudApiConstants.OutboundTypeInteractive,
            interactive = new
            {
                type = MetaCloudApiConstants.InteractiveTypeButton,
                body = new
                {
                    text = bodyText
                },
                action = new
                {
                    buttons = buttons
                        .Select(button => new
                        {
                            type = MetaCloudApiConstants.ReplyButtonType,
                            reply = new
                            {
                                id = button.Id,
                                title = button.Title
                            }
                        })
                        .ToArray()
                }
            }
        };

        return SendGraphMessageAsync(
            requestBody,
            recipientUserId,
            MetaCloudApiConstants.InteractiveTypeButton,
            cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The platform requires a title for every section, so the one section of the list is titled with the
    /// caption of the button that opens it.
    /// </remarks>
    public Task<bool> SendListMessageAsync(
        string recipientUserId,
        string bodyText,
        string listButtonText,
        IReadOnlyList<WhatsAppReplyOption> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var requestBody = new
        {
            messaging_product = MetaCloudApiConstants.MessagingProduct,
            recipient = recipientUserId,
            type = MetaCloudApiConstants.OutboundTypeInteractive,
            interactive = new
            {
                type = MetaCloudApiConstants.InteractiveTypeList,
                body = new
                {
                    text = bodyText
                },
                action = new
                {
                    button = listButtonText,
                    sections = new[]
                    {
                        new
                        {
                            title = listButtonText,
                            rows = rows
                                .Select(row => new
                                {
                                    id = row.Id,
                                    title = row.Title
                                })
                                .ToArray()
                        }
                    }
                }
            }
        };

        return SendGraphMessageAsync(
            requestBody,
            recipientUserId,
            MetaCloudApiConstants.InteractiveTypeList,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(bool IsHealthy, string? Details)> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        // The method checks Graph API availability and the validity of the access token.
        try
        {
            // The probe runs outside any request, so no host path has stated whose credentials it
            // should read — and the installation's own (the default implicit tenant) is the answer it
            // wants: a health endpoint reports the state of THIS deployment, not of one of its
            // tenants. That is said explicitly rather than by omission, so that the read is not
            // mistaken for a request path that forgot to state its tenant (CA-164).
            using var tenantScope = ChannelTenantContext.BeginScope(tenantId: null);

            // One credential resolve per operation (CA-171): PhoneNumberId/AccessToken are tenant-credential.
            var credentialsResult = await WhatsAppCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
            if (credentialsResult.IsFailure)
            {
                _logger.LogError("Meta Cloud API health check: WhatsApp credentials are unavailable.");
                return (false, "WhatsApp credentials are unavailable.");
            }

            var tenantMeta = credentialsResult.Value.MetaCloudApi;

            // GraphApiVersion is infrastructure-level (audit verdict OK) — stays from the global IOptions.
            var graph = _options.MetaCloudApi;
            var endpointPath = $"/{graph.GraphApiVersion}/{tenantMeta.PhoneNumberId}";
            var endpointUrl = BuildGraphApiUri(endpointPath);

            using var request = new HttpRequestMessage(HttpMethod.Get, endpointUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                MetaCloudApiConstants.BearerScheme,
                tenantMeta.AccessToken);

            var client = _httpClientFactory.CreateClient(MetaCloudApiConstants.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"HTTP {(int)response.StatusCode}");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Meta Cloud API health check failed.");
            return (false, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ValidateWebhookAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // The method validates the GET Hub Challenge and the POST webhook signature.
        // The envelope itself is already buffered; what is awaited is the credential resolution, which
        // both checks need — the verify token and the app secret are tenant-credential fields.
        if (string.Equals(request.Method, HttpMethods.Get, StringComparison.Ordinal))
        {
            return await ValidateWebhookChallengeRequestAsync(request, cancellationToken);
        }

        if (!string.Equals(request.Method, HttpMethods.Post, StringComparison.Ordinal))
        {
            return Result<bool>.Success(false);
        }

        return await ValidateMetaWebhookSignatureAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public WhatsAppInboundEvent ReadInboundEvent(ChannelInboundRequest request) =>
        MetaCloudApiWebhookReader.Read(request.Body.Span, _logger);

    /// <summary>
    /// Validates the GET Hub Challenge request.
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result.</returns>
    private async Task<Result<bool>> ValidateWebhookChallengeRequestAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken)
    {
        // The method checks the hub.mode and hub.verify_token parameters.
        request.Query.TryGetValue(MetaCloudApiConstants.HubModeQueryKey, out var mode);
        request.Query.TryGetValue(MetaCloudApiConstants.HubVerifyTokenQueryKey, out var verifyToken);

        if (!string.Equals(
                mode,
                MetaCloudApiConstants.HubSubscribeMode,
                StringComparison.Ordinal))
        {
            return Result<bool>.Success(false);
        }

        // One credential resolve per operation (CA-171): the verify token is a tenant-credential field.
        var credentialsResult = await WhatsAppCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogError("WhatsApp credentials are unavailable for webhook challenge validation (Meta Cloud API).");
            return Result<bool>.Success(false);
        }

        var configuredToken = credentialsResult.Value.MetaCloudApi.WebhookVerifyToken;
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            _logger.LogError("WebhookVerifyToken is not configured for the WhatsApp adapter (Meta Cloud API).");
            return Result<bool>.Success(false);
        }

        // Compare fixed-length SHA-256 hashes (32 bytes) via FixedTimeEquals (CA-033):
        // the comparison is always constant-length, which eliminates the verify-token length leak through an early
        // return on length mismatch and preserves constant time.
        Span<byte> configuredTokenHash = stackalloc byte[32];
        Span<byte> incomingTokenHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(configuredToken), configuredTokenHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(verifyToken ?? string.Empty), incomingTokenHash);

        var isValid = CryptographicOperations.FixedTimeEquals(
            configuredTokenHash,
            incomingTokenHash);
        return Result<bool>.Success(isValid);
    }

    /// <summary>
    /// Validates the POST webhook request signature (<c>X-Hub-Signature-256</c>).
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result.</returns>
    private async Task<Result<bool>> ValidateMetaWebhookSignatureAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken)
    {
        // The method verifies the HMAC-SHA256 signature of the request body.
        try
        {
            request.Headers.TryGetValue(MetaCloudApiConstants.SignatureHeader, out var signatureHeader);
            if (string.IsNullOrWhiteSpace(signatureHeader))
            {
                _logger.LogWarning(
                    "WhatsApp webhook rejected: header {SignatureHeader} is missing.",
                    MetaCloudApiConstants.SignatureHeader);
                return Result<bool>.Success(false);
            }

            if (!signatureHeader.StartsWith(
                    MetaCloudApiConstants.SignaturePrefix,
                    StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "WhatsApp webhook rejected: the signature does not contain the {Prefix} prefix.",
                    MetaCloudApiConstants.SignaturePrefix);
                return Result<bool>.Success(false);
            }

            // One credential resolve per operation (CA-171): the app secret is a tenant-credential field.
            var credentialsResult = await WhatsAppCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
            if (credentialsResult.IsFailure)
            {
                _logger.LogError("WhatsApp webhook rejected: credentials are unavailable.");
                return Result<bool>.Success(false);
            }

            var appSecret = credentialsResult.Value.MetaCloudApi.AppSecret;
            if (string.IsNullOrWhiteSpace(appSecret))
            {
                _logger.LogError("WhatsApp webhook rejected: AppSecret is not configured.");
                return Result<bool>.Success(false);
            }

            // The envelope carries the body byte for byte, so the HMAC is computed over exactly what
            // arrived — no decoding step in between that could swallow a byte-order mark.
            var bodyBytes = request.Body.Span;
            var expectedSignatureHex = signatureHeader[MetaCloudApiConstants.SignaturePrefix.Length..]
                .ToLowerInvariant();
            var computedHash = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(appSecret),
                bodyBytes);

            var computedSignatureHex = Convert.ToHexStringLower(computedHash);

            // Compare fixed-length SHA-256 hashes (32 bytes) via FixedTimeEquals (CA-033):
            // comparing HMAC signatures is always constant-length, which eliminates the length leak through an early
            // return on length mismatch and preserves constant time.
            Span<byte> expectedSignatureHash = stackalloc byte[32];
            Span<byte> computedSignatureHash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(expectedSignatureHex), expectedSignatureHash);
            SHA256.HashData(Encoding.UTF8.GetBytes(computedSignatureHex), computedSignatureHash);

            var isValid = CryptographicOperations.FixedTimeEquals(
                expectedSignatureHash,
                computedSignatureHash);

            if (!isValid)
            {
                _logger.LogWarning("WhatsApp webhook rejected: invalid signature.");
            }

            return Result<bool>.Success(isValid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WhatsApp webhook validation error.");
            return Result<bool>.Failure(
                ChannelAdapterErrorCodes.WebhookValidationFailed,
                "WhatsApp webhook validation error.");
        }
    }

    /// <summary>
    /// Sends a message via the WhatsApp Graph API.
    /// </summary>
    /// <param name="requestBody">Request body.</param>
    /// <param name="channelUserId">Business-scoped user ID (BSUID) of the recipient.</param>
    /// <param name="messageType">Outbound message type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the send succeeded.</returns>
    private async Task<bool> SendGraphMessageAsync(
        object requestBody,
        string channelUserId,
        string messageType,
        CancellationToken cancellationToken)
    {
        // The method sends a POST request to the Graph API and handles the response codes.
        try
        {
            // One credential resolve per operation (CA-171): PhoneNumberId/AccessToken are tenant-credential.
            var credentialsResult = await WhatsAppCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
            if (credentialsResult.IsFailure)
            {
                _logger.LogError(
                    "Cannot send a WhatsApp (Meta) message: credentials are unavailable. Type={MessageType}, ChannelUserIdHash={ChannelUserIdHash}.",
                    messageType,
                    LogMasking.Fingerprint(channelUserId));

                return false;
            }

            var tenantMeta = credentialsResult.Value.MetaCloudApi;

            // GraphApiVersion is infrastructure-level (audit verdict OK) — stays from the global IOptions.
            var graph = _options.MetaCloudApi;
            var messagesPath = $"/{graph.GraphApiVersion}/{tenantMeta.PhoneNumberId}/{MetaCloudApiConstants.GraphMessagesResourceSegment}";
            var endpointUrl = BuildGraphApiUri(messagesPath);
            var requestJson = JsonSerializer.Serialize(requestBody);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                MetaCloudApiConstants.BearerScheme,
                tenantMeta.AccessToken);
            request.Content = new StringContent(
                requestJson,
                Encoding.UTF8,
                MetaCloudApiConstants.ApplicationJsonContentType);

            var client = _httpClientFactory.CreateClient(MetaCloudApiConstants.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Error sending a WhatsApp (Meta) message. Type={MessageType}, ChannelUserIdHash={ChannelUserIdHash}, StatusCode={StatusCode}.",
                    messageType,
                    LogMasking.Fingerprint(channelUserId),
                    (int)response.StatusCode);

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Exception while sending a WhatsApp (Meta) message. Type={MessageType}, ChannelUserIdHash={ChannelUserIdHash}.",
                messageType,
                LogMasking.Fingerprint(channelUserId));

            return false;
        }
    }

    /// <summary>
    /// Builds an absolute Graph API URI.
    /// </summary>
    /// <param name="resourcePath">Relative resource path.</param>
    /// <returns>Absolute URI.</returns>
    private Uri BuildGraphApiUri(string resourcePath)
    {
        // The method combines GraphApiBaseUrl and the resource path into a safe absolute URI.
        var baseUrl = _options.MetaCloudApi.GraphApiBaseUrl.TrimEnd('/');
        var path = resourcePath.StartsWith("/", StringComparison.Ordinal)
            ? resourcePath
            : "/" + resourcePath;

        return new Uri(baseUrl + path, UriKind.Absolute);
    }
}
