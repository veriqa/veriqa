// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.DependencyInjection;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;

/// <summary>
/// One variant of the wrapper, composed outside a Veriqa host the way the wrapper package promises:
/// the transaction engine, the channel contour with the HTTP channels, and the catalog of the
/// inbound-verification axis the contour reads. The composition is a started host: only the start runs
/// the start-up checks of the core (SPEC-042 E55, the declared inbound verification), so a run never
/// goes against a configuration Veriqa would refuse.
/// </summary>
internal sealed class WrapperComposition : IAsyncDisposable
{
    /// <summary>
    /// The started host.
    /// </summary>
    private readonly IHost _host;

    /// <summary>
    /// Creates the composition over a started host.
    /// </summary>
    /// <param name="host">The host.</param>
    /// <param name="adapter">The wrapper of the channel under test.</param>
    /// <param name="ownerTenantId">The tenant that owns the registration under test; <c>null</c> — none.</param>
    private WrapperComposition(IHost host, IChannelAdapter adapter, string? ownerTenantId)
    {
        _host = host;
        Adapter = adapter;
        OwnerTenantId = ownerTenantId;
    }

    /// <summary>
    /// The wrapper of the channel under test, as the core resolves it: by its channel type among the
    /// channel adapters of the composition.
    /// </summary>
    public IChannelAdapter Adapter { get; }

    /// <summary>
    /// The tenant that owns the registration under test and is the only one whose route it accepts
    /// events on (SPEC-042 R60), as the wrapper package declares it to the core in the
    /// <see cref="CoreChannelDeclaration"/> of the channel. <c>null</c> — the registration of the
    /// operator, which serves every tenant.
    /// </summary>
    public string? OwnerTenantId { get; }

    /// <summary>
    /// The HTTP clients of the composition — the named clients of the suite are taken from here per
    /// operation (<see cref="ConformanceHttpClients"/>).
    /// </summary>
    public IHttpClientFactory HttpClients => _host.Services.GetRequiredService<IHttpClientFactory>();

    /// <summary>
    /// Composes and starts one variant.
    /// </summary>
    /// <param name="configuration">Configuration of the suite — the channel section is read from it.</param>
    /// <param name="channelType">The channel under test.</param>
    /// <param name="seams">What every composition of the suite carries.</param>
    /// <param name="variant">What sets this variant apart.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The started composition.</returns>
    /// <exception cref="ConformanceSetupException">The composition does not start, or it has no enabled registration of the channel.</exception>
    public static async Task<WrapperComposition> StartAsync(
        IConfiguration configuration,
        string channelType,
        WrapperSeams seams,
        WrapperVariant variant,
        CancellationToken cancellationToken)
    {
        IHost host;
        try
        {
            host = Build(configuration, seams, variant);
        }
        catch (Exception exception)
        {
            throw NotStarted(variant, exception);
        }

        try
        {
            await host.StartAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            host.Dispose();
            throw NotStarted(variant, exception);
        }

        var adapter = host.Services
            .GetServices<IChannelAdapter>()
            .FirstOrDefault(candidate => string.Equals(candidate.ChannelType, channelType, StringComparison.Ordinal));

        if (adapter is null)
        {
            await StopAsync(host);
            throw new ConformanceSetupException(
                "The configuration has no enabled HTTP channel '" + channelType + "': the " + variant.Name
                + " composition of the wrapper registered no adapter under that name.");
        }

        var ownerTenantId = host.Services
            .GetServices<CoreChannelDeclaration>()
            .FirstOrDefault(declaration => string.Equals(declaration.ChannelType, channelType, StringComparison.Ordinal))
            ?.OwnerTenantId;

        return new WrapperComposition(host, adapter, ownerTenantId);
    }

    /// <summary>
    /// Stops the host.
    /// </summary>
    /// <returns>A task that completes when the host is stopped and disposed.</returns>
    public async ValueTask DisposeAsync() => await StopAsync(_host);

    /// <summary>
    /// Builds the host of a variant. The seams of the suite are registered before the first call of the
    /// composition: the composition registers the shipped ones only where none is registered yet.
    /// </summary>
    /// <param name="configuration">Configuration of the suite.</param>
    /// <param name="seams">What every composition of the suite carries.</param>
    /// <param name="variant">What sets this variant apart.</param>
    /// <returns>The host, not started.</returns>
    private static IHost Build(IConfiguration configuration, WrapperSeams seams, WrapperVariant variant)
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);

        builder.Configuration.AddConfiguration(configuration);
        if (variant.ConfigurationOverlay is { } overlay)
        {
            builder.Configuration.AddInMemoryCollection(overlay);
        }

        // Warnings of the wrapper and the core go to the error stream: the standard output carries the
        // lines of the scenarios alone.
        builder.Logging
            .AddConsole(static console => console.LogToStandardErrorThreshold = LogLevel.Trace)
            .SetMinimumLevel(LogLevel.Warning);

        // The signals of the process belong to the entry point: a composition does not swallow them.
        builder.Services.AddSingleton<IHostLifetime, SuiteHostLifetime>();

        if (variant.Clock is { } clock)
        {
            builder.Services.AddSingleton<TimeProvider>(clock);
        }

        builder.Services.AddSingleton<IHttpChannelEgressPolicy>(new CountingEgressPolicy(seams.Recorder));
        builder.Services.AddSingleton<IHttpChannelInboundEventIdSource>(seams.EventIds);

        // The recording handler joins every HTTP client of the composition — the mechanism a host uses
        // to put its own handlers on all clients; the wrapper removes resilience handlers only.
        builder.Services.ConfigureHttpClientDefaults(
            http => http.AddHttpMessageHandler(() => new WireRecordingHandler(seams.Recorder)));
        ConformanceHttpClients.AddTo(builder.Services);

        builder.Services.AddVeriqaTransactionEngine(builder.Configuration);
        builder.Services.AddVeriqaChannelAdapters(builder.Configuration, static adapters => adapters.AddHttpChannels());
        builder.Services.AddVeriqaConfigKeyCatalogs(builder.Configuration, ChannelInboundVerificationConfigKeys.Catalog);

        return builder.Build();
    }

    /// <summary>
    /// The failure of a composition to come up. The message of the composition — an error of the
    /// configuration of the channel (SPEC-042 E55) or of its declared inbound verification — is kept as
    /// it is: it names the channel and the key.
    /// </summary>
    /// <param name="variant">The variant.</param>
    /// <param name="exception">What the composition threw.</param>
    /// <returns>The exception to throw.</returns>
    private static ConformanceSetupException NotStarted(WrapperVariant variant, Exception exception) =>
        new("The " + variant.Name + " composition of the wrapper did not start: " + exception.Message, exception);

    /// <summary>
    /// Stops and disposes a host.
    /// </summary>
    /// <param name="host">The host.</param>
    /// <returns>A task that completes when the host is disposed.</returns>
    private static async Task StopAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }
}
