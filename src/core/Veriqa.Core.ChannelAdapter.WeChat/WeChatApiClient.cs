// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.WeChat.Configuration;
using Veriqa.Core.ChannelAdapter.WeChat.Constants;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.WeChat;

/// <summary>
/// Client of the Official Account API of ONE account. It is built from the credentials of a tenant
/// (<see cref="Veriqa.Core.ChannelAdapter.WeChat.MultiTenancy.WeChatClientBuilder"/>) and cached by the channel client factory per
/// <c>(tenant, channel type)</c>, so everything that belongs to the account — its AppID, its
/// AppSecret and the access token obtained with them — lives here and not in the adapter, which
/// stays a stateless singleton (SPEC-003 CA-014, CA-164).
/// </summary>
/// <remarks>
/// The access token is cached by this instance for the lifetime the platform stated, less a renewal
/// margin. Two tenants are two instances, so the token of one account never answers a call made for
/// another. An answer that carries no token is not remembered: the next call asks the platform again.
/// <para>
/// The token is asked for with the stable-token call. Its repeated use within the lifetime of a
/// token returns that same token, so several hosts serving one account each obtain it on their own
/// without revoking the token of the others — which the plain token call does on every request.
/// </para>
/// <para>
/// The QR codes created for sign-ins are remembered here as well, by transaction, for as long as
/// they live: asking again for the entry of the same transaction makes no second call to the
/// platform and yields the same link (SPEC-025 HOP-011a). The memory is of this process only.
/// </para>
/// </remarks>
internal sealed class WeChatApiClient : IDisposable
{
    /// <summary>
    /// Lifetime (seconds) asked for a sign-in QR code: the longest lifetime a transaction can have.
    /// A transaction never outlives its QR code this way, whatever lifetime the installation or
    /// the request set for it; a code that outlives its transaction is harmless, because its scene
    /// names that transaction alone.
    /// </summary>
    private const int QrCodeLifetimeSeconds = TransactionEngineOptions.MaxTransactionTtlSeconds;

    /// <summary>
    /// Serialization of the request bodies. Characters outside ASCII are written as they are
    /// rather than as escape sequences: a message text reaches the recipient in any language, and
    /// its delivery must not depend on how the platform reads an escaped character.
    /// </summary>
    private static readonly JsonSerializerOptions WireJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// AppID of the account this client speaks for.
    /// </summary>
    private readonly string _appId;

    /// <summary>
    /// AppSecret of the account this client speaks for.
    /// </summary>
    private readonly string _appSecret;

    /// <summary>
    /// HTTP client factory of the Official Account API calls.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Channel settings — read ONLY for the installation-level address of the API
    /// (<c>ApiBaseUrl</c>), at the moment of a call.
    /// </summary>
    private readonly IOptionsMonitor<WeChatOptions> _options;

    /// <summary>
    /// Clock behind the expiry of the access token.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<WeChatApiClient> _logger;

    /// <summary>
    /// Lets one caller at a time ask the platform for an access token: concurrent calls of the same
    /// account wait for that answer instead of each requesting a token of their own.
    /// </summary>
    private readonly SemaphoreSlim _accessTokenLock = new(1, 1);

    /// <summary>
    /// The access token in force and the moment it stops being handed out; null until the first
    /// successful request. Replaced as a whole, so a reader never sees a token with a foreign expiry.
    /// </summary>
    private volatile CachedAccessToken? _accessToken;

    /// <summary>
    /// QR codes created for sign-ins, by transaction. An entry is the creation itself, so callers
    /// asking for the same transaction at once share one call to the platform.
    /// </summary>
    private readonly ConcurrentDictionary<TransactionId, Task<QrCodeLink?>> _qrCodeLinks = new();

    /// <summary>
    /// Guards the start of a creation: of the callers that find no entry for a transaction, one
    /// starts the call to the platform and the others pick up the entry it left.
    /// </summary>
    private readonly Lock _qrCodeCreationGate = new();

    /// <summary>
    /// Moment (UTC ticks) from which the remembered QR codes are swept again.
    /// </summary>
    private long _nextQrCodeSweepTicks;

