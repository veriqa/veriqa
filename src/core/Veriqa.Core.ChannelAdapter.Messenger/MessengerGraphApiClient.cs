// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Messenger.Configuration;
using Veriqa.Core.ChannelAdapter.Messenger.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;

namespace Veriqa.Core.ChannelAdapter.Messenger;

/// <summary>
/// The outbound calls of the Messenger channel: the Send API and the user profile read. Every call
/// resolves the credentials of the tenant of the current request and takes a client of the named
/// <see cref="IHttpClientFactory"/> registration; the address of the API (base URL and version) is
/// an installation-level setting and is read at the moment of the call.
/// </summary>
internal sealed class MessengerGraphApiClient
{
    /// <summary>
    /// HTTP client factory of the Graph API calls.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Channel settings — read ONLY for the installation-level address of the API
    /// (<c>GraphApiBaseUrl</c>/<c>GraphApiVersion</c>); the credentials come from the resolver.
    /// </summary>
    private readonly IOptionsMonitor<MessengerOptions> _options;

    /// <summary>
    /// Canonical layer resolver — the source of the tenant's credentials, asked per operation.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Clock behind the hold of a refused locale read.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<MessengerGraphApiClient> _logger;

    /// <summary>
    /// Moment until which the profiles of a tenant are read without the locale, by tenant
    /// identifier (the empty string is the default implicit tenant). An entry appears when the
    /// platform refused the read with the locale and granted the one without it.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _localeRefusedUntil = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates an instance of <see cref="MessengerGraphApiClient"/>.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="options">Channel settings (installation-level address of the API only).</param>
    /// <param name="resolver">Canonical layer resolver (source of the tenant's credentials).</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public MessengerGraphApiClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<MessengerOptions> options,
        IConfigurationResolver resolver,
        TimeProvider timeProvider,
        ILogger<MessengerGraphApiClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sends one message to a user through the Send API on behalf of the tenant's Page.
    /// </summary>
    /// <param name="recipientId">Page-scoped ID of the recipient.</param>
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
        // One credential resolve per operation: the access token and the Page are the tenant's.
        var credentialsResult = await MessengerCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogError(
                "Cannot send a Messenger message: credentials are unavailable. ChannelUserIdHash={ChannelUserIdHash}.",
                LogMasking.Fingerprint(recipientId));

