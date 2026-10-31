// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net.Http.Headers;
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

namespace Veriqa.Core.ChannelAdapter.WhatsApp.Providers.Twilio;

/// <summary>
/// WhatsApp message delivery provider via the Twilio Messages API. Talks to the REST API over plain HTTP (no
/// Twilio SDK): a text is a message with a <c>Body</c>, the confirmation prompt is the operator's
/// <c>twilio/quick-reply</c> content filled through its variables. Owns the Twilio side of the inbound path as
/// well: the <c>X-Twilio-Signature</c> of a POST (<see cref="TwilioRequestSignature"/>) and the form body format
/// (<see cref="TwilioWebhookReader"/>). Twilio has no GET handshake, so a GET webhook is rejected.
/// </summary>
internal sealed class TwilioWhatsAppProvider : IWhatsAppProvider
{
    /// <summary>
    /// HTTP client factory for Twilio REST API calls.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// WhatsApp adapter settings — kept ONLY for the infrastructure-level REST API endpoint
    /// (<c>Twilio:ApiBaseUrl</c>), the same split the Meta provider keeps for its Graph API endpoint. The
    /// tenant-credential fields are resolved per operation via the seam.
    /// </summary>
    private readonly WhatsAppOptions _options;

    /// <summary>
    /// Per-tenant channel credential provider (seam CA-165). The account, the Auth Token, the quick-reply content
    /// and the webhook URL are resolved per operation by the ambient tenant, never captured in the constructor.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<TwilioWhatsAppProvider> _logger;

    /// <summary>
    /// Creates an instance of <see cref="TwilioWhatsAppProvider"/>.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="options">Adapter settings (infrastructure-level REST API endpoint only).</param>
    /// <param name="resolver">Canonical layer resolver (source of the tenant's credentials).</param>
    /// <param name="logger">Logger.</param>
    public TwilioWhatsAppProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<WhatsAppOptions> options,
        IConfigurationResolver resolver,
        ILogger<TwilioWhatsAppProvider> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ProviderType => WhatsAppProviderNames.Twilio;

