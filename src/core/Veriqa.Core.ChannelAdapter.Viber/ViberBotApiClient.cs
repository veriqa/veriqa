// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Viber.Configuration;
using Veriqa.Core.ChannelAdapter.Viber.Constants;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;

namespace Veriqa.Core.ChannelAdapter.Viber;

/// <summary>
/// The outbound calls of the Viber channel: a message to a user and the webhook of the bot. Every
/// call resolves the credentials of the tenant of the current scope and takes a client of the named
/// <see cref="IHttpClientFactory"/> registration; the address of the API is an installation-level
/// setting and is read at the moment of the call.
/// </summary>
internal sealed class ViberBotApiClient
{
    /// <summary>
    /// HTTP client factory of the Bot API calls.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Channel settings — read ONLY for the installation-level address of the API
    /// (<c>ApiBaseUrl</c>); the credentials come from the resolver.
    /// </summary>
    private readonly IOptionsMonitor<ViberOptions> _options;

    /// <summary>
    /// Canonical layer resolver — the source of the tenant's credentials, asked per operation.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ViberBotApiClient> _logger;

    /// <summary>
    /// Creates an instance of <see cref="ViberBotApiClient"/>.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="options">Channel settings (installation-level address of the API only).</param>
    /// <param name="resolver">Canonical layer resolver (source of the tenant's credentials).</param>
    /// <param name="logger">Logger.</param>
    public ViberBotApiClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<ViberOptions> options,
        IConfigurationResolver resolver,
        ILogger<ViberBotApiClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// What a Bot API call ended with. The platform answers a refused call with a successful HTTP
    /// status and a non-zero <c>status</c> in the body, so both are kept.
    /// </summary>
    /// <param name="IsSuccess">true — the HTTP status is a success and the body says <c>status = 0</c>.</param>
    /// <param name="HttpStatusCode">HTTP status of the answer; null when the call did not complete.</param>
    /// <param name="ViberStatus">Status of the body; null when the body carries none or is not JSON.</param>
    /// <param name="MessageToken">Token of the sent message; null when the answer names none.</param>
    private readonly record struct CallOutcome(
        bool IsSuccess,
        int? HttpStatusCode,
        int? ViberStatus,
        long? MessageToken);

    /// <summary>
    /// Sends one text message to a user on behalf of the tenant's bot.
    /// </summary>
    /// <param name="receiver">Identifier of the user the message is addressed to.</param>
    /// <param name="text">Text of the message.</param>
    /// <param name="keyboard">Keyboard shown under the message; null sends none.</param>
    /// <param name="minApiVersion">
    /// Minimal API version the message requires of a client; null leaves the platform default.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Reference to the sent message (null when the platform named none), or a failure: the
    /// credentials are unavailable, the sender name is over the limit of the platform, the call did
    /// not complete, or the platform refused the message.
    /// </returns>
    public async Task<Result<ChannelMessageRef?>> SendTextAsync(
        string receiver,
        string text,
        ViberKeyboard? keyboard,
        int? minApiVersion,
        CancellationToken cancellationToken)
    {
        // One credential resolve per operation: the token and the sender name are the tenant's.
        var credentialsResult = await ViberCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            _logger.LogError(
                "Cannot send a Viber message: credentials are unavailable. ChannelUserIdHash={ChannelUserIdHash}.",
                LogMasking.Fingerprint(receiver));

            return Result<ChannelMessageRef?>.Failure(credentialsResult.Error);
        }

        var credentials = credentialsResult.Value;
        if (string.IsNullOrWhiteSpace(credentials.AuthToken) || string.IsNullOrWhiteSpace(credentials.SenderName))
        {
            _logger.LogError(
                "Cannot send a Viber message: AuthToken or SenderName is not configured. "
                + "ChannelUserIdHash={ChannelUserIdHash}.",
                LogMasking.Fingerprint(receiver));

            return Result<ChannelMessageRef?>.Failure(
                ChannelCredentialErrorCodes.ChannelCredentialsMissing,
                "Viber authentication token and sender name must be configured.");
        }

