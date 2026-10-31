// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Instagram.Configuration;
using Veriqa.Core.ChannelAdapter.Instagram.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;

namespace Veriqa.Core.ChannelAdapter.Instagram;

/// <summary>
/// The outbound calls of the Instagram channel: the Send API and the user profile read. Every call
/// resolves the credentials of the tenant of the current request and takes a client of the named
/// <see cref="IHttpClientFactory"/> registration; the address of the API (base URL and version) is
/// an installation-level setting and is read at the moment of the call.
/// </summary>
internal sealed class InstagramGraphApiClient
{
    /// <summary>
    /// HTTP client factory of the Instagram API calls.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Channel settings — read ONLY for the installation-level address of the API
    /// (<c>GraphApiBaseUrl</c>/<c>GraphApiVersion</c>); the credentials come from the resolver.
    /// </summary>
    private readonly IOptionsMonitor<InstagramOptions> _options;

    /// <summary>
    /// Canonical layer resolver — the source of the tenant's credentials, asked per operation.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<InstagramGraphApiClient> _logger;

    /// <summary>
    /// Creates an instance of <see cref="InstagramGraphApiClient"/>.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="options">Channel settings (installation-level address of the API only).</param>
    /// <param name="resolver">Canonical layer resolver (source of the tenant's credentials).</param>
    /// <param name="logger">Logger.</param>
    public InstagramGraphApiClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<InstagramOptions> options,
        IConfigurationResolver resolver,
        ILogger<InstagramGraphApiClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sends one message to a user through the Send API on behalf of the tenant's account.
    /// </summary>
    /// <param name="recipientId">Instagram-scoped ID of the recipient.</param>
    /// <param name="message">
    /// The <c>message</c> object of the Send API request, in its wire form: a text, a template or
    /// any other content the platform accepts there.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Reference to the delivered message (null when the platform named none), or a failure: the
    /// credentials are unavailable, the call did not complete, or the platform refused the message.
    /// </returns>
    public async Task<Result<ChannelMessageRef?>> SendMessageAsync(
        string recipientId,
        object message,
        CancellationToken cancellationToken)
    {
        // One credential resolve per operation: the access token and the account are the tenant's.
        var credentialsResult = await InstagramCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogError(
                "Cannot send an Instagram message: credentials are unavailable. ChannelUserIdHash={ChannelUserIdHash}.",
                LogMasking.Fingerprint(recipientId));

            return Result<ChannelMessageRef?>.Failure(credentialsResult.Error);
        }

        var credentials = credentialsResult.Value;
        if (string.IsNullOrWhiteSpace(credentials.AccessToken) || string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            _logger.LogError(
                "Cannot send an Instagram message: AccessToken or AccountId is not configured. "
                + "ChannelUserIdHash={ChannelUserIdHash}.",
                LogMasking.Fingerprint(recipientId));

            return Result<ChannelMessageRef?>.Failure(
                ChannelCredentialErrorCodes.ChannelCredentialsMissing,
                "Instagram access token and account ID must be configured.");
        }

