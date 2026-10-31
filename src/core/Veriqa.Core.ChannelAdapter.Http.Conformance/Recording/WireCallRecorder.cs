// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;

/// <summary>
/// Routes what the recording handler and the egress port of every composition see to the capture open
/// on the asynchronous flow of the call — so a scenario reads the exchanges of its own calls, even while
/// the receiver makes calls of its own on other flows. Outside a capture nothing is kept.
/// </summary>
internal sealed class WireCallRecorder
{
    /// <summary>
    /// The capture open on the current flow.
    /// </summary>
    private readonly AsyncLocal<WireCallCapture?> _current = new();

    /// <summary>
    /// Opens a capture on the current flow; the calls made on it until <see cref="IDisposable.Dispose"/>
    /// are recorded into it.
    /// </summary>
    /// <returns>The capture.</returns>
    public WireCallCapture Capture()
    {
        var capture = new WireCallCapture(this, _current.Value);
        _current.Value = capture;
        return capture;
    }

    /// <summary>
    /// Records an exchange into the capture of the current flow.
    /// </summary>
    /// <param name="exchange">The exchange.</param>
    public void Record(RecordedExchange exchange) => _current.Value?.Add(exchange);

    /// <summary>
    /// Counts a question to the egress port in the capture of the current flow.
    /// </summary>
    public void CountEgressQuestion() => _current.Value?.CountEgressQuestion();

    /// <summary>
    /// Makes a capture current again when an inner one closes.
    /// </summary>
    /// <param name="capture">The capture to restore.</param>
    internal void Restore(WireCallCapture? capture) => _current.Value = capture;
}
