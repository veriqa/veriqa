// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Telegram.Bot;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Telegram.Configuration;
using Veriqa.Core.ChannelAdapter.Telegram.Constants;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Telegram.MultiTenancy;

/// <summary>
/// Builder of a Telegram Bot client from the tenant's effective token (SPEC-003 §17.4, CA-166).
/// Channel-specific layer on top of the shared <see cref="BotTokenClientBuilder{TClient,TCredentials}"/> skeleton:
/// channel/client type, credential group type, and construction of a <see cref="TelegramBotClient"/> from the token.
/// The client sends its requests through the named <see cref="IHttpClientFactory"/> client
/// <see cref="TelegramAdapterConstants.HttpClientName"/>, which bounds every Bot API request by
/// <see cref="TelegramAdapterConstants.BotApiTimeoutSeconds"/>. The built client is cached by
/// <see cref="ChannelClientFactory"/> per (tenant, channel), and its <see cref="HttpClient"/> lives with it.
/// </summary>
internal sealed class TelegramClientBuilder : BotTokenClientBuilder<ITelegramBotClient, TelegramTenantCredentials>
{
    /// <summary>
    /// Factory of the named HTTP client the Bot client is built over.
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Creates the Telegram client builder over the canonical resolver.
    /// </summary>
    /// <param name="resolver">Canonical layer resolver.</param>
    /// <param name="httpClientFactory">Factory of the named HTTP client of the Bot API.</param>
    /// <param name="logger">Logger of the builder.</param>
    public TelegramClientBuilder(
        IConfigurationResolver resolver,
        IHttpClientFactory httpClientFactory,
        ILogger<TelegramClientBuilder> logger)
        : base(resolver, logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <inheritdoc />
    public override string ChannelType => ChannelTypes.Telegram;

    /// <inheritdoc />
    protected override ConfigKey<TelegramTenantCredentials> CredentialsKey => TelegramConfigKeys.Credentials;

    /// <inheritdoc />
    protected override string ChannelDisplayName => "Telegram";

    /// <inheritdoc />
    protected override string? GetToken(TelegramTenantCredentials credentials) => credentials.BotToken;

    /// <inheritdoc />
    protected override ITelegramBotClient CreateClient(string token) =>
        new TelegramBotClient(token, _httpClientFactory.CreateClient(TelegramAdapterConstants.HttpClientName));
}
