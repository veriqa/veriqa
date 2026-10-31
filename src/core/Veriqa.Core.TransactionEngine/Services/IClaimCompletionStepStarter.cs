// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// Starts one step of the claims completion (SPEC-041 L91) — what has to happen when the step begins,
/// before the pages are told about it: the phone request in the channel, the surface of the web step.
/// </summary>
/// <remarks>
/// At most one starter per step is registered; a component extending the starter of a step replaces
/// it rather than registering a second one. When several are registered, the engine asks the one
/// registered last. A step without a starter begins with the step signal alone.
/// <para>
/// The engine resolves starters from a scope of its own per operation, so any lifetime is safe. A
/// starter must not depend on <see cref="ITransactionService"/> in its constructor: the service owns
/// the call, and that dependency would be a cycle.
/// </para>
/// </remarks>
public interface IClaimCompletionStepStarter
{
    /// <summary>
    /// Step this starter starts.
    /// </summary>
    ClaimCompletionStep Step { get; }

    /// <summary>
    /// Starts the step. Called when the step begins, before the step signal is published; returns
    /// after the synchronous part of the start — for the web step, after the surface is written through
    /// <c>ITransactionStore.TrySetWebStepSurfaceAsync</c>. A delivery into the channel with the retries
    /// of SPEC-003 §12.2 runs outside this call.
    /// </summary>
    /// <remarks>
    /// An exception is logged as a warning by the engine and does not interrupt the step: the signal is
    /// published all the same.
    /// </remarks>
    /// <param name="transaction">The transaction as it stood when the step began.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task completing when the synchronous part of the start is done.</returns>
    Task StartAsync(Transaction transaction, CancellationToken cancellationToken);
}