        // The platform refuses a message whose sender name is over its limit. The name of a
        // tenant does not pass the start-up check of the core section, so it is checked here and
        // the refusal is named instead of arriving as a bare status of the platform.
        if (credentials.SenderName.Length > ViberAdapterConstants.SenderNameMaxLength)
        {
            _logger.LogError(
                "Cannot send a Viber message: SenderName is {SenderNameLength} characters long, the platform "
                + "accepts at most {SenderNameMaxLength}. ChannelUserIdHash={ChannelUserIdHash}.",
                credentials.SenderName.Length,
                ViberAdapterConstants.SenderNameMaxLength,
                LogMasking.Fingerprint(receiver));

            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                $"Viber sender name must be at most {ViberAdapterConstants.SenderNameMaxLength} characters long.");
        }

        var outcome = await PostAsync(
            ViberAdapterConstants.SendMessagePath,
            new ViberOutboundMessage
            {
                Receiver = receiver,
                MinApiVersion = minApiVersion,
                Sender = new ViberOutboundSender { Name = credentials.SenderName },
                Type = ViberAdapterConstants.MessageTypeText,
                Text = text,
                Keyboard = keyboard
            },
            credentials.AuthToken,
            cancellationToken);

        if (!outcome.IsSuccess)
        {
            _logger.LogWarning(
                "Viber did not accept an outbound message. ChannelUserIdHash={ChannelUserIdHash}, "
                + "StatusCode={StatusCode}, ViberStatus={ViberStatus}.",
                LogMasking.Fingerprint(receiver),
                outcome.HttpStatusCode,
                outcome.ViberStatus);

            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "Viber did not accept the outbound message.");
        }

        if (outcome.MessageToken is not { } messageToken)
        {
            return Result<ChannelMessageRef?>.Success(null);
        }

        return Result<ChannelMessageRef?>.Success(new ChannelMessageRef
        {
            ChatId = receiver,
            MessageId = messageToken.ToString(CultureInfo.InvariantCulture)
        });
    }

    /// <summary>
    /// Sets the webhook of the bot of the current tenant, or removes it.
    /// </summary>
    /// <param name="url">Address the platform is to deliver events to; empty removes the webhook.</param>
    /// <param name="eventTypes">
    /// Events to subscribe to besides the ones the platform always delivers; null leaves the choice
    /// to the platform.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Success when the platform accepted the call; a failure whose message names the HTTP status
    /// and the status of the platform — never the token.
    /// </returns>
    public async Task<Result<bool>> SetWebhookAsync(
        string url,
        IReadOnlyList<string>? eventTypes,
        CancellationToken cancellationToken)
    {
        var credentialsResult = await ViberCredentialsResolver.ResolveAsync(_resolver, _logger, cancellationToken);
        if (credentialsResult.IsFailure)
        {
            return Result<bool>.Failure(credentialsResult.Error);
        }

        if (string.IsNullOrWhiteSpace(credentialsResult.Value.AuthToken))
        {
            return Result<bool>.Failure(
                ChannelCredentialErrorCodes.ChannelCredentialsMissing,
                "Viber authentication token must be configured.");
        }

        var outcome = await PostAsync(
            ViberAdapterConstants.SetWebhookPath,
            new ViberSetWebhookRequest { Url = url, EventTypes = eventTypes },
            credentialsResult.Value.AuthToken,
            cancellationToken);

        return outcome.IsSuccess
            ? Result<bool>.Success(true)
            : Result<bool>.Failure(
                ChannelAdapterErrorCodes.ChannelNotAvailable,
                $"Viber did not accept the webhook call (HTTP status: {Describe(outcome.HttpStatusCode)}, "
                + $"Viber status: {Describe(outcome.ViberStatus)}).");
    }

    /// <summary>
    /// Makes one Bot API call and reads what it ended with. A call that did not complete — a network
    /// failure, the client's own timeout — is logged here and reported as an unsuccessful outcome
    /// without an HTTP status.
    /// </summary>
    /// <typeparam name="TBody">Shape of the request body.</typeparam>
    /// <param name="path">Path of the method, relative to the API base address.</param>
    /// <param name="body">Request body.</param>
    /// <param name="authToken">Authentication token of the tenant's bot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Outcome of the call.</returns>
    private async Task<CallOutcome> PostAsync<TBody>(
        string path,
        TBody body,
        string authToken,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(_options.CurrentValue.ApiBaseUrl.TrimEnd('/') + path, UriKind.Absolute);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            // The member names are fixed by the attributes of the body type, so the naming policy
            // of the options has no say in them; the default options keep it that way.
            Content = JsonContent.Create(body, options: JsonSerializerOptions.Default)
        };
        request.Headers.TryAddWithoutValidation(ViberAdapterConstants.AuthTokenHeader, authToken);

        try
        {
            var client = _httpClientFactory.CreateClient(ViberAdapterConstants.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);

            var answer = await ReadAnswerAsync(response, cancellationToken);

            return new CallOutcome(
                response.IsSuccessStatusCode && answer?.Status is ViberAdapterConstants.StatusOk,
                (int)response.StatusCode,
                answer?.Status,
                answer?.MessageToken);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
        {
            _logger.LogWarning(ex, "Viber Bot API call did not complete. Path={Path}.", path);

            return new CallOutcome(IsSuccess: false, HttpStatusCode: null, ViberStatus: null, MessageToken: null);
        }
    }

    /// <summary>
    /// Reads the JSON body of an answer of the API.
    /// </summary>
    /// <param name="response">Answer of the API.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The body, or null when it is not the JSON object the API documents.</returns>
    private async Task<ViberApiResponse?> ReadAnswerAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);

            return await JsonSerializer.DeserializeAsync<ViberApiResponse>(body, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The answer of the Viber Bot API is not valid JSON.");
            return null;
        }
    }

    /// <summary>
    /// Spells a status for a failure message.
    /// </summary>
    /// <param name="status">Status; null when there was none to read.</param>
    /// <returns>The number, or a word saying there is none.</returns>
    private static string Describe(int? status) =>
        status?.ToString(CultureInfo.InvariantCulture) ?? "none";

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
