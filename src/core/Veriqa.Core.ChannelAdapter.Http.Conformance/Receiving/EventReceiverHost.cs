// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Receiving;

/// <summary>
/// The web application of the receiver: the webhook routes of the channel under test — the paths of
/// the core, <c>POST /api/channels/{channel}/webhook</c> and <c>…/webhook/{tenant}</c>, so an adapter
/// only changes the base address of the core — on the address of the standard Kestrel configuration
/// (<c>ASPNETCORE_URLS</c> / <c>ASPNETCORE_HTTP_PORTS</c>).
/// </summary>
internal sealed class EventReceiverHost : IAsyncDisposable
{
    /// <summary>
    /// The started application.
    /// </summary>
    private readonly WebApplication _app;

    /// <summary>
    /// Creates the host over a started application.
    /// </summary>
    /// <param name="app">The application.</param>
    private EventReceiverHost(WebApplication app) => _app = app;

    /// <summary>
    /// Builds the application from the builder of the suite and starts it.
    /// </summary>
    /// <param name="builder">The builder of the suite — its configuration carries the address.</param>
    /// <param name="channelType">The channel under test.</param>
    /// <param name="receiver">The receiver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The started host.</returns>
    /// <exception cref="ConformanceSetupException">The address cannot be listened on.</exception>
    public static async Task<EventReceiverHost> StartAsync(
        WebApplicationBuilder builder,
        string channelType,
        EventReceiver receiver,
        CancellationToken cancellationToken)
    {
        // Warnings go to the error stream: the standard output carries the lines of the scenarios.
        builder.Logging
            .ClearProviders()
            .AddConsole(static console => console.LogToStandardErrorThreshold = LogLevel.Trace)
            .SetMinimumLevel(LogLevel.Warning);

        // The signals of the process belong to the entry point: the receiver does not swallow them.
        builder.Services.AddSingleton<IHostLifetime, SuiteHostLifetime>();

        var app = builder.Build();

        var path = CustomChannelConstants.BuildWebhookPath(channelType);
        RequestDelegate handle = receiver.HandleAsync;
        app.MapPost(path, handle);
        app.MapPost(path + "/{" + EventReceiver.TenantRouteKey + "}", handle);

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (IOException exception)
        {
            await app.DisposeAsync();
            throw new ConformanceSetupException(
                "The receiver of events cannot listen on its address (ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS): " + exception.Message,
                exception);
        }

        return new EventReceiverHost(app);
    }

    /// <summary>
    /// Stops the application.
    /// </summary>
    /// <returns>A task that completes when it is stopped and disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }
}
