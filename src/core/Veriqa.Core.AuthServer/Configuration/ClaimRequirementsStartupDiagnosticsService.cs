// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Startup report on a phone number required where no registered channel can provide it (SPEC-041 R20,
/// L48).
/// <para>
/// The requirement is not refused at the start: a channel may be added later, and until then the
/// authorization requests that ask for the phone scope are refused one by one with
/// <see cref="OidcErrorCodes.NoChannelForRequiredPhone"/>. Staying silent is what the start must not do —
/// the operator would learn about it only from the failed sign-ins. Hence one Warning per source of the
/// requirement: the core level, or else each client entry that requires the phone number on its own.
/// </para>
/// <para>
/// The levels are read through the one reading of the configured claim levels — the same one the
/// requirements of a transaction are written from. A client entry is resolved with the tenant it states
/// (<see cref="OidcClientOptions.TenantId"/>) — the tenant its authorization requests are resolved with
/// by default — so a tenant-level requirement of the phone number is reported as well.
/// </para>
/// </summary>
internal sealed class ClaimRequirementsStartupDiagnosticsService : IHostedService
{
    /// <summary>
    /// The one reading of the configured claim levels.
    /// </summary>
    private readonly ClaimRequirementSettings _settings;

    /// <summary>
    /// Client entries of the deployment.
    /// </summary>
    private readonly OidcClientsOptions _clientsOptions;

    /// <summary>
    /// Factory of the scope the channel adapters are read in: a host may narrow their lifetime to a scope.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ClaimRequirementsStartupDiagnosticsService> _logger;

    /// <summary>
    /// Creates the startup report service.
    /// </summary>
    /// <param name="settings">Reading of the configured claim levels.</param>
    /// <param name="clientsOptions">Client entries of the deployment.</param>
    /// <param name="scopeFactory">Factory of the scope the channel adapters are read in.</param>
    /// <param name="logger">Logger.</param>
    public ClaimRequirementsStartupDiagnosticsService(
        ClaimRequirementSettings settings,
        IOptions<OidcClientsOptions> clientsOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<ClaimRequirementsStartupDiagnosticsService> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clientsOptions);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _settings = settings;
        _clientsOptions = clientsOptions.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The method looks for a channel able to provide the phone number first — with one there is
        // nothing to report — and then reports the core level, or else every client requiring it itself.

        if (AnyChannelProvidesPhone())
        {
            return;
        }

        if (await RequiresPhoneAsync(ResolutionContext.Core, cancellationToken))
        {
            // Every client inherits the requirement of the core level: one report names its one source.
            ReportUnobtainablePhone(ScopesClaimsOptions.SectionName);
            return;
        }

        foreach (var client in _clientsOptions.Clients)
        {
            if (string.IsNullOrEmpty(client.ClientId))
            {
                continue;
            }

            var context = ResolutionContext.Of(client.TenantId, client.ClientId, uiConfigSelector: null);
            if (await RequiresPhoneAsync(context, cancellationToken))
            {
                ReportUnobtainablePhone(client.ClientId);
            }
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Whether a registered channel adapter declares a way to obtain the phone number (SPEC-041 C36).
    /// </summary>
    /// <returns><c>true</c> when at least one channel provides the phone number.</returns>
    private bool AnyChannelProvidesPhone()
    {
        using var scope = _scopeFactory.CreateScope();

        return scope.ServiceProvider.GetServices<IChannelAdapter>()
            .Any(adapter => adapter.Capabilities.PhoneNumber.Acquisition is not PhoneNumberAcquisition.None);
    }

    /// <summary>
    /// Whether the configuration requires the phone number in an ownership context.
    /// </summary>
    /// <param name="context">Ownership context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the phone number is required.</returns>
    private async Task<bool> RequiresPhoneAsync(ResolutionContext context, CancellationToken cancellationToken)
    {
        using var scope = new ConfigResolutionScope(context);
        var configured = await _settings.ResolveAsync(scope, cancellationToken);

        return configured.LevelOf(VeriqaClaimTypes.PhoneNumber) is ClaimRequirementLevel.Required;
    }

    /// <summary>
    /// Reports one source of a phone number requirement no registered channel can meet.
    /// </summary>
    /// <param name="source">The configuration section or the client that requires the phone number.</param>
    private void ReportUnobtainablePhone(string source) =>
        _logger.LogWarning(
            "The phone number is required by {Source}, but no registered channel can provide it: every "
            + "authorization request asking for the phone scope is refused with {ErrorCode}. Register a "
            + "channel that obtains the phone number (WhatsApp) or lift the requirement",
            source,
            OidcErrorCodes.NoChannelForRequiredPhone);
}
