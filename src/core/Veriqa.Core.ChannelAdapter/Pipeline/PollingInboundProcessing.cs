// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Events;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// How a polling loop hands an update to <see cref="ChannelInboundProcessor"/>. A polling service is a
/// singleton with no request around it, so it takes nothing of the processing in its constructor: each
/// update gets a scope of its own, and the processor with its collaborators comes from that scope.
/// </summary>
internal static class PollingInboundProcessing
{
    /// <summary>
    /// Refuses a composition a polling loop cannot process its updates in. Called from the constructor
    /// of a polling service, so the refusal surfaces when the hosted services are resolved — before the
    /// host starts, instead of as a loop that starts and never asks its question (SPEC-039 E41). The
    /// processor fails to resolve without the collaborators of the in-chat question; the journal of the
    /// refusals is required on a polling loop on top of what the processor itself requires.
    /// </summary>
    /// <remarks>
    /// The probe scope is disposed asynchronously, like the scope of an update: a scoped service that
    /// implements only <see cref="IAsyncDisposable"/> cannot be disposed synchronously, and a constructor
    /// can neither await nor block on the disposal without sync-over-async. What disposes synchronously is
    /// done before this returns; a disposal that completes later finishes on its own, and its failure is
    /// logged, not thrown — the composition has been checked by then.
    /// </remarks>
    /// <param name="scopeFactory">Scope factory of the host.</param>
    /// <param name="logger">Logger of the polling service, for a failed disposal of the probe.</param>
    /// <exception cref="InvalidOperationException">A required service is not registered; the message
    /// names its type.</exception>
    public static void EnsureComposed(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        var probe = scopeFactory.CreateAsyncScope();
        try
        {
            probe.ServiceProvider.GetRequiredService<ChannelInboundProcessor>();
            probe.ServiceProvider.GetRequiredService<ITransactionEventPublisher>();
        }
        finally
        {
            _ = DisposeProbeAsync(probe, logger);
        }
    }

    /// <summary>
    /// Disposes the probe scope of <see cref="EnsureComposed"/>.
    /// </summary>
    /// <param name="probe">The probe scope.</param>
    /// <param name="logger">Logger for a failed disposal.</param>
    /// <returns>The completed disposal.</returns>
    private static async Task DisposeProbeAsync(AsyncServiceScope probe, ILogger logger)
    {
        try
        {
            await probe.DisposeAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The probe scope of the polling composition check failed to dispose");
        }
    }

    /// <summary>
    /// Processes the result of one update in a scope of its own.
    /// </summary>
    /// <param name="scopeFactory">Scope factory of the host.</param>
    /// <param name="inboundResult">Result of processing the update, typed by intent.</param>
    /// <param name="adapter">Adapter of the polled channel.</param>
    /// <param name="replyTexts">Texts the channel declared at its registration.</param>
    /// <param name="cancellationToken">Cancellation token of the loop.</param>
    /// <returns>The completed processing.</returns>
    public static async Task ProcessAsync(
        IServiceScopeFactory scopeFactory,
        ChannelInboundResult inboundResult,
        IChannelAdapter adapter,
        ChannelReplyTexts replyTexts,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<ChannelInboundProcessor>();

        await processor.ProcessAsync(inboundResult, adapter, replyTexts, asksInChannelPrompt: true, cancellationToken);
    }
}
