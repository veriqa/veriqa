// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Telegram.Bot;
using Telegram.Bot.Types;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.Telegram.Constants;

namespace Veriqa.Core.ChannelAdapter.Telegram.Services;

/// <summary>
/// Per-tenant supervisor of Telegram update long polling (CA-083, TASK-051).
/// The supervisor takes the set of active polling tenants from <see cref="IPollingTenantSource"/> and
/// starts an independent long-poll loop for each one with its own bot client (from
/// <see cref="IChannelClientFactory"/> by <c>(Telegram, tenantId)</c>) and its own <c>offset</c>.
/// N=1 (self-hosted) is the degenerate case: the set <c>[null]</c> ⇒ one loop, the default client.
/// Reaction to set changes without a restart — in the base class.
/// A simplified implementation for local development; the Webhook mode is recommended in production.
/// </summary>
internal sealed class TelegramPollingService : PerTenantPollingSupervisor
{
    /// <summary>
    /// Channel client factory (per-tenant bot client by <c>(ChannelType, tenantId)</c>).
    /// </summary>
    private readonly IChannelClientFactory _clientFactory;

    /// <summary>
    /// Telegram channel adapter.
    /// </summary>
    private readonly TelegramChannelAdapter _channelAdapter;

    /// <summary>
    /// Scope factory: every update is processed in a scope of its own, and the processing with its
    /// collaborators comes from that scope — this loop is a singleton, and a collaborator the host
    /// registered as scoped must not be captured by it.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Texts this channel declared at its registration. The loop drives the very same pipeline as the
    /// webhook route, so it states the very same texts — read off the registration rather than named
    /// here, or an installation that switched a reply off would still be answered by this loop.
    /// </summary>
    private readonly ChannelReplyTexts _replyTexts;

    /// <summary>
    /// Creates a <see cref="TelegramPollingService"/> instance.
    /// </summary>
    /// <remarks>
    /// The constructor resolves the processing of an update once, in a probe scope: a composition
    /// without the collaborators of the in-chat question, or without the journal of the refusals, is
    /// refused while the hosted services are resolved, with the name of the missing type — not
    /// discovered as a loop that starts and never asks its question (SPEC-039 E41).
    /// </remarks>
    /// <param name="clientFactory">Channel client factory (per-tenant bot client).</param>
    /// <param name="tenantSource">Source of active polling tenants.</param>
    /// <param name="channelAdapters">Registered channel adapters.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="channels">Registry the channel's own declared reply texts are read from.</param>
    /// <param name="scopeFactory">Scope factory the scope of each update is created by.</param>
    public TelegramPollingService(
        IChannelClientFactory clientFactory,
        IPollingTenantSource tenantSource,
        IEnumerable<IChannelAdapter> channelAdapters,
        ILogger<TelegramPollingService> logger,
        CustomChannelRegistry channels,
        IServiceScopeFactory scopeFactory)
        : base(tenantSource, logger)
    {
        // Initialize the dependencies and explicitly pick the Telegram adapter
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _channelAdapter = ResolveTelegramAdapter(channelAdapters);
        ArgumentNullException.ThrowIfNull(channels);
        _replyTexts = channels.ReplyTextsOf(ChannelTypes.Telegram);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

        PollingInboundProcessing.EnsureComposed(_scopeFactory, logger);
    }

    /// <inheritdoc />
    protected override string ChannelType => ChannelTypes.Telegram;

    /// <inheritdoc />
    protected override int RetryBaseDelayMilliseconds => TelegramAdapterConstants.RetryBaseDelayMs;

    /// <summary>
    /// Picks the registered adapter for the Telegram channel.
    /// </summary>
    /// <param name="channelAdapters">Collection of registered channel adapters.</param>
    /// <returns>Telegram channel adapter.</returns>
    private static TelegramChannelAdapter ResolveTelegramAdapter(IEnumerable<IChannelAdapter> channelAdapters)
    {
        // Validate the input and look for the adapter with the Telegram channel type
        ArgumentNullException.ThrowIfNull(channelAdapters);

        foreach (var adapter in channelAdapters)
        {
            if (adapter is TelegramChannelAdapter telegramAdapter)
            {
                return telegramAdapter;
            }
        }

        throw new InvalidOperationException("The Telegram channel adapter is not registered.");
    }