            return Result<ChannelMessageRef?>.Failure(credentialsResult.Error);
        }

        var credentials = credentialsResult.Value;
        if (string.IsNullOrWhiteSpace(credentials.PageAccessToken) || string.IsNullOrWhiteSpace(credentials.PageId))
        {
            _logger.LogError(
                "Cannot send a Messenger message: PageAccessToken or PageId is not configured. "
                + "ChannelUserIdHash={ChannelUserIdHash}.",
                LogMasking.Fingerprint(recipientId));

            return Result<ChannelMessageRef?>.Failure(
                ChannelCredentialErrorCodes.ChannelCredentialsMissing,
                "Messenger page access token and page ID must be configured.");
        }

        var endpoint = BuildUri(
            $"{Uri.EscapeDataString(credentials.PageId)}/{MessengerAdapterConstants.MessagesResourceSegment}");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                recipient = new { id = recipientId },
                messaging_type = MessengerAdapterConstants.MessagingTypeResponse,
                message
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            MessengerAdapterConstants.BearerScheme,
            credentials.PageAccessToken);

        try
        {
            var client = _httpClientFactory.CreateClient(MessengerAdapterConstants.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Messenger rejected an outbound message. ChannelUserIdHash={ChannelUserIdHash}, StatusCode={StatusCode}.",
                    LogMasking.Fingerprint(recipientId),
                    (int)response.StatusCode);

                return Result<ChannelMessageRef?>.Failure(
                    ChannelAdapterErrorCodes.MessageSendFailed,
                    "Messenger rejected the outbound message.");
            }

            return Result<ChannelMessageRef?>.Success(
                await ReadMessageRefAsync(response, recipientId, cancellationToken));
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
        {
            _logger.LogWarning(
                ex,
                "Messenger Send API call failed. ChannelUserIdHash={ChannelUserIdHash}.",
                LogMasking.Fingerprint(recipientId));

            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "Messenger Send API call failed.");
        }
    }

    /// <summary>
    /// Reads the profile of a user who has written to the tenant's Page. The locale takes a
    /// permission of its own, so a read the platform refused is repeated without it — once within
    /// the call. A read that did not complete (a network failure, a timeout, a rate limit, a server
    /// error, an unreadable answer) is not repeated: leaving the locale out would not have helped it.
    /// </summary>
    /// <remarks>
    /// A refusal that the repeat without the locale turned into a profile is remembered for the
    /// tenant: for <see cref="MessengerAdapterConstants.LocaleRefusalHoldMinutes"/> minutes its
    /// profiles are read without the locale straight away, so an app that lacks the permission
    /// does not pay a refused call — and a warning — on every step of every sign-in. The memory is
    /// of this process only; after the hold the locale is asked for again, which is how a
    /// permission granted later is noticed.
    /// </remarks>
    /// <param name="userId">Page-scoped ID of the user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The profile, or null when it could not be read — no credentials, no access, a failed call.
    /// A sign-in does not depend on it.
    /// </returns>
    public async Task<MessengerUserProfile?> GetUserProfileAsync(string userId, CancellationToken cancellationToken)
    {
        // One credential resolve per operation: the access token is the tenant's.
        var credentialsResult = await MessengerCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure || string.IsNullOrWhiteSpace(credentialsResult.Value.PageAccessToken))
        {
            return null;
        }

        var accessToken = credentialsResult.Value.PageAccessToken;
        var tenantKey = ChannelTenantContext.CurrentTenantId ?? string.Empty;
        var now = _timeProvider.GetUtcNow();

        if (_localeRefusedUntil.TryGetValue(tenantKey, out var refusedUntil) && now < refusedUntil)
        {
            var held = await ReadUserProfileAsync(
                userId, MessengerAdapterConstants.ProfileFieldsWithoutLocale, accessToken, cancellationToken);

            return held.Profile;
        }

        var withLocale = await ReadUserProfileAsync(
            userId, MessengerAdapterConstants.ProfileFields, accessToken, cancellationToken);
        if (!withLocale.Refused)
        {
            return withLocale.Profile;
        }

        var withoutLocale = await ReadUserProfileAsync(
            userId, MessengerAdapterConstants.ProfileFieldsWithoutLocale, accessToken, cancellationToken);

        // Only a profile read without the locale proves that the locale is what was refused; a
        // read refused either way says nothing about it — the profile of that user is out of reach.
        if (withoutLocale.Profile is not null)
        {
            _localeRefusedUntil[tenantKey] =
                now + TimeSpan.FromMinutes(MessengerAdapterConstants.LocaleRefusalHoldMinutes);
        }

        return withoutLocale.Profile;
    }

    /// <summary>
    /// Makes one read of the given fields of a user's profile.
    /// </summary>
    /// <param name="userId">Page-scoped ID of the user.</param>
    /// <param name="fields">Comma-separated fields of the profile to read.</param>
    /// <param name="accessToken">Page access token of the tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The profile (null when it was not read), and whether the platform refused the request — an
    /// answer with a client-error status other than a timeout or a rate limit, as opposed to a call
    /// that did not complete.
    /// </returns>
    private async Task<(MessengerUserProfile? Profile, bool Refused)> ReadUserProfileAsync(
        string userId,
        string fields,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildUri(
            $"{Uri.EscapeDataString(userId)}?{MessengerAdapterConstants.FieldsQueryKey}={fields}");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            MessengerAdapterConstants.BearerScheme,
            accessToken);

        try
        {
            var client = _httpClientFactory.CreateClient(MessengerAdapterConstants.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;

                _logger.LogWarning(
                    "Messenger user profile read failed. ChannelUserIdHash={ChannelUserIdHash}, StatusCode={StatusCode}.",
                    LogMasking.Fingerprint(userId),
                    statusCode);

                // A client-error status is the platform answering "no"; a server error is a call
                // that did not complete — and so are a request timeout and a rate limit, which
                // say nothing about what the app may read.
                return (
                    null,
                    statusCode is >= StatusCodes.Status400BadRequest and < StatusCodes.Status500InternalServerError
                        and not StatusCodes.Status408RequestTimeout
                        and not StatusCodes.Status429TooManyRequests);
            }

            return (await ReadJsonAsync<MessengerUserProfile>(response, cancellationToken), false);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
        {
            _logger.LogWarning(
                ex,
                "Messenger user profile read failed. ChannelUserIdHash={ChannelUserIdHash}.",
                LogMasking.Fingerprint(userId));

            return (null, false);
        }
    }

    /// <summary>
    /// Reads the reference to a delivered message from the answer of the Send API. The message is
    /// delivered whatever the answer looks like, so an answer that names no message — or cannot be
    /// read — yields no reference rather than a failure.
    /// </summary>
    /// <param name="response">Successful answer of the Send API.</param>
    /// <param name="recipientId">Page-scoped ID the message was addressed to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reference to the delivered message, or null.</returns>
    private async Task<ChannelMessageRef?> ReadMessageRefAsync(
        HttpResponseMessage response,
        string recipientId,
        CancellationToken cancellationToken)
    {
        MessengerSendResponse? body;
        try
        {
            body = await ReadJsonAsync<MessengerSendResponse>(response, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The answer of the Messenger Send API is not valid JSON.");
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