        var endpoint = BuildUri(
            $"{Uri.EscapeDataString(credentials.AccountId)}/{InstagramAdapterConstants.MessagesResourceSegment}");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                recipient = new { id = recipientId },
                message
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            InstagramAdapterConstants.BearerScheme,
            credentials.AccessToken);

        try
        {
            var client = _httpClientFactory.CreateClient(InstagramAdapterConstants.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Instagram rejected an outbound message. ChannelUserIdHash={ChannelUserIdHash}, StatusCode={StatusCode}.",
                    LogMasking.Fingerprint(recipientId),
                    (int)response.StatusCode);

                return Result<ChannelMessageRef?>.Failure(
                    ChannelAdapterErrorCodes.MessageSendFailed,
                    "Instagram rejected the outbound message.");
            }

            return Result<ChannelMessageRef?>.Success(
                await ReadMessageRefAsync(response, recipientId, cancellationToken));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
        {
            _logger.LogWarning(
                ex,
                "Instagram Send API call failed. ChannelUserIdHash={ChannelUserIdHash}.",
                LogMasking.Fingerprint(recipientId));

            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "Instagram Send API call failed.");
        }
    }

    /// <summary>
    /// Reads the profile of a user who has written to the tenant's account.
    /// </summary>
    /// <param name="userId">Instagram-scoped ID of the user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The profile, or null when it could not be read — no credentials, no access, a failed call.
    /// A sign-in does not depend on it.
    /// </returns>
    public async Task<InstagramUserProfile?> GetUserProfileAsync(string userId, CancellationToken cancellationToken)
    {
        // One credential resolve per operation: the access token is the tenant's.
        var credentialsResult = await InstagramCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure || string.IsNullOrWhiteSpace(credentialsResult.Value.AccessToken))
        {
            return null;
        }

        var endpoint = BuildUri(
            $"{Uri.EscapeDataString(userId)}?{InstagramAdapterConstants.FieldsQueryKey}={InstagramAdapterConstants.ProfileFields}");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            InstagramAdapterConstants.BearerScheme,
            credentialsResult.Value.AccessToken);

        try
        {
            var client = _httpClientFactory.CreateClient(InstagramAdapterConstants.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Instagram user profile read failed. ChannelUserIdHash={ChannelUserIdHash}, StatusCode={StatusCode}.",
                    LogMasking.Fingerprint(userId),
                    (int)response.StatusCode);

                return null;
            }

            return await ReadJsonAsync<InstagramUserProfile>(response, cancellationToken);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
        {
            _logger.LogWarning(
                ex,
                "Instagram user profile read failed. ChannelUserIdHash={ChannelUserIdHash}.",
                LogMasking.Fingerprint(userId));

            return null;
        }
    }

    /// <summary>
    /// Reads the reference to a delivered message from the answer of the Send API. The message is
    /// delivered whatever the answer looks like, so an answer that names no message — or cannot be
    /// read — yields no reference rather than a failure.
    /// </summary>
    /// <param name="response">Successful answer of the Send API.</param>
    /// <param name="recipientId">Instagram-scoped ID the message was addressed to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reference to the delivered message, or null.</returns>
    private async Task<ChannelMessageRef?> ReadMessageRefAsync(
        HttpResponseMessage response,
        string recipientId,
        CancellationToken cancellationToken)
    {
        InstagramSendResponse? body;
        try
        {
            body = await ReadJsonAsync<InstagramSendResponse>(response, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The answer of the Instagram Send API is not valid JSON.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(body?.MessageId))
        {
            return null;
        }

        return new ChannelMessageRef
        {
            ChatId = string.IsNullOrWhiteSpace(body.RecipientId) ? recipientId : body.RecipientId,
            MessageId = body.MessageId
        };
    }

    /// <summary>
    /// Reads the JSON body of an answer of the API.
    /// </summary>
    /// <typeparam name="T">Shape of the body.</typeparam>
    /// <param name="response">Answer of the API.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The body, or null for a JSON <c>null</c>.</returns>
    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);

        return await JsonSerializer.DeserializeAsync<T>(body, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Builds an absolute address under the configured API base URL and version.
    /// </summary>
    /// <param name="resource">Resource path and query, relative to the version segment.</param>
    /// <returns>Absolute address of the resource.</returns>
    private Uri BuildUri(string resource)
    {
        var options = _options.CurrentValue;

        return new Uri(
            $"{options.GraphApiBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(options.GraphApiVersion)}/{resource}",
            UriKind.Absolute);
    }

    /// <summary>
    /// Tells a cancellation the caller asked for — which is not this client's to swallow — from
    /// every other end of a call: a network failure, the client's own timeout, an answer that
    /// cannot be read, an exception of a resilience handler the host put on the HTTP client. All of
    /// those are a call that did not complete and are reported as a failed result.
    /// </summary>
    /// <param name="exception">Exception the call ended with.</param>
    /// <param name="cancellationToken">Cancellation token of the caller.</param>
    /// <returns>true — the caller cancelled the call, and the exception goes on to it.</returns>
    private static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
}
