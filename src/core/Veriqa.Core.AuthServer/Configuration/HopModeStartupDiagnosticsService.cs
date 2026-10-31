// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Startup report on browser fingerprint collection switched on where no fingerprint can be collected
/// (SPEC-025 HOP-090, HOP-123).
/// <para>
/// The switch is not refused: it takes effect only inside its three conditions — client signals
/// accepted, scanner context captured, and a page a presentation of a hop URL is answered with — and
/// outside them it collects nothing, which is the privacy-safe outcome. Staying silent is what it must
/// not do: the operator switched the collection on and would take it for working. Hence one Warning, at
/// the start, naming every condition that is missing.
/// </para>
/// <para>
/// The values are read through the single reading of the hop settings, and the first two conditions are
/// the one predicate both sides of the collection ask
/// (<see cref="HopObservationSettings.CollectsFingerprintOnPage"/>). The page is asked of the answers the
/// presentation itself goes by: the page of a channel (<see cref="HopChannelModes.GivesPageAsync"/>) and
/// the page of the channel buttons of the unified QR code (<see cref="HopUnifiedMode"/>), judged over the
/// registered channels. The observation settings are those of the CORE level. The page condition is met
/// at the core level or for at least one effective OIDC client entry, resolved with the context of that
/// application (SPEC-025 HOP-127) — without it the report would fire falsely where hop is turned on by a
/// client entry alone. A tenant is not asked about here — this is a report of the start, and no tenant
/// is owning anything at that moment. An Email channel is judged by the sign-in mode each of those
/// contexts puts in force — the core ceiling, narrowed by the tenant and the application of a client
/// entry: a hop URL is issued for its Push mail only, so on Pull it has no page a hop URL opens. Its
/// tenant credentials are read under the default implicit tenant, so a tenant that changes the mode by
/// its own credentials is not seen here.
/// </para>
/// </summary>
internal sealed class HopModeStartupDiagnosticsService : IHostedService
{
    /// <summary>
    /// Separator of the missing conditions in the report.
    /// </summary>
    private const string ConditionSeparator = "; ";

    /// <summary>
    /// The single reading of the hop settings.
    /// </summary>
    private readonly HopModeSettings _settings;

    /// <summary>
    /// Types of the channels registered in this deployment.
    /// </summary>
    private readonly IReadOnlyList<string> _channelTypes;

    /// <summary>
    /// Guarded access to the OIDC clients snapshot — the effective set of client entries.
    /// </summary>
    private readonly OidcClientsOptionsAccessor _clients;

    /// <summary>
    /// Factory of the scope the hop mode of a channel is asked in: the answer reads the channel adapters,
    /// whose lifetime a host may narrow below the process.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<HopModeStartupDiagnosticsService> _logger;

