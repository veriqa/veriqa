// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Max.BotClient;
using Max.BotClient.Types;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Max.Configuration;
using Veriqa.Core.ChannelAdapter.Max.Constants;
using Veriqa.Core.ChannelAdapter.Max.Enums;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Webhooks;

namespace Veriqa.Core.ChannelAdapter.Max.Services;

/// <summary>
/// MAX bot webhook initialization service (SPEC-003 §11, SPEC-012).
/// Registers the webhook in the MAX Bot API in the background after startup, retrying until the
/// platform accepts it, and deletes it on shutdown. Active only with <see cref="MaxUpdateMode.Webhook"/>.
/// </summary>
internal sealed class MaxWebhookInitializerService : ChannelWebhookRegistrationService
{
    /// <summary>
    /// Update types required for the adapter to work. A shared contact — the answer to the phone number
    /// request — arrives as message_created: without it in the subscription the platform never delivers
    /// the contact, and the phone step waits until the sign-in expires.
    /// </summary>
    private static readonly UpdateType[] SubscribedUpdateTypes =
    [
        UpdateType.BotStarted,
        UpdateType.MessageCallback,
        UpdateType.MessageCreated
    ];

    /// <summary>
    /// MAX Bot API client.
    /// </summary>
    private readonly IChannelClientFactory _clientFactory;

    /// <summary>
    /// MAX adapter settings.
    /// </summary>
    private readonly MaxOptions _options;

    /// <summary>
    /// Creates a <see cref="MaxWebhookInitializerService"/> instance.
    /// </summary>
    /// <param name="clientFactory">Per-tenant channel client factory.</param>
    /// <param name="options">MAX adapter settings.</param>
    /// <param name="state">Webhook registration state the channel health check reads.</param>
    /// <param name="timeProvider">Clock the pauses between registration attempts run on.</param>
    /// <param name="lifetime">Lifetime of the host; the first registration attempt waits for its start.</param>
    /// <param name="logger">Logger.</param>
    public MaxWebhookInitializerService(
        IChannelClientFactory clientFactory,
        IOptions<MaxOptions> options,
        ChannelWebhookRegistrationState state,
        TimeProvider timeProvider,
        IHostApplicationLifetime lifetime,
        ILogger<MaxWebhookInitializerService> logger)
        : base(state, timeProvider, lifetime, logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override string ChannelType => ChannelTypes.Max;

    /// <inheritdoc />
    protected override bool IsWebhookMode => _options.UpdateMode is MaxUpdateMode.Webhook;

    /// <inheritdoc />
    protected override string WebhookUrl => _options.WebhookBaseUrl.TrimEnd('/') + MaxAdapterConstants.WebhookPath;

    /// <inheritdoc />
    protected override async Task RegisterAsync(CancellationToken cancellationToken)
    {
        // In a valid configuration the secret token is mandatory for the Webhook mode
        // (checked by MaxOptionsValidator at startup)
        // Startup core-only: webhook is registered once at boot; no request tenant here, so this stays a direct core-level read (unlike the per-request adapters migrated to the seam in TASK-072/B4).
        var secretToken = _options.WebhookSecretToken;

        var result = await (await ResolveClientAsync(cancellationToken)).Subscribe(
            url: WebhookUrl,
            updateTypes: SubscribedUpdateTypes,
            secret: secretToken,
            cancellationToken: cancellationToken);

        // A refusal comes back as an answer rather than an exception; it is not a registration.
        if (!result.Success)
        {
            throw new InvalidOperationException($"The MAX Bot API refused the webhook subscription: {result.Message}");
        }
    }

    /// <inheritdoc />
    protected override async Task UnregisterAsync(CancellationToken cancellationToken)
    {
        var result = await (await ResolveClientAsync(cancellationToken)).Unsubscribe(
            url: WebhookUrl,
            cancellationToken: cancellationToken);

        // A refusal comes back as an answer rather than an exception; the webhook is still there.
        if (!result.Success)
        {
            throw new InvalidOperationException($"The MAX Bot API refused to remove the webhook subscription: {result.Message}");
        }
    }

    /// <summary>
    /// Resolves the default tenant's client on the ASYNCHRONOUS startup platform. The client is built
    /// from the tenant's credentials, and resolving credentials is asynchronous now — so it happens
    /// here, in the registration and the removal, rather than in a constructor a container calls
    /// synchronously.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Client of the default tenant.</returns>
    private async Task<IBotClient> ResolveClientAsync(CancellationToken cancellationToken)
    {
        var result = await _clientFactory.GetOrCreateClientAsync<IBotClient>(
            ChannelCredentialContext.ForTenant(ChannelTypes.Max, tenantId: null),
            cancellationToken);

        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"Failed to create the default tenant MAX Bot client: {result.Error.Message}");
        }

        return result.Value;
    }
}
