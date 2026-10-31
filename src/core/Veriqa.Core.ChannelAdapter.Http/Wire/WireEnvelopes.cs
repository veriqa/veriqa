// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Wire;

/// <summary>
/// Envelope of a call of the core to the external adapter (SPEC-042 C32): the body of the
/// <c>POST</c> to the base URL of the registration.
/// </summary>
/// <typeparam name="TPayload">Payload of the operation (SPEC-042 C34–C38).</typeparam>
internal sealed class WireCallEnvelope<TPayload>
    where TPayload : class
{
    /// <summary>
    /// Wire version of the sender (SPEC-042 C40).
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Operation of the call — a value of <see cref="HttpChannelWire.Operations"/>.
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Channel type of the registration: one endpoint of an adapter may serve several registrations.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Tenant of the call; <c>null</c> — the implicit tenant (SPEC-042 R13, L47).
    /// </summary>
    public required string? Tenant { get; init; }

    /// <summary>
    /// Opaque settings string resolved for the tenant of the call; <c>null</c> — none (SPEC-042 R12).
    /// A secret: never logged.
    /// </summary>
    public required string? AdapterSettings { get; init; }

    /// <summary>
    /// Data of the operation.
    /// </summary>
    public required TPayload Payload { get; init; }
}

/// <summary>
/// Envelope of the answer of the external adapter to a call (SPEC-042 C32): the version and exactly
/// one of <see cref="Result"/> and <see cref="Error"/>. That exactly one is present is a rule of the
/// classification of the answer, not of its parsing: an envelope with both or with neither parses and
/// is then refused as an answer outside the contract (SPEC-042 L44).
/// </summary>
/// <typeparam name="TResult">Result of the operation (SPEC-042 C34–C38).</typeparam>
internal sealed class WireResponseEnvelope<TResult>
    where TResult : class
{
    /// <summary>
    /// Wire version of the adapter (SPEC-042 C40).
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Data of a successful operation; <c>null</c> — absent.
    /// </summary>
    public TResult? Result { get; init; }

    /// <summary>
    /// Refusal of the operation; <c>null</c> — absent.
    /// </summary>
    public WireError? Error { get; init; }
}

/// <summary>
/// Refusal of an operation by the external adapter (SPEC-042 C32).
/// </summary>
internal sealed class WireError
{
    /// <summary>
    /// Error code — a value of <see cref="HttpChannelWire.ErrorCodes"/> or a code of the channel
    /// contract; any other code is read as the channel being unavailable (SPEC-042 L45).
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Description of the refusal for the log of the core.
    /// </summary>
    public required string Message { get; init; }
}
