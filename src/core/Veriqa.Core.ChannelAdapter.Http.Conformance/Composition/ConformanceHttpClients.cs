// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;

/// <summary>
/// The HTTP clients of the suite's own, beside the client of the wrapper: the sender of the calls the
/// wrapper does not make, and the client of the platform stub. They are named clients of the
/// compositions, so the recording handler of the suite stands in their chains as in the wrapper's one.
/// </summary>
internal static class ConformanceHttpClients
{
    /// <summary>
    /// Client of the sender of the calls the wrapper does not make.
    /// </summary>
    public const string Sender = "Veriqa.Conformance.Sender";

    /// <summary>
    /// Client of the platform stub of the stand.
    /// </summary>
    public const string PlatformStub = "Veriqa.Conformance.PlatformStub";

    /// <summary>
    /// How long one request of these clients may take — beyond the default operation timeout of the
    /// wrapper with a margin, so an adapter that hangs ends its scenario instead of the run.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Registers both clients. The sender follows no redirect and keeps no cookies — as the client of
    /// the wrapper: a redirect is an answer outside the wire contract.
    /// </summary>
    /// <param name="services">Services of a composition.</param>
    public static void AddTo(IServiceCollection services)
    {
        services
            .AddHttpClient(Sender, static client => client.Timeout = RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            });

        services.AddHttpClient(PlatformStub, static client => client.Timeout = RequestTimeout);
    }
}
