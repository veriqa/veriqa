// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Max.BotClient;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Max.Configuration;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.ChannelAdapter.Max.MultiTenancy;

/// <summary>
/// Builder of the MAX Bot client from the tenant's effective token (SPEC-003 §17.4, CA-166).
/// Channel specifics on top of the shared <see cref="BotTokenClientBuilder{TClient,TCredentials}"/> skeleton:
/// channel/client type, credential group type and construction of <see cref="BotClient"/> from the token
/// and the installation's Bot API address.
/// </summary>
internal sealed class MaxClientBuilder : BotTokenClientBuilder<IBotClient, MaxTenantCredentials>
{
    /// <summary>
    /// MAX adapter settings — the source of the Bot API address the client is pointed at.
    /// </summary>
    private readonly IOptions<MaxOptions> _options;

    /// <summary>
    /// Creates the MAX client builder over the canonical resolver.
    /// </summary>
    /// <param name="resolver">Canonical layer resolver.</param>
    /// <param name="logger">Logger of the builder.</param>
    /// <param name="options">MAX adapter settings (the Bot API address).</param>
    public MaxClientBuilder(
        IConfigurationResolver resolver,
        ILogger<MaxClientBuilder> logger,
        IOptions<MaxOptions> options)
        : base(resolver, logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public override string ChannelType => ChannelTypes.Max;

    /// <inheritdoc />
    protected override ConfigKey<MaxTenantCredentials> CredentialsKey => MaxConfigKeys.Credentials;

    /// <inheritdoc />
    protected override string ChannelDisplayName => "MAX";

    /// <inheritdoc />
    protected override string? GetToken(MaxTenantCredentials credentials) => credentials.BotToken;

    /// <inheritdoc />
    /// <remarks>
    /// The address is passed explicitly: the SDK falls back to its own built-in domain only when none is
    /// given, so the installation's setting always wins.
    /// </remarks>
    protected override IBotClient CreateClient(string token) =>
        new BotClient(new BotClientOptions(token, _options.Value.ApiBaseUrl));
}
