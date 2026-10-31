// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Ambient gate of one button press handled by the channel pipeline: while it is open, a background
/// operation of <see cref="IClaimCompletionChannelDelivery"/> launched on the same flow is held back
/// until the handler of the press is left — on a normal way out, hence until after the press has
/// been answered (SPEC-041 L91); a handler left by an exception or a cancellation releases the gate
/// with the press unanswered.
/// </summary>
/// <remarks>
/// The confirmation that leaves a sign-in in the claims completion puts two messages into the chat:
/// the state of the completion, which answers the press, and the message of the step that has just
/// begun. The step starter runs inside the confirmation and hands its message to the background
/// executor before the answer is sent, so without the gate the two race — and in a channel whose
/// buttons live until the next message, the answer arriving second takes the buttons off the step
/// message. Implemented via <see cref="AsyncLocal{T}"/>, like the tenant scope of the processing
/// path: the gate is the scope of one press, not a registry keyed by the transaction, because the
/// starter is awaited on the very flow that opened it.
/// </remarks>
internal static class ChannelPressReplyGate
{
    /// <summary>
    /// Ambient storage of the gate of the current flow; null — no press is being handled on it.
    /// </summary>
    private static readonly AsyncLocal<GateScope?> CurrentScope = new();

    /// <summary>
    /// What a background operation launched on the current flow waits for before its own work: the
    /// release of the gate open on the flow, or an already completed task when there is none. Read on
    /// the launching flow — the operation itself runs with the flow suppressed and would see nothing.
    /// </summary>
    public static Task Released => CurrentScope.Value?.Released ?? Task.CompletedTask;

    /// <summary>
    /// Opens the gate for the rest of the handling of a press. Disposing the scope releases the gate
    /// — on every way out of the handler, an exception and a cancellation included — and restores
    /// the gate the flow had before.
    /// </summary>
    /// <returns>Scope that releases the gate on Dispose.</returns>
    public static IDisposable Open()
    {
        var scope = new GateScope(CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>
    /// One open gate: the signal of its release and the gate to restore after it.
    /// </summary>
    private sealed class GateScope : IDisposable
    {
        /// <summary>
        /// Signal of the release. Its continuations run asynchronously: releasing the gate must not
        /// carry out the waiting operation on the thread of the handler that releases it.
        /// </summary>
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gate the flow had before this one (restored on Dispose); null — there was none.
        /// </summary>
        private readonly GateScope? _previous;

        /// <summary>
        /// Creates an open gate remembering the previous one.
        /// </summary>
        /// <param name="previous">Gate the flow had before.</param>
        public GateScope(GateScope? previous)
        {
            _previous = previous;
        }

        /// <summary>
        /// Completes when the gate is released.
        /// </summary>
        public Task Released => _released.Task;

        /// <inheritdoc />
        public void Dispose()
        {
            CurrentScope.Value = _previous;
            _released.TrySetResult();
        }
    }
}
