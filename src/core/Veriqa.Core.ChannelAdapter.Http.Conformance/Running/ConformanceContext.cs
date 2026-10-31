// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Platform;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Receiving;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Schema;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Sending;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Running;

/// <summary>
/// What a scenario works with: the options of the run, the compositions of the wrapper, the seams of the
/// suite, the schema of the wire and what earlier scenarios of the run left for later ones. Scenarios run
/// one after another, in the order of the catalog.
/// </summary>
/// <param name="options">Options of the run.</param>
/// <param name="compositions">The started compositions of the wrapper.</param>
/// <param name="seams">Seams of the suite.</param>
/// <param name="schema">Schema of the wire.</param>
/// <param name="parties">Who talks to the adapter besides the wrapper.</param>
internal sealed class ConformanceContext(
    ConformanceOptions options,
    WrapperCompositions compositions,
    WrapperSeams seams,
    WireSchema schema,
    ConformanceParties parties)
{
    /// <summary>
    /// The call bodies of the scenarios that are sources of the sender, by the identifier of the scenario.
    /// </summary>
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _sourceBodies = new(StringComparer.Ordinal);

    /// <summary>
    /// Options of the run.
    /// </summary>
    public ConformanceOptions Options { get; } = options;

    /// <summary>
    /// The started compositions of the wrapper.
    /// </summary>
    public WrapperCompositions Compositions { get; } = compositions;

    /// <summary>
    /// Recorder of the exchanges of the wrapper.
    /// </summary>
    public WireCallRecorder Recorder => seams.Recorder;

    /// <summary>
    /// Source of the identifier of the played inbound event.
    /// </summary>
    public ConformanceInboundEventIds EventIds => seams.EventIds;

    /// <summary>
    /// Schema of the wire.
    /// </summary>
    public WireSchema Schema { get; } = schema;

    /// <summary>
    /// The prompt <c>send-prompt.success</c> sent — the transaction and the coordinates the adapter
    /// returned (<c>null</c> — none); <c>null</c> — that scenario has not run.
    /// </summary>
    public SentPrompt? SentPrompt { get; set; }

    /// <summary>
    /// The sender of the calls the wrapper does not make.
    /// </summary>
    public OutOfWrapperSender Sender => parties.Sender;

    /// <summary>
    /// The platform stub; <c>null</c> outside the automatic mode.
    /// </summary>
    public PlatformStubClient? PlatformStub => parties.PlatformStub;

    /// <summary>
    /// The receiver of events of a mode that runs it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The mode runs no receiver.</exception>
    public EventReceiver Receiver =>
        parties.Receiver ?? throw new InvalidOperationException("The receiver of events does not run in this mode.");

    /// <summary>
    /// The platform stub of the automatic mode.
    /// </summary>
    /// <exception cref="InvalidOperationException">The mode drives no platform stub.</exception>
    public PlatformStubClient RequiredPlatformStub =>
        parties.PlatformStub ?? throw new InvalidOperationException("The platform stub is driven in the automatic mode alone.");

    /// <summary>
    /// Keeps the body of the last call a scenario made, for the sender to mutate.
    /// </summary>
    /// <param name="scenarioId">Identifier of the scenario.</param>
    /// <param name="capture">What its call did on the wire; a call that reached no adapter leaves nothing.</param>
    public void KeepSourceBody(string scenarioId, WireCallCapture capture)
    {
        if (capture.Last is { } exchange)
        {
            _sourceBodies[scenarioId] = exchange.RequestBody;
        }
    }

    /// <summary>
    /// The body a scenario kept.
    /// </summary>
    /// <param name="scenarioId">Identifier of the scenario.</param>
    /// <returns>The body; <c>null</c> — the scenario made no call to the adapter.</returns>
    public ReadOnlyMemory<byte>? SourceBodyOf(string scenarioId) =>
        _sourceBodies.TryGetValue(scenarioId, out var body) ? body : null;
}

/// <summary>
/// A prompt sent in a run.
/// </summary>
/// <param name="TransactionId">Transaction of the prompt.</param>
/// <param name="Message">Coordinates of the prompt; <c>null</c> — the adapter returned none.</param>
internal sealed record SentPrompt(TransactionId TransactionId, ChannelMessageRef? Message);
