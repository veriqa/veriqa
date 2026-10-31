// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.WeChat.Configuration;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Common;

namespace Veriqa.Core.ChannelAdapter.WeChat.MultiTenancy;

/// <summary>
/// Builder of the Official Account API client from the tenant's effective credentials
/// (SPEC-003 §17.4, CA-165). The channel client factory calls it once per
/// <c>(tenant, channel type)</c> and keeps the client, which is what makes the access token cached
/// by the client a token of that tenant's account.
/// </summary>
internal sealed class WeChatClientBuilder : IChannelClientBuilder<WeChatApiClient>
{
    /// <summary>
    /// Canonical layer resolver — the source of the tenant's credentials.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// HTTP client factory handed to every client built here.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Channel settings handed to every client built here (installation-level address of the API).
    /// </summary>
    private readonly IOptionsMonitor<WeChatOptions> _options;

    /// <summary>
    /// Time provider handed to every client built here.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger handed to every client built here.
    /// </summary>
    private readonly ILogger<WeChatApiClient> _clientLogger;

    /// <summary>
    /// Logger of the builder — the place the credential read below is made from.
    /// </summary>
    private readonly ILogger<WeChatClientBuilder> _logger;

    /// <summary>
    /// Creates the WeChat client builder over the canonical resolver.
    /// </summary>
    /// <param name="resolver">Canonical layer resolver.</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="options">Channel settings.</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <param name="clientLogger">Logger of the clients.</param>
    /// <param name="logger">Logger of the builder.</param>
    public WeChatClientBuilder(
        IConfigurationResolver resolver,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<WeChatOptions> options,
        TimeProvider timeProvider,
        ILogger<WeChatApiClient> clientLogger,
        ILogger<WeChatClientBuilder> logger)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _clientLogger = clientLogger ?? throw new ArgumentNullException(nameof(clientLogger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string ChannelType => ChannelTypes.WeChat;

    /// <inheritdoc />
    public async ValueTask<Result<WeChatApiClient>> BuildAsync(
        ChannelCredentialContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Obtain the tenant's credential group through the canonical resolver.
        var resolved = await _resolver.ResolveCredentialsAsync(
            WeChatConfigKeys.Credentials, context, _logger, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result<WeChatApiClient>.Failure(resolved.Error);
        }

        // Without the AppID and the AppSecret no call of the account can be made, and without the
        // Token no request of the platform passes the signature check: an account that can create a
        // QR code but never hears of its scan is as unusable as one with no credentials, so all
        // three make the credentials of the tenant. A group arriving from an upper configuration
        // layer has not passed the validator of the core-level section, so it is checked here.
        var credentials = resolved.Value;
        if (string.IsNullOrWhiteSpace(credentials.AppId)
            || string.IsNullOrWhiteSpace(credentials.AppSecret)
            || string.IsNullOrWhiteSpace(credentials.Token))
        {
            return Result<WeChatApiClient>.Failure(
                ChannelCredentialErrorCodes.ChannelCredentialsMissing,
                "WeChat AppId, AppSecret and Token are not all set for tenant "
                + $"'{context.TenantId ?? ChannelTenantContext.DefaultTenantDisplayName}'.");
        }

        return Result<WeChatApiClient>.Success(new WeChatApiClient(
            credentials.AppId,
            credentials.AppSecret,
            _httpClientFactory,
            _options,
            _timeProvider,
            _clientLogger));
    }
}
