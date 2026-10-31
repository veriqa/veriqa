// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;

/// <summary>
/// What one call of the wrapper, or a group of calls a scenario makes together, did on the wire: the
/// exchanges the recording handler saw and how many times the wrapper asked the egress port. Open from
/// <see cref="WireCallRecorder.Capture"/> to <see cref="Dispose"/>, on the asynchronous flow that
/// opened it.
/// </summary>
internal sealed class WireCallCapture : IDisposable
{
    /// <summary>
    /// Message of a call that asked the egress port but reached no recording handler.
    /// </summary>
    private const string SeamMissingMessage =
        "The recording handler of the suite saw no request although the wrapper asked its egress port for a connection: the handler the suite adds to every HTTP client does not stand in the chain of the wrapper's client, so the suite cannot read the answers of the adapter.";

    /// <summary>
    /// Guard of <see cref="_exchanges"/> and <see cref="_egressQuestions"/>.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The exchanges, in the order they happened.
    /// </summary>
    private readonly List<RecordedExchange> _exchanges = [];

    /// <summary>
    /// The recorder that opened the capture.
    /// </summary>
    private readonly WireCallRecorder _recorder;

    /// <summary>
    /// The capture this one replaced on its flow; restored on <see cref="Dispose"/>.
    /// </summary>
    private readonly WireCallCapture? _outer;

    /// <summary>
    /// How many times the egress port was asked.
    /// </summary>
    private int _egressQuestions;

    /// <summary>
    /// Creates a capture.
    /// </summary>
    /// <param name="recorder">The recorder that opens it.</param>
    /// <param name="outer">The capture it replaces on its flow.</param>
    internal WireCallCapture(WireCallRecorder recorder, WireCallCapture? outer)
    {
        _recorder = recorder;
        _outer = outer;
    }

    /// <summary>
    /// The last exchange — the one whose answer reached the wrapper last; <c>null</c> — none. The
    /// retries of the wrapper stand above its HTTP client, so every attempt is an exchange of its own.
    /// </summary>
    public RecordedExchange? Last
    {
        get
        {
            lock (_gate)
            {
                return _exchanges.Count is 0 ? null : _exchanges[^1];
            }
        }
    }

    /// <summary>
    /// Stops the run when the wrapper asked for a connection and the recording handler saw nothing:
    /// without the handler the suite cannot judge any answer.
    /// </summary>
    /// <exception cref="ConformanceSetupException">The seam is missing.</exception>
    public void EnsureSeamPresent()
    {
        lock (_gate)
        {
            if (_egressQuestions > 0 && _exchanges.Count is 0)
            {
                throw new ConformanceSetupException(SeamMissingMessage);
            }
        }
    }

    /// <summary>
    /// Closes the capture and gives its flow back to the capture it replaced.
    /// </summary>
    public void Dispose() => _recorder.Restore(_outer);

    /// <summary>
    /// Adds an exchange.
    /// </summary>
    /// <param name="exchange">The exchange.</param>
    internal void Add(RecordedExchange exchange)
    {
        lock (_gate)
        {
            _exchanges.Add(exchange);
        }
    }

    /// <summary>
    /// Counts a question to the egress port.
    /// </summary>
    internal void CountEgressQuestion()
    {
        lock (_gate)
        {
            _egressQuestions++;
        }
    }
}
