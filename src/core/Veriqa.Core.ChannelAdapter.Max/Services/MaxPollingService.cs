// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;

using Max.BotClient;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.ChannelAdapter.Max.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;

namespace Veriqa.Core.ChannelAdapter.Max.Services;

/// <summary>
/// Per-tenant supervisor of MAX update long polling (SPEC-003 §11, TASK-051).
/// The supervisor takes the set of active polling tenants from <see cref="IPollingTenantSource"/> and
/// starts an independent long-poll loop for each one with its own bot client (from
/// <see cref="IChannelClientFactory"/> by <c>(Max, tenantId)</c>) and its own <c>marker</c>. Updates are
/// requested as a raw <c>GET /updates</c> and every one of them is handed to the adapter as its raw body —
/// the same reception point the webhook uses, so both modes read an update the same way.
/// N=1 (self-hosted) is the degenerate case: the set <c>[null]</c> ⇒ one loop, the default client.
/// Reaction to set changes without a restart — in the base class.
/// A simplified implementation for local development; the Webhook mode is recommended in production.
/// </summary>
internal sealed class MaxPollingService : PerTenantPollingSupervisor
{
    /// <summary>
    /// Channel client factory (per-tenant bot client by <c>(ChannelType, tenantId)</c>).
    /// </summary>
    private readonly IChannelClientFactory _clientFactory;

    /// <summary>
    /// MAX channel adapter.
    /// </summary>
    private readonly MaxChannelAdapter _channelAdapter;

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
    /// Creates a <see cref="MaxPollingService"/> instance.
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
    public MaxPollingService(
        IChannelClientFactory clientFactory,
        IPollingTenantSource tenantSource,
        IEnumerable<IChannelAdapter> channelAdapters,
        ILogger<MaxPollingService> logger,
        CustomChannelRegistry channels,
        IServiceScopeFactory scopeFactory)
        : base(tenantSource, logger)
    {
        // Initialize the dependencies and explicitly pick the MAX adapter
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _channelAdapter = ResolveMaxAdapter(channelAdapters);
        ArgumentNullException.ThrowIfNull(channels);
        _replyTexts = channels.ReplyTextsOf(ChannelTypes.Max);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

        PollingInboundProcessing.EnsureComposed(_scopeFactory, logger);
    }

    /// <inheritdoc />
    protected override string ChannelType => ChannelTypes.Max;

    /// <inheritdoc />
    protected override int RetryBaseDelayMilliseconds => MaxAdapterConstants.RetryBaseDelayMs;

    /// <summary>
    /// Picks the registered adapter for the MAX channel.
    /// </summary>
    /// <param name="channelAdapters">Collection of registered channel adapters.</param>
    /// <returns>MAX channel adapter.</returns>
    private static MaxChannelAdapter ResolveMaxAdapter(IEnumerable<IChannelAdapter> channelAdapters)
    {
        // Validate the input and look for the adapter with the MAX channel type
        ArgumentNullException.ThrowIfNull(channelAdapters);

        foreach (var adapter in channelAdapters)
        {
            if (adapter is MaxChannelAdapter maxAdapter)
            {
                return maxAdapter;
            }
        }

        throw new InvalidOperationException("The MAX channel adapter is not registered.");
    }

    /// <summary>
    /// Per-tenant MAX long-poll loop: its own bot client, its own <c>marker</c>, update processing
    /// in the tenant scope. Local retry on failure — does not bring down other tenants' loops.
    /// </summary>
    /// <param name="tenantId">Tenant identifier (<c>null</c> — the default implicit tenant).</param>
    /// <param name="cancellationToken">Cancellation token of the tenant's loop.</param>
    protected override async Task RunTenantPollLoopAsync(string? tenantId, CancellationToken cancellationToken)
    {
        // The tenant's bot client from the per-tenant factory by (Max, tenantId).
        var clientResult = await _clientFactory.GetOrCreateClientAsync<IBotClient>(
            ChannelCredentialContext.ForTenant(ChannelTypes.Max, tenantId),
            cancellationToken);

        if (clientResult.IsFailure)
        {
            // No token in the logs (core-rules §10): only the resolution error code/message.
            Logger.LogWarning(
                "Failed to create the tenant {TenantId} MAX client: {ErrorCode} — {ErrorMessage}. Loop not started",
                tenantId ?? ChannelTenantContext.DefaultTenantDisplayName,
                clientResult.Error.Code,
                clientResult.Error.Message);
            return;
        }

        var botClient = clientResult.Value;

        // marker is a local variable of THIS loop (per-tenant position isolation, requirement 2).
        long? marker = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Request updates via MAX Bot API long polling: a raw call through the tenant's client,
                // so the answer keeps every field of every update — the typed method of the client
                // library parses them into a model that drops the signature of a shared contact.
                var page = await botClient.SendRequest<JsonElement>(
                    HttpMethod.Get,
                    UpdatesRequestPath(marker),
                    body: null,
                    cancellationToken);

                // Process every update in the tenant scope (requirement 3).
                foreach (var rawUpdate in ReadUpdates(page))
                {
                    await ProcessUpdateSafelyAsync(rawUpdate, tenantId, cancellationToken);
                }

                // Move to the position the platform named; an answer that names none keeps the current
                // one — a request without a marker would get only the latest update.
                marker = ReadMarker(page) ?? marker;
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
                    "Error in the MAX polling loop of tenant {TenantId}. Retrying in {DelayMs} ms",
                    tenantId ?? ChannelTenantContext.DefaultTenantDisplayName,
                    MaxAdapterConstants.RetryBaseDelayMs);

