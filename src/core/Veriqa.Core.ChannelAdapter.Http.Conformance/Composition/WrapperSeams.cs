// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;

/// <summary>
/// What the suite puts into every composition of the wrapper alike: the recorder the recording handler
/// and the egress port report to, and the source of the identifier of the played inbound event.
/// </summary>
/// <param name="Recorder">Recorder of the exchanges.</param>
/// <param name="EventIds">Source of the <c>webhook-id</c> of the played event.</param>
internal sealed record WrapperSeams(WireCallRecorder Recorder, ConformanceInboundEventIds EventIds);