    /// <inheritdoc />
    public Task<bool> SendTextMessageAsync(
        string recipientUserId,
        string messageText,
        CancellationToken cancellationToken = default)
    {
        // The method sends a plain text message: the Body field of a Messages resource.
        return SendMessageAsync(
            recipientUserId,
            TwilioConstants.OutboundKindText,
            _ => [new(TwilioConstants.BodyField, messageText)],
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
    {
        // The method fills the operator's twilio/quick-reply content through its variables 1..5. A body over the
        // content's limit cannot be sent at all, so it is refused here; a button title over its limit is cut.
        // The limits are counted in characters (Unicode scalar values), so a surrogate pair counts once.
        var bodyLength = bodyText.EnumerateRunes().Count();
        if (bodyLength > TwilioConstants.QuickReplyBodyMaxLength)
        {
            _logger.LogWarning(
                "Cannot send a WhatsApp (Twilio) prompt: the body is longer than the quick-reply limit. "
                + "Length={Length}, Limit={Limit}, ChannelUserIdHash={ChannelUserIdHash}.",
                bodyLength,
                TwilioConstants.QuickReplyBodyMaxLength,
                LogMasking.Fingerprint(recipientUserId));

            return Task.FromResult(false);
        }

        var contentVariables = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [TwilioConstants.QuickReplyBodyVariable] = bodyText,
            [TwilioConstants.QuickReplyConfirmTitleVariable] = TruncateToCharacters(
                confirmButtonTitle,
                TwilioConstants.QuickReplyButtonTitleMaxLength),
            [TwilioConstants.QuickReplyConfirmIdVariable] = confirmButtonId,
            [TwilioConstants.QuickReplyDeclineTitleVariable] = TruncateToCharacters(
                declineButtonTitle,
                TwilioConstants.QuickReplyButtonTitleMaxLength),
            [TwilioConstants.QuickReplyDeclineIdVariable] = declineButtonId
        });

        return SendMessageAsync(
            recipientUserId,
            TwilioConstants.OutboundKindQuickReply,
            twilio =>
            [
                new(TwilioConstants.ContentSidField, twilio.QuickReplyContentSid),
                new(TwilioConstants.ContentVariablesField, contentVariables)
            ],
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(bool IsHealthy, string? Details)> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        // The method reads the account resource: reachable API, valid credentials and an active account.
        try
        {
            // The probe runs outside any request and reports the state of THIS deployment, so it reads the
            // installation's own credentials (the default implicit tenant) explicitly, as the Meta probe does.
            using var tenantScope = ChannelTenantContext.BeginScope(tenantId: null);

            var twilio = (await ResolveCredentialsAsync(cancellationToken))?.Twilio;
            if (twilio is null)
            {
                _logger.LogError("Twilio health check: WhatsApp credentials are unavailable.");
                return (false, "WhatsApp credentials are unavailable.");
            }

            var accountUri = BuildAccountUri(twilio.AccountSid, TwilioConstants.AccountResourceSuffix);
            using var request = CreateAuthenticatedRequest(HttpMethod.Get, accountUri, twilio);

            var client = _httpClientFactory.CreateClient(TwilioConstants.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorCode = await ReadErrorCodeAsync(response, cancellationToken);
                var httpStatus = $"HTTP {(int)response.StatusCode}";
                return (false, errorCode is null ? httpStatus : $"{httpStatus}, Twilio error code {errorCode}");
            }

            var status = await ReadAccountStatusAsync(response, cancellationToken);
            if (!string.Equals(status, TwilioConstants.AccountStatusActive, StringComparison.Ordinal))
            {
                return (false, status is null ? "Twilio account status is unknown." : $"Twilio account status: {status}.");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Twilio health check failed.");
            return (false, "Twilio API request failed.");
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ValidateWebhookAsync(
        ChannelInboundRequest request,
        CancellationToken cancellationToken = default)
    {
        // The method verifies the X-Twilio-Signature of a POST. Twilio has no GET handshake: any other method is
        // rejected. The signature is computed over the webhook URL of the tenant's credentials — the request
        // envelope carries no URL, and behind a proxy the internal one would differ from what Twilio signed.
        if (!string.Equals(request.Method, HttpMethods.Post, StringComparison.Ordinal))
        {
            return Result<bool>.Success(false);
        }

        try
        {
            request.Headers.TryGetValue(TwilioConstants.SignatureHeader, out var signatureHeader);
            if (string.IsNullOrWhiteSpace(signatureHeader))
            {
                _logger.LogWarning(
                    "WhatsApp (Twilio) webhook rejected: header {SignatureHeader} is missing.",
                    TwilioConstants.SignatureHeader);
                return Result<bool>.Success(false);
            }

            // One credential resolve per operation (CA-171): the Auth Token and the webhook URL are
            // tenant-credential fields, so a signature made with another tenant's token does not pass.
            var twilio = (await ResolveCredentialsAsync(cancellationToken))?.Twilio;
            if (twilio is null)
            {
                _logger.LogError("WhatsApp (Twilio) webhook rejected: credentials are unavailable.");
                return Result<bool>.Success(false);
            }

            if (string.IsNullOrWhiteSpace(twilio.AuthToken) || string.IsNullOrWhiteSpace(twilio.WebhookUrl))
            {
                _logger.LogError("WhatsApp (Twilio) webhook rejected: AuthToken or WebhookUrl is not configured.");
                return Result<bool>.Success(false);
            }

            if (!TwilioWebhookReader.TryReadForm(request.Body.Span, out var form))
            {
                _logger.LogWarning("WhatsApp (Twilio) webhook rejected: the body is not a readable form.");
                return Result<bool>.Success(false);
            }

            var isValid = TwilioRequestSignature.IsValid(twilio.AuthToken, twilio.WebhookUrl, form, signatureHeader);
            if (!isValid)
            {
                _logger.LogWarning("WhatsApp (Twilio) webhook rejected: invalid signature.");
            }

            return Result<bool>.Success(isValid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WhatsApp (Twilio) webhook validation error.");
            return Result<bool>.Failure(
                ChannelAdapterErrorCodes.WebhookValidationFailed,
                "WhatsApp webhook validation error.");
        }
    }

    /// <inheritdoc />
    public WhatsAppInboundEvent ReadInboundEvent(ChannelInboundRequest request) =>
        TwilioWebhookReader.Read(request.Body.Span, _logger);

    /// <summary>
    /// Creates a message from the business number to the recipient through the Messages resource.
    /// </summary>
    /// <param name="recipientUserId">Business-scoped user ID (BSUID) of the recipient.</param>
    /// <param name="messageType">Kind of the outbound message, for the log.</param>
    /// <param name="contentFields">Content fields of the message, built from the tenant's Twilio credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether Twilio accepted the message.</returns>
    private async Task<bool> SendMessageAsync(
        string recipientUserId,
        string messageType,
        Func<TwilioOptions, IReadOnlyList<KeyValuePair<string?, string?>>> contentFields,
        CancellationToken cancellationToken)
    {
        // The method resolves the tenant's credentials, posts the form and maps the answer to a bool. The log
        // carries the HTTP status and the Twilio error code only — never the token, the account, the text or
        // the recipient's identifier (it appears as a fingerprint).
        try
        {
            var credentials = await ResolveCredentialsAsync(cancellationToken);
            if (credentials?.Twilio is not { } twilio)
            {
                _logger.LogError(
                    "Cannot send a WhatsApp (Twilio) message: credentials are unavailable. Type={MessageType}, ChannelUserIdHash={ChannelUserIdHash}.",
                    messageType,
                    LogMasking.Fingerprint(recipientUserId));

                return false;
            }

            var sender = WhatsAppChannelAdapter.TryNormalizePhoneToE164(credentials.BusinessPhoneNumber);
            if (sender is null)
            {
                _logger.LogError(
                    "Cannot send a WhatsApp (Twilio) message: BusinessPhoneNumber is not a valid E.164 number. Type={MessageType}.",
                    messageType);

                return false;
            }

            // The element type is the one FormUrlEncodedContent takes, nullable annotations included.
            var fields = new List<KeyValuePair<string?, string?>>
            {
                new(TwilioConstants.FromField, TwilioConstants.WhatsAppAddressPrefix + sender),
                new(TwilioConstants.ToField, TwilioConstants.WhatsAppAddressPrefix + recipientUserId)
            };
            fields.AddRange(contentFields(twilio));

            var messagesUri = BuildAccountUri(twilio.AccountSid, TwilioConstants.MessagesResourceSuffix);
            using var request = CreateAuthenticatedRequest(HttpMethod.Post, messagesUri, twilio);
            request.Content = new FormUrlEncodedContent(fields);

            var client = _httpClientFactory.CreateClient(TwilioConstants.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorCode = await ReadErrorCodeAsync(response, cancellationToken);
                _logger.LogWarning(
                    "Error sending a WhatsApp (Twilio) message. Type={MessageType}, ChannelUserIdHash={ChannelUserIdHash}, StatusCode={StatusCode}, TwilioErrorCode={TwilioErrorCode}.",
                    messageType,
                    LogMasking.Fingerprint(recipientUserId),
                    (int)response.StatusCode,
                    errorCode);

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Exception while sending a WhatsApp (Twilio) message. Type={MessageType}, ChannelUserIdHash={ChannelUserIdHash}.",
                messageType,
                LogMasking.Fingerprint(recipientUserId));

            return false;
        }
    }

    /// <summary>
    /// Resolves the WhatsApp credentials of the current tenant — one resolve per operation (CA-171).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The credential group, or null when no layer supplies credentials. The group's Twilio part may still be
    /// absent (a group stored before this provider existed carries none): callers treat that as no credentials.
    /// </returns>
    private async Task<WhatsAppTenantCredentials?> ResolveCredentialsAsync(CancellationToken cancellationToken)
    {
        var credentialsResult = await WhatsAppCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);

        return credentialsResult.IsFailure ? null : credentialsResult.Value;
    }

    /// <summary>
    /// Creates a request to the Twilio REST API with Basic authentication (<c>AccountSid:AuthToken</c>).
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="uri">Resource URI.</param>
    /// <param name="twilio">Tenant's Twilio credentials.</param>
    /// <returns>The request; the caller owns it.</returns>
    private static HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, Uri uri, TwilioOptions twilio)
    {
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(twilio.AccountSid + TwilioConstants.BasicCredentialsSeparator + twilio.AuthToken));
        var authorization = new AuthenticationHeaderValue(TwilioConstants.BasicScheme, credentials);

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = authorization;
        return request;
    }

    /// <summary>
    /// Builds an absolute URI of an account resource: <c>{ApiBaseUrl}/2010-04-01/Accounts/{AccountSid}{suffix}</c>.
    /// </summary>
    /// <param name="accountSid">Account identifier.</param>
    /// <param name="suffix">Rest of the resource path after the account identifier.</param>
    /// <returns>Absolute URI.</returns>
    private Uri BuildAccountUri(string accountSid, string suffix)
    {
        // ApiBaseUrl is infrastructure-level — from the global IOptions, as GraphApiBaseUrl is for Meta.
        var baseUrl = _options.Twilio.ApiBaseUrl.TrimEnd('/');
        return new Uri(
            baseUrl + TwilioConstants.AccountsPath + Uri.EscapeDataString(accountSid) + suffix,
            UriKind.Absolute);
    }

    /// <summary>
    /// Reads the Twilio error code from an error response body.
    /// </summary>
    /// <param name="response">Error response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The numeric <c>code</c> of the body, or null when the body carries none.</returns>
    private static async Task<int?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // The method reads only the numeric code: the error message of Twilio may quote the addresses of the
        // request, so it is not carried anywhere.
        using var document = await TryReadJsonAsync(response, cancellationToken);
        if (document is null
            || document.RootElement.ValueKind is not JsonValueKind.Object
            || !document.RootElement.TryGetProperty(TwilioConstants.ErrorCodeProperty, out var code)
            || code.ValueKind is not JsonValueKind.Number
            || !code.TryGetInt32(out var value))
        {
            return null;
        }

        return value;
    }

    /// <summary>
    /// Reads the account status from an account resource.
    /// </summary>
    /// <param name="response">Successful response of the account resource.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The <c>status</c> of the account, or null when the body carries none.</returns>
    private static async Task<string?> ReadAccountStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var document = await TryReadJsonAsync(response, cancellationToken);
        if (document is null
            || document.RootElement.ValueKind is not JsonValueKind.Object
            || !document.RootElement.TryGetProperty(TwilioConstants.AccountStatusProperty, out var status)
            || status.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        return status.GetString();
    }

    /// <summary>
    /// Parses a response body as JSON.
    /// </summary>
    /// <param name="response">Response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The document, or null when the body is not JSON.</returns>
    private static async Task<JsonDocument?> TryReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cuts a text to a number of characters (Unicode scalar values) at a text-element boundary, so a
    /// grapheme cluster (a flag, an emoji with a skin tone, a letter with combining marks) is never split.
    /// </summary>
    /// <param name="text">Text.</param>
    /// <param name="maxCharacters">Maximum number of characters.</param>
    /// <returns>
    /// The text itself when it fits; otherwise its longest prefix of whole text elements that holds at most
    /// <paramref name="maxCharacters"/> characters.
    /// </returns>
    private static string TruncateToCharacters(string text, int maxCharacters)
    {
        // The limit is counted in characters, the cut is made between text elements: whole elements are
        // taken while their characters still fit, and the first element that would overflow ends the prefix.
        var characters = 0;
        var length = 0;
        while (length < text.Length)
        {
            var element = text.AsSpan(length, StringInfo.GetNextTextElementLength(text.AsSpan(length)));

            var elementCharacters = 0;
            for (var rest = element; !rest.IsEmpty; elementCharacters++)
            {
                Rune.DecodeFromUtf16(rest, out _, out var consumed);
                rest = rest[consumed..];
            }

            if (characters + elementCharacters > maxCharacters)
            {
                return text[..length];
            }

            characters += elementCharacters;
            length += element.Length;
        }

        return text;
    }
}