    /// <summary>
    /// Creates the startup report service.
    /// </summary>
    /// <param name="settings">Reading of the hop settings.</param>
    /// <param name="channels">Channels registered in this deployment.</param>
    /// <param name="clients">Guarded access to the OIDC clients snapshot.</param>
    /// <param name="scopeFactory">Factory of the scope the hop mode of a channel is asked in.</param>
    /// <param name="logger">Logger.</param>
    public HopModeStartupDiagnosticsService(
        HopModeSettings settings,
        IEnumerable<CoreChannelDeclaration> channels,
        OidcClientsOptionsAccessor clients,
        IServiceScopeFactory scopeFactory,
        ILogger<HopModeStartupDiagnosticsService> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _settings = settings;
        _channelTypes = [.. channels.Select(static channel => channel.ChannelType)];
        _clients = clients;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var observation = await _settings.ResolveObservationAsync(ResolutionContext.Core, cancellationToken);

        if (!observation.CollectFingerprint)
        {
            return;
        }

        var givesPage = await AnyPageAsync(cancellationToken);

        if (observation.CollectsFingerprintOnPage && givesPage)
        {
            return;
        }

        _logger.LogWarning(
            "Browser fingerprint collection is switched on by {Setting}, but no fingerprint is collected: "
            + "{MissingConditions}",
            HopModeConfigKeys.CoreAddress(nameof(HopModeOptions.CollectFingerprint)),
            string.Join(ConditionSeparator, MissingConditions(observation, givesPage)));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Tells whether a presentation of a hop URL is answered with a page at the core level or for at least
    /// one effective OIDC client entry: hop may be turned on by a client entry alone, and the page then
    /// exists for the requests of that application.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when there is a page to compute the fingerprint on.</returns>
    private async Task<bool> AnyPageAsync(CancellationToken cancellationToken)
    {
        // The answers read the channel adapters, whose lifetime a host may narrow below the process.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var channelModes = scope.ServiceProvider.GetRequiredService<HopChannelModes>();
        var unifiedMode = scope.ServiceProvider.GetRequiredService<HopUnifiedMode>();

        if (await GivesPageAsync(channelModes, unifiedMode, ResolutionContext.Core, cancellationToken))
        {
            return true;
        }

        foreach (var client in _clients.Current.Clients)
        {
            if (string.IsNullOrEmpty(client.ClientId))
            {
                continue;
            }

            var context = TransactionResolutionContext.ForClient(client.TenantId, client.ClientId, uiConfigSelector: null);

            if (await GivesPageAsync(channelModes, unifiedMode, context, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The registered channels a hop URL is issued for in one resolution context — the one the page is
    /// then judged with: whether the Email channel starts by its Push mail depends on the sign-in modes
    /// the context chooses.
    /// </summary>
    /// <param name="channelModes">The one answer to the hop mode of a channel.</param>
    /// <param name="context">Resolution context the answer is resolved with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Types of the registered channels a hop URL is issued for.</returns>
    private async Task<IReadOnlySet<string>> HopUrlChannelsAsync(
        HopChannelModes channelModes,
        ResolutionContext context,
        CancellationToken cancellationToken)
    {
        // The report runs outside any request, and the settings of the installation itself (the default
        // implicit tenant) are the answer it wants. That is said explicitly, so the credential read of a
        // channel is not mistaken for a request path that forgot to state its tenant.
        using var tenantScope = ChannelTenantContext.BeginScope(tenantId: null);

        return await channelModes.HopUrlChannelsAsync(context, _channelTypes, cancellationToken);
    }

    /// <summary>
    /// Tells whether a presentation of a hop URL is answered with a page as one resolution context sees
    /// the registered channels: the page of the channel buttons of the unified QR code, or the page of a
    /// channel.
    /// </summary>
    /// <param name="channelModes">The one answer to the hop mode of a channel.</param>
    /// <param name="unifiedMode">The one answer to whether the unified QR code applies.</param>
    /// <param name="context">Resolution context the answers are resolved with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the context has a page.</returns>
    private async Task<bool> GivesPageAsync(
        HopChannelModes channelModes,
        HopUnifiedMode unifiedMode,
        ResolutionContext context,
        CancellationToken cancellationToken)
    {
        if (await unifiedMode.ResolveAsync(context, _channelTypes, cancellationToken) is not null)
        {
            return true;
        }

        var hopUrlChannels = await HopUrlChannelsAsync(channelModes, context, cancellationToken);

        foreach (var channelType in _channelTypes)
        {
            if (await channelModes.GivesPageAsync(context, channelType, hopUrlChannels, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every condition of the collection that is not met.
    /// </summary>
    /// <param name="observation">Observation settings of the core level.</param>
    /// <param name="givesPage">
    /// Whether a presentation of a hop URL is answered with a page, at the core level or for an effective
    /// OIDC client entry.
    /// </param>
    /// <returns>One description per missing condition.</returns>
    private static IEnumerable<string> MissingConditions(HopObservationSettings observation, bool givesPage)
    {
        if (!observation.AcceptClientSignals)
        {
            yield return $"{HopModeConfigKeys.CoreAddress(nameof(HopModeOptions.AcceptClientSignals))} is false, "
                + "so there is no receiver for the signal carrying the fingerprint";
        }

        if (!observation.CollectScannerContext)
        {
            yield return $"{HopModeConfigKeys.CoreAddress(nameof(HopModeOptions.CollectScannerContext))} is false, "
                + "so there is no scanner context to keep the fingerprint in";
        }

        if (!givesPage)
        {
            yield return "no hop URL is answered with a page: no registered channel has the hop mode on with "
                + $"the redirect style {HopRedirectStyle.Interstitial} (an Email channel counts only while it "
                + "starts by its Push mail: no hop URL is issued for the Pull start), and neither QR mode "
                + $"{HopQrMode.Unified} nor {HopQrMode.UnifiedWithChannels} "
                + "applies, so there is no page to compute the fingerprint on "
                + $"({HopModeConfigKeys.RedirectStyleSetting.NodeWideAddress}, "
                + $"{HopModeConfigKeys.EnabledSetting.NodeWideAddress} and their per-channel values, "
                + "on the node or in the entry of an OIDC client)";
        }
    }
}
