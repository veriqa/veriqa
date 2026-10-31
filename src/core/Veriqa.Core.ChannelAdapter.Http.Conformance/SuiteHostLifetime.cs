// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Hosting;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance;

/// <summary>
/// The lifetime of every host the suite starts — the compositions of the wrapper and the receiver of
/// events. Unlike the console lifetime a host registers by default, it does not take the signals of the
/// process: a host of the suite is started and stopped by the run, and a signal is taken once, by the
/// entry point, which cancels the run.
/// </summary>
internal sealed class SuiteHostLifetime : IHostLifetime
{
    /// <summary>
    /// Nothing to wait for: the host starts at once.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Nothing to release.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
