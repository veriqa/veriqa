// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Viber.Configuration;
using Veriqa.Core.ChannelAdapter.Viber.Constants;
using Veriqa.Core.ChannelAdapter.Webhooks;

namespace Veriqa.Core.ChannelAdapter.Viber.Services;

/// <summary>
/// Viber bot webhook initialization service (SPEC-003 §11d, CA-210). Registers the webhook of the
/// bot of the default tenant in the Bot API in the background once the host accepts requests,
/// retrying until the platform accepts it, and removes it on shutdown. The webhooks of the bots of
/// other tenants are not registered here: whoever sets a tenant up registers its webhook, with the
/// tenant segment in the address.
/// </summary>
/// <remarks>
/// The platform answers the registration only after it has delivered a check event to the address
/// being registered and received a success for it, so the web server has to be listening by then —
/// which is why the base waits for the host to start before the first attempt.
/// </remarks>
internal sealed class ViberWebhookInitializerService : ChannelWebhookRegistrationService
{
    /// <summary>
    /// Events the webhook subscribes to besides the ones the platform always delivers (a message
    /// of a user among them). The opening of a conversation is what starts a sign-in, and the
    /// platform delivers it only on request.
    /// </summary>
    private static readonly IReadOnlyList<string> SubscribedEventTypes =
        [ViberAdapterConstants.EventConversationStarted];

    /// <summary>
    /// Outbound calls of the channel.
    /// </summary>
    private readonly ViberBotApiClient _botApi;

    /// <summary>
    /// Viber adapter settings — the source of the public address of the host.
    /// </summary>
    private readonly IOptionsMonitor<ViberOptions> _options;

    /// <summary>
    /// Creates a <see cref="ViberWebhookInitializerService"/> instance.
    /// </summary>
    /// <param name="botApi">Outbound calls of the channel.</param>
    /// <param name="options">Viber adapter settings.</param>
    /// <param name="state">Webhook registration state the channel health check reads.</param>
    /// <param name="timeProvider">Clock the pauses between registration attempts run on.</param>
    /// <param name="lifetime">Lifetime of the host; the first registration attempt waits for its start.</param>
    /// <param name="logger">Logger.</param>
    public ViberWebhookInitializerService(
        ViberBotApiClient botApi,
        IOptionsMonitor<ViberOptions> options,
        ChannelWebhookRegistrationState state,
        TimeProvider timeProvider,
        IHostApplicationLifetime lifetime,
        ILogger<ViberWebhookInitializerService> logger)
        : base(state, timeProvider, lifetime, logger)
    {
        _botApi = botApi ?? throw new ArgumentNullException(nameof(botApi));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    protected override string ChannelType => ChannelTypes.Viber;

    /// <summary>
    /// Always: the channel receives its events only through the webhook.
    /// </summary>
    protected override bool IsWebhookMode => true;

    /// <inheritdoc />
    protected override string WebhookUrl =>
        _options.CurrentValue.WebhookBaseUrl.TrimEnd('/') + CustomChannelConstants.BuildWebhookPath(ChannelTypes.Viber);

    /// <inheritdoc />
    protected override async Task RegisterAsync(CancellationToken cancellationToken)
    {
        // No request tenant here: the bot registered at startup is the one of the default
        // implicit tenant, and that is said explicitly (CA-164).
        using var tenantScope = ChannelTenantContext.BeginScope(tenantId: null);

        var result = await _botApi.SetWebhookAsync(WebhookUrl, SubscribedEventTypes, cancellationToken);

        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Message);
        }
    }

    /// <inheritdoc />
    protected override async Task UnregisterAsync(CancellationToken cancellationToken)
    {
        using var tenantScope = ChannelTenantContext.BeginScope(tenantId: null);

        // An empty address removes the webhook.
        var result = await _botApi.SetWebhookAsync(string.Empty, eventTypes: null, cancellationToken);

        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Message);
        }
    }
}