    /// <summary>
    /// Per-tenant Telegram long-poll loop: its own bot client, its own <c>offset</c>, update
    /// processing in the tenant scope. Local retry on failure — does not bring down other tenants' loops.
    /// </summary>
    /// <param name="tenantId">Tenant identifier (<c>null</c> — the default implicit tenant).</param>
    /// <param name="cancellationToken">Cancellation token of the tenant's loop.</param>
    protected override async Task RunTenantPollLoopAsync(string? tenantId, CancellationToken cancellationToken)
    {
        // The tenant's bot client from the per-tenant factory by (Telegram, tenantId).
        var clientResult = await _clientFactory.GetOrCreateClientAsync<ITelegramBotClient>(
            ChannelCredentialContext.ForTenant(ChannelTypes.Telegram, tenantId),
            cancellationToken);

        if (clientResult.IsFailure)
        {
            // No token in the logs (core-rules §10): only the resolution error code/message.
            Logger.LogWarning(
                "Failed to create the tenant {TenantId} Telegram client: {ErrorCode} — {ErrorMessage}. Loop not started",
                tenantId ?? ChannelTenantContext.DefaultTenantDisplayName,
                clientResult.Error.Code,
                clientResult.Error.Message);
            return;
        }

        var botClient = clientResult.Value;

        // offset is a local variable of THIS loop (per-tenant position isolation, requirement 2).
        var offset = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Request updates via long polling
                var updates = await botClient.GetUpdates(
                    offset: offset,
                    timeout: TelegramAdapterConstants.LongPollTimeoutSeconds,
                    cancellationToken: cancellationToken);

                // Process every update in the tenant scope (requirement 3).
                foreach (var update in updates)
                {
                    await ProcessUpdateSafelyAsync(update, tenantId, cancellationToken);

                    // Advance the offset to acknowledge receipt of the update
                    offset = update.Id + 1;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal completion when the tenant loop/host is stopped
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    "Error in the Telegram polling loop of tenant {TenantId}. Retrying in {DelayMs} ms",
                    tenantId ?? ChannelTenantContext.DefaultTenantDisplayName,
                    TelegramAdapterConstants.RetryBaseDelayMs);

                // Local delay before retrying (one tenant's failure does not bring down the others)
                await Task.Delay(TelegramAdapterConstants.RetryBaseDelayMs, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Safely processes one update via the pipeline (similar to webhook processing),
    /// setting the tenant scope around the processing (<see cref="ChannelTenantContext.BeginScope"/>).
    /// </summary>
    /// <param name="update">Telegram update.</param>
    /// <param name="tenantId">Tenant identifier (<c>null</c> — default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task ProcessUpdateSafelyAsync(Update update, string? tenantId, CancellationToken cancellationToken)
    {
        try
        {
            // Tenant context around the processing: the adapter/factory/pipeline take this tenant's credentials.
            using var tenantScope = ChannelTenantContext.BeginScope(tenantId);

            // Pass the update to the channel adapter for processing
            // Polling receives an already deserialized update from the platform library; serializing
            // it back just to pass through the neutral envelope would be wasteful and fragile, so the
            // loop enters the adapter's internal reception point. Both paths meet inside the adapter.
            var result = await _channelAdapter.ProcessUpdateAsync(update, cancellationToken);

            if (result.IsFailure)
            {
                Logger.LogWarning(
                    "Error processing update {UpdateId}: {ErrorCode} — {ErrorMessage}",
                    update.Id,
                    result.Error.Code,
                    result.Error.Message);
                return;
            }

            // Routing via the pipeline (similar to webhook processing). The terminal wording comes
            // from the core through the receipt port; the texts the channel states for itself come
            // from its registration, the same source the webhook route reads them from.
            await PollingInboundProcessing.ProcessAsync(
                _scopeFactory, result.Value, _channelAdapter, _replyTexts, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Unhandled exception while processing update {UpdateId}",
                update.Id);
        }
    }
}
