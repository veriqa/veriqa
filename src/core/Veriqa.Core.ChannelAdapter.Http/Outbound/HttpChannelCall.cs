// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization.Metadata;

using Veriqa.Core.ChannelAdapter.Http.Wire;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Outbound;

/// <summary>
/// One outbound call as a member of the channel contract states it to the transport: the operation,
/// its payload, and how the envelopes of the call and of the answer are written and read. Everything
/// else of the call — the tenant, the settings string, the idempotency key, the signature — the
/// transport adds itself.
/// </summary>
/// <typeparam name="TPayload">Payload of the operation (SPEC-042 C34–C38).</typeparam>
/// <typeparam name="TResult">Result of the operation (SPEC-042 C34–C38).</typeparam>
/// <param name="Operation">Wire operation — a value of <see cref="HttpChannelWire.Operations"/>.</param>
/// <param name="TransactionId">
/// Transaction the call is about — a part of the idempotency key of <c>send-prompt</c> and
/// <c>report-outcome</c> (SPEC-042 L43); <c>null</c> for an operation without one.
/// </param>
/// <param name="Payload">Payload of the operation.</param>
/// <param name="EnvelopeType">Serialization of the envelope of the call, from the wire context of the package.</param>
/// <param name="ResponseType">Serialization of the envelope of the answer, from the wire context of the package.</param>
internal sealed record HttpChannelCall<TPayload, TResult>(
    string Operation,
    TransactionId? TransactionId,
    TPayload Payload,
    JsonTypeInfo<WireCallEnvelope<TPayload>> EnvelopeType,
    JsonTypeInfo<WireResponseEnvelope<TResult>> ResponseType)
    where TPayload : class
    where TResult : class;