    /// <summary>
    /// Creates the client of one Official Account.
    /// </summary>
    /// <param name="appId">AppID of the account.</param>
    /// <param name="appSecret">AppSecret of the account.</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="options">Channel settings (installation-level address of the API only).</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public WeChatApiClient(
        string appId,
        string appSecret,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<WeChatOptions> options,
        TimeProvider timeProvider,
        ILogger<WeChatApiClient> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appSecret);

        _appId = appId;
        _appSecret = appSecret;
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns the access token of the account: the cached one while it is in force, otherwise a
    /// token requested from the platform.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The access token, or a failure: the call did not complete, the answer could not be read, or
    /// the platform refused the token (wrong credentials, a calling address outside the allowed
    /// list of the account).
    /// </returns>
    public async Task<Result<string>> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        // Fast path: a token in force is handed out without waiting for the lock.
        if (TryGetCachedAccessToken(out var cached))
        {
            return Result<string>.Success(cached);
        }

        await _accessTokenLock.WaitAsync(cancellationToken);
        try
        {
            // A caller that waited for the lock finds the token the previous holder obtained.
            if (TryGetCachedAccessToken(out cached))
            {
                return Result<string>.Success(cached);
            }

            return await RequestAccessTokenAsync(cancellationToken);
        }
        finally
        {
            _accessTokenLock.Release();
        }
    }

    /// <summary>
    /// Returns the content of the QR code a sign-in of the transaction is entered by: the one
    /// already created for it while it lives, otherwise a code created now. Its scene is the
    /// transaction behind the sign-in marker, and the platform hands the scene back in the event
    /// of the scan.
    /// </summary>
    /// <param name="transactionId">Transaction the QR code is for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The address a scanner reads off the QR code, or a failure: no access token, a call that did
    /// not complete, an unreadable answer, a refusal of the platform.
    /// </returns>
    public async Task<Result<string>> GetSignInQrCodeLinkAsync(
        TransactionId transactionId,
        CancellationToken cancellationToken)
    {
        await SweepQrCodeLinksAsync();

        while (true)
        {
            // The creation runs detached from the caller that started it: other callers of the
            // same transaction wait for it too, and one of them leaving must not fail the rest.
            // The timeout of the HTTP client bounds it.
            var creation = GetOrStartQrCodeCreationAsync(transactionId);
            var remembered = KeyValuePair.Create(transactionId, creation);

            QrCodeLink? link;
            try
            {
                link = await creation.WaitAsync(cancellationToken);
            }
            catch when (creation.IsFaulted)
            {
                _qrCodeLinks.TryRemove(remembered);
                throw;
            }

            // A failed creation is not remembered: the next call asks the platform again.
            if (link is null)
            {
                _qrCodeLinks.TryRemove(remembered);

                return Result<string>.Failure(
                    ChannelAdapterErrorCodes.DeepLinkGenerationFailed,
                    "WeChat QR code could not be created.");
            }

            if (_timeProvider.GetUtcNow() < link.ExpiresAt)
            {
                return Result<string>.Success(link.Url);
            }

            // The remembered code has expired: it is dropped and a new one is created.
            _qrCodeLinks.TryRemove(remembered);
        }
    }

    /// <summary>
    /// Sends one customer service message of the account.
    /// </summary>
    /// <param name="openId">OpenID of the recipient — for the log only; the body names it too.</param>
    /// <param name="message">The request body of the call, in its wire form.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// No message reference (the platform names none), or a failure: no access token, a call that
    /// did not complete, an unreadable answer, a refusal of the platform — a message outside the
    /// interaction window the platform allows included.
    /// </returns>
    public async Task<Result<ChannelMessageRef?>> SendCustomerServiceMessageAsync(
        string openId,
        object message,
        CancellationToken cancellationToken)
    {
        var answer = await PostAsync<WeChatApiResponse>(
            WeChatAdapterConstants.CustomerServiceMessagePath,
            message,
            "customer service message",
            cancellationToken);

        if (answer is null)
        {
            _logger.LogWarning(
                "WeChat message was not sent. AppIdHash={AppIdHash}, ChannelUserIdHash={ChannelUserIdHash}.",
                LogMasking.Fingerprint(_appId),
                LogMasking.Fingerprint(openId));

            return Result<ChannelMessageRef?>.Failure(
                ChannelAdapterErrorCodes.MessageSendFailed,
                "WeChat did not accept the outbound message.");
        }

        return Result<ChannelMessageRef?>.Success(null);
    }

    /// <inheritdoc />
    public void Dispose() => _accessTokenLock.Dispose();

    /// <summary>
    /// Returns the creation remembered for a transaction, starting one when there is none.
    /// </summary>
    /// <param name="transactionId">Transaction the QR code is for.</param>
    /// <returns>The creation every caller of the transaction waits for.</returns>
    private Task<QrCodeLink?> GetOrStartQrCodeCreationAsync(TransactionId transactionId)
    {
        if (_qrCodeLinks.TryGetValue(transactionId, out var creation))
        {
            return creation;
        }

        // The gate is held only while the call is started, never while it is awaited: the
        // creation yields before it does anything of its own.
        lock (_qrCodeCreationGate)
        {
            if (!_qrCodeLinks.TryGetValue(transactionId, out creation))
            {
                creation = CreateQrCodeLinkAsync(transactionId);
                _qrCodeLinks[transactionId] = creation;
            }

            return creation;
        }
    }

    /// <summary>
    /// Creates the QR code of a transaction at the platform.
    /// </summary>
    /// <param name="transactionId">Transaction the QR code is for.</param>
    /// <returns>The content of the QR code and its expiry, or null when it was not created.</returns>
    private async Task<QrCodeLink?> CreateQrCodeLinkAsync(TransactionId transactionId)
    {
        // Leaves the caller at once, so the one that starts a creation holds the gate for the
        // start alone and the call itself runs on the pool.
        await Task.Yield();

        var createdAt = _timeProvider.GetUtcNow();

        var answer = await PostAsync<WeChatQrCodeResponse>(
            WeChatAdapterConstants.QrCodeCreatePath,
            new
            {
                expire_seconds = QrCodeLifetimeSeconds,
                action_name = WeChatAdapterConstants.QrCodeActionTemporaryStringScene,
                action_info = new
                {
                    scene = new { scene_str = CallbackDataPrefixes.Auth + transactionId.ToString() }
                }
            },
            "QR code creation",
            CancellationToken.None);

        return string.IsNullOrWhiteSpace(answer?.Url)
            ? null
            : new QrCodeLink(answer.Url, createdAt + TimeSpan.FromSeconds(QrCodeLifetimeSeconds));
    }

    /// <summary>
    /// Drops the remembered QR codes that are of no use any more — expired or never created — at
    /// most once per sweep interval, so the memory holds the codes of live sign-ins only.
    /// </summary>
    private async Task SweepQrCodeLinksAsync()
    {
        var now = _timeProvider.GetUtcNow();
        var dueTicks = Volatile.Read(ref _nextQrCodeSweepTicks);
        var nextTicks = now.UtcTicks + TimeSpan.FromSeconds(WeChatAdapterConstants.QrCodeSweepIntervalSeconds).Ticks;

        // One caller sweeps; the others, and every caller before the interval has passed, go on.
        if (now.UtcTicks < dueTicks
            || Interlocked.CompareExchange(ref _nextQrCodeSweepTicks, nextTicks, dueTicks) != dueTicks)
        {
            return;
        }

        foreach (var remembered in _qrCodeLinks)
        {
            // A creation still running is left alone: its callers are waiting for it.
            var creation = remembered.Value;
            if (!creation.IsCompleted)
            {
                continue;
            }

            // A creation that ran to its end is awaited only to read what it made: it is over, so
            // nothing is waited for here.
            if (!creation.IsCompletedSuccessfully || await creation is not { } link || now >= link.ExpiresAt)
            {
                _qrCodeLinks.TryRemove(remembered);
            }
        }
    }

    /// <summary>
    /// Makes one call of the account that takes its access token: a JSON body posted to a path of
    /// the API.
    /// </summary>
    /// <typeparam name="TAnswer">Shape of the answer.</typeparam>
    /// <param name="path">Path of the call, relative to the API base URL.</param>
    /// <param name="body">Request body, in its wire form.</param>
    /// <param name="operation">Name of the call for the log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The answer of a call the platform carried out, or null: no access token, a call that did not
    /// complete, an unreadable answer, a refusal. The cause is in the log.
    /// </returns>
    private async Task<TAnswer?> PostAsync<TAnswer>(
        string path,
        object body,
        string operation,
        CancellationToken cancellationToken)
        where TAnswer : WeChatApiResponse
    {
        var accessTokenResult = await GetAccessTokenAsync(cancellationToken);
        if (accessTokenResult.IsFailure)
        {
            return null;
        }

        // The platform takes the access token in the query string. As with the AppSecret, the
        // address is never written to a log and the exception of a failed call is left out of it.
        var endpoint = new Uri(
            $"{_options.CurrentValue.ApiBaseUrl.TrimEnd('/')}{path}"
            + $"?{WeChatAdapterConstants.AccessTokenQueryKey}={Uri.EscapeDataString(accessTokenResult.Value)}",
            UriKind.Absolute);

        try
        {
            var client = _httpClientFactory.CreateClient(WeChatAdapterConstants.HttpClientName);
            using var content = JsonContent.Create(body, options: WireJsonOptions);
            using var response = await client.PostAsync(endpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "WeChat {Operation} failed. AppIdHash={AppIdHash}, StatusCode={StatusCode}.",
                    operation,
                    LogMasking.Fingerprint(_appId),
                    (int)response.StatusCode);

                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var answer = await JsonSerializer.DeserializeAsync<TAnswer>(stream, cancellationToken: cancellationToken);

            if (answer is null || answer.ErrCode != WeChatAdapterConstants.ApiSuccessCode)
            {
                // A token the platform no longer accepts is forgotten, so the next call asks for
                // one anew instead of repeating the refusal until the cached token expires.
                if (answer is not null && WeChatAdapterConstants.AccessTokenRejectedCodes.Contains(answer.ErrCode))
                {
                    _accessToken = null;
                }

                _logger.LogWarning(
                    "WeChat refused the {Operation}. AppIdHash={AppIdHash}, ErrCode={ErrCode}.",
                    operation,
                    LogMasking.Fingerprint(_appId),
                    answer?.ErrCode);

                return null;
            }

            return answer;
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
        {
            _logger.LogWarning(
                "WeChat {Operation} did not complete. AppIdHash={AppIdHash}, Failure={Failure}.",
                operation,
                LogMasking.Fingerprint(_appId),
                ex.GetType().Name);

            return null;
        }
    }

    /// <summary>
    /// Reads the cached access token if it is still handed out.
    /// </summary>
    /// <param name="accessToken">The token in force.</param>
    /// <returns>true — a token in force exists.</returns>
    private bool TryGetCachedAccessToken(out string accessToken)
    {
        var current = _accessToken;
        if (current is not null && _timeProvider.GetUtcNow() < current.RenewAt)
        {
            accessToken = current.Value;
            return true;
        }

        accessToken = string.Empty;
        return false;
    }

    /// <summary>
    /// Asks the platform for an access token and remembers a successful answer.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The access token, or a failure that leaves the cache untouched.</returns>
    private async Task<Result<string>> RequestAccessTokenAsync(CancellationToken cancellationToken)
    {
        // The stable-token call takes the AppSecret in the request body, and the body is never
        // written to a log. Without force_refresh the platform keeps the token it has issued and
        // returns it again, so a second host of the same account revokes nothing.
        var endpoint = new Uri(
            $"{_options.CurrentValue.ApiBaseUrl.TrimEnd('/')}{WeChatAdapterConstants.StableAccessTokenPath}",
            UriKind.Absolute);

        try
        {
            var requestedAt = _timeProvider.GetUtcNow();
            var client = _httpClientFactory.CreateClient(WeChatAdapterConstants.HttpClientName);
            using var content = JsonContent.Create(
                new
                {
                    grant_type = WeChatAdapterConstants.GrantTypeClientCredential,
                    appid = _appId,
                    secret = _appSecret
                },
                options: WireJsonOptions);
            using var response = await client.PostAsync(endpoint, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "WeChat access token request failed. AppIdHash={AppIdHash}, StatusCode={StatusCode}.",
                    LogMasking.Fingerprint(_appId),
                    (int)response.StatusCode);

                return AccessTokenUnavailable();
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var answer = await JsonSerializer.DeserializeAsync<WeChatAccessTokenResponse>(
                body, cancellationToken: cancellationToken);

            // The platform reports a refusal in the body of a successful HTTP answer. Only its
            // code is logged: the code names the cause, and nothing else of the answer is needed.
            if (answer is null
                || answer.ErrCode != WeChatAdapterConstants.ApiSuccessCode
                || string.IsNullOrWhiteSpace(answer.AccessToken))
            {
                _logger.LogWarning(
                    "WeChat refused the access token. AppIdHash={AppIdHash}, ErrCode={ErrCode}.",
                    LogMasking.Fingerprint(_appId),
                    answer?.ErrCode);

                return AccessTokenUnavailable();
            }

            _accessToken = new CachedAccessToken(
                answer.AccessToken,
                GetRenewAt(requestedAt, answer.ExpiresIn));

            return Result<string>.Success(answer.AccessToken);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
        {
            // The exception object is left out of the log entry on purpose, as for every call of
            // the account: a transport failure may quote the request address, and the address of
            // the other calls carries the access token.
            _logger.LogWarning(
                "WeChat access token request did not complete. AppIdHash={AppIdHash}, Failure={Failure}.",
                LogMasking.Fingerprint(_appId),
                ex.GetType().Name);

            return AccessTokenUnavailable();
        }
    }

    /// <summary>
    /// Failure of an access-token request, in the one wording every cause shares: the cause is in
    /// the log, and the result carries nothing of the platform's answer.
    /// </summary>
    /// <returns>The failure.</returns>
    private static Result<string> AccessTokenUnavailable() =>
        Result<string>.Failure(
            ChannelAdapterErrorCodes.ChannelNotAvailable,
            "WeChat access token could not be obtained.");

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

    /// <summary>
    /// Moment from which a token is requested anew: the expiry the platform stated, less the
    /// renewal margin. The margin never takes more than a fixed share of the stated lifetime, so a
    /// token the platform hands out shortly before its expiry is still cached for the rest of it
    /// instead of being asked for again on every call.
    /// </summary>
    /// <param name="requestedAt">Moment the token was asked for.</param>
    /// <param name="lifetimeSeconds">Lifetime (seconds) the platform stated for the token.</param>
    /// <returns>The moment the token stops being handed out.</returns>
    private static DateTimeOffset GetRenewAt(DateTimeOffset requestedAt, int lifetimeSeconds)
    {
        var marginSeconds = Math.Min(
            WeChatAdapterConstants.AccessTokenRenewalMarginSeconds,
            lifetimeSeconds / WeChatAdapterConstants.AccessTokenRenewalMarginLifetimeDivisor);

        return requestedAt + TimeSpan.FromSeconds(lifetimeSeconds - marginSeconds);
    }

    /// <summary>
    /// An access token and the moment from which it is no longer handed out.
    /// </summary>
    /// <param name="Value">The access token.</param>
    /// <param name="RenewAt">Expiry stated by the platform, less the renewal margin.</param>
    private sealed record CachedAccessToken(string Value, DateTimeOffset RenewAt);

    /// <summary>
    /// A QR code created for a sign-in.
    /// </summary>
    /// <param name="Url">Content of the QR code: the address a scanner reads off it.</param>
    /// <param name="ExpiresAt">Moment the platform stops serving the code.</param>
    private sealed record QrCodeLink(string Url, DateTimeOffset ExpiresAt);
}