                // Local delay before retrying (one tenant's failure does not bring down the others)
                await Task.Delay(MaxAdapterConstants.RetryBaseDelayMs, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Path and query of one long-poll request (MAX Bot API, <c>GET /updates</c>): the page size, the
    /// long-poll timeout and — once the platform has named one — the position of the next expected update.
    /// </summary>
    /// <param name="marker">Position of the next expected update; null — none named yet.</param>
    /// <returns>Path of the request, relative to the address of the client.</returns>
    private static string UpdatesRequestPath(long? marker)
    {
        var limit = MaxAdapterConstants.LongPollUpdatesLimit.ToString(CultureInfo.InvariantCulture);
        var timeout = MaxAdapterConstants.LongPollTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        var path = $"{MaxAdapterConstants.UpdatesPath}"
            + $"?{MaxAdapterConstants.UpdatesLimitQueryParam}={limit}"
            + $"&{MaxAdapterConstants.UpdatesTimeoutQueryParam}={timeout}";

        return marker is { } position
            ? $"{path}&{MaxAdapterConstants.UpdatesMarkerQueryParam}={position.ToString(CultureInfo.InvariantCulture)}"
            : path;
    }

    /// <summary>
    /// The raw bodies of the updates of one long-poll answer (its <c>updates</c> array), each exactly as
    /// the platform wrote it. An answer of another shape carries none.
    /// </summary>
    /// <param name="page">Answer of <c>GET /updates</c>.</param>
    /// <returns>Raw update bodies, in the order of the answer.</returns>
    private static List<ReadOnlyMemory<byte>> ReadUpdates(JsonElement page)
    {
        if (page.ValueKind is not JsonValueKind.Object
            || !page.TryGetProperty(MaxAdapterConstants.UpdatesResponseUpdatesField, out var updates)
            || updates.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var bodies = new List<ReadOnlyMemory<byte>>(updates.GetArrayLength());
        foreach (var update in updates.EnumerateArray())
        {
            bodies.Add(Encoding.UTF8.GetBytes(update.GetRawText()));
        }

        return bodies;
    }

    /// <summary>
    /// The position of the next expected update a long-poll answer names (its <c>marker</c>).
    /// </summary>
    /// <param name="page">Answer of <c>GET /updates</c>.</param>
    /// <returns>The position; null — the answer names none.</returns>
    private static long? ReadMarker(JsonElement page) =>
        page.ValueKind is JsonValueKind.Object
        && page.TryGetProperty(MaxAdapterConstants.UpdatesResponseMarkerField, out var marker)
        && marker.ValueKind is JsonValueKind.Number
        && marker.TryGetInt64(out var position)
            ? position
            : null;

    /// <summary>
    /// Safely processes one update via the pipeline (similar to webhook processing),
    /// setting the tenant scope around the processing (<see cref="ChannelTenantContext.BeginScope"/>).
    /// </summary>
    /// <param name="rawUpdate">Raw body of one MAX update.</param>
    /// <param name="tenantId">Tenant identifier (<c>null</c> — default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task ProcessUpdateSafelyAsync(
        ReadOnlyMemory<byte> rawUpdate,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Tenant context around the processing: the adapter/factory/pipeline take this tenant's credentials.
            using var tenantScope = ChannelTenantContext.BeginScope(tenantId);

            // The update enters the adapter at the reception point of the webhook, as its raw body: the
            // signature of a shared contact lives only there.
            var result = await _channelAdapter.ProcessRawUpdateAsync(rawUpdate, cancellationToken);

            if (result.IsFailure)
            {
                Logger.LogWarning(
                    "Error processing a MAX update: {ErrorCode} — {ErrorMessage}",
                    result.Error.Code,
                    result.Error.Message);
                return;
            }

            // Routing via the pipeline. The texts the channel states for itself come from its
            // registration, the same source the webhook route reads them from.
            await PollingInboundProcessing.ProcessAsync(
                _scopeFactory, result.Value, _channelAdapter, _replyTexts, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Unhandled exception while processing a MAX update");
        }
    }
}
