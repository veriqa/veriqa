// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;

/// <summary>
/// The egress port of the compositions of the suite: allows every connection, as the shipped policy
/// does, and counts the questions into the capture of the call — a question with no recorded exchange
/// behind it means the recording handler is not in the chain of the wrapper's client.
/// </summary>
/// <param name="recorder">Recorder of the exchanges.</param>
internal sealed class CountingEgressPolicy(WireCallRecorder recorder) : IHttpChannelEgressPolicy
{
    /// <summary>
    /// Allows the connection and counts the question.
    /// </summary>
    /// <param name="request">What the wrapper is about to connect to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Always <c>true</c>.</returns>
    public ValueTask<bool> IsAllowedAsync(HttpChannelEgressRequest request, CancellationToken cancellationToken)
    {
        recorder.CountEgressQuestion();
        return ValueTask.FromResult(true);
    }
}
