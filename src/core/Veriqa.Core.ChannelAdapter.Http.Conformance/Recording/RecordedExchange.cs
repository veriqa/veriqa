// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;

/// <summary>
/// One exchange of the wrapper with the external adapter, as the recording handler saw it: the body
/// the wrapper sent and what came back to the wrapper. The bodies are kept in memory for the checks
/// of the run and are never printed (SPEC-042 N58).
/// </summary>
internal sealed record RecordedExchange
{
    /// <summary>
    /// Body of the request — the envelope of the call, exactly as the wrapper serialized it.
    /// </summary>
    public required ReadOnlyMemory<byte> RequestBody { get; init; }

    /// <summary>
    /// Status of the answer; <c>null</c> — no answer came (<see cref="TransportFailure"/>).
    /// </summary>
    public int? StatusCode { get; init; }

    /// <summary>
    /// Body of the answer, read up to the limit of the channel contour; <c>null</c> — no answer, or a
    /// body over the limit (<see cref="ResponseExceedsLimit"/>).
    /// </summary>
    public ReadOnlyMemory<byte>? ResponseBody { get; init; }

    /// <summary>
    /// The body of the answer exceeds the limit of the channel contour and was not read in full.
    /// </summary>
    public bool ResponseExceedsLimit { get; init; }

    /// <summary>
    /// Why no answer came — the exception of the exchange, by type and message; <c>null</c> — an
    /// answer came.
    /// </summary>
    public string? TransportFailure { get; init; }
}
