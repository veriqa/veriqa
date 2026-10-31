// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Telegram.Bot;

using Veriqa.Core.ChannelAdapter.Telegram.Configuration;
using Veriqa.Core.ChannelAdapter.Telegram.Constants;
using Veriqa.Core.ChannelAdapter.Telegram.Enums;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Webhooks;

namespace Veriqa.Core.ChannelAdapter.Telegram.Services;

/// <summary>
/// Telegram bot webhook initialization service (SPEC-012 §6.9).
/// Registers the webhook in the Telegram Bot API in the background after startup, retrying until the
/// platform accepts it, and deletes it on shutdown. Active only when <see cref="TelegramUpdateMode.Webhook"/>.
/// </summary>
internal sealed class TelegramWebhookInitializerService : ChannelWebhookRegistrationService
{
    /// <summary>
    /// Telegram Bot API client.
    /// </summary>
    private readonly IChannelClientFactory _clientFactory;

    /// <summary>
    /// Telegram adapter settings.
    /// </summary>
    private readonly TelegramOptions _options;

    /// <summary>
    /// Creates an instance of <see cref="TelegramWebhookInitializerService"/>.
    /// </summary>
    /// <param name="clientFactory">Per-tenant channel client factory.</param>
    /// <param name="options">Telegram adapter settings.</param>
    /// <param name="state">Webhook registration state the channel health check reads.</param>
    /// <param name="timeProvider">Clock the pauses between registration attempts run on.</param>
    /// <param name="lifetime">Lifetime of the host; the first registration attempt waits for its start.</param>
    /// <param name="logger">Logger.</param>
    public TelegramWebhookInitializerService(
        IChannelClientFactory clientFactory,
        IOptions<TelegramOptions> options,
        ChannelWebhookRegistrationState state,
        TimeProvider timeProvider,
        IHostApplicationLifetime lifetime,
        ILogger<TelegramWebhookInitializerService> logger)
        : base(state, timeProvider, lifetime, logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override string ChannelType => ChannelTypes.Telegram;

    /// <inheritdoc />
    protected override bool IsWebhookMode => _options.UpdateMode is TelegramUpdateMode.Webhook;

    /// <inheritdoc />
    protected override string WebhookUrl => _options.WebhookBaseUrl.TrimEnd('/') + TelegramAdapterConstants.WebhookPath;

    /// <inheritdoc />
    protected override async Task RegisterAsync(CancellationToken cancellationToken)
    {
        // In a valid configuration for the Webhook mode the secret token is required
        // (checked by TelegramOptionsValidator at startup)
        // Startup core-only: webhook is registered once at boot; no request tenant here, so this stays a direct core-level read (unlike the per-request adapters migrated to the seam in TASK-072/B4).
        var secretToken = _options.WebhookSecretToken;

        await (await ResolveClientAsync(cancellationToken)).SetWebhook(
            url: WebhookUrl,
            secretToken: secretToken,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task UnregisterAsync(CancellationToken cancellationToken)
    {
        await (await ResolveClientAsync(cancellationToken)).DeleteWebhook(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Resolves the default tenant's client on the ASYNCHRONOUS startup platform. The client is built
    /// from the tenant's credentials, and resolving credentials is asynchronous now — so it happens
    /// here, in the registration and the removal, rather than in a constructor a container calls
    /// synchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Client of the default tenant.</returns>
    private async Task<ITelegramBotClient> ResolveClientAsync(CancellationToken cancellationToken)
    {
        var result = await _clientFactory.GetOrCreateClientAsync<ITelegramBotClient>(
            ChannelCredentialContext.ForTenant(ChannelTypes.Telegram, tenantId: null),
            cancellationToken);

        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Failed to create the default tenant Telegram Bot client: {result.Error.Message}");
        }

        return result.Value;
    }
}
