// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Outbound;

/// <summary>
/// How one attempt of an outbound call ended (SPEC-042 L44).
/// </summary>
internal enum HttpChannelAttemptKind
{
    /// <summary>
    /// The egress policy refused the attempt: no connection was opened (SPEC-042 L48).
    /// </summary>
    EgressRefused,

    /// <summary>
    /// A transport failure — no connection, the attempt timed out, a 5xx or a 429 status. The only
    /// kind a retried operation is retried on.
    /// </summary>
    TransportFailure,

    /// <summary>
    /// A status the contract does not answer with — a redirect, any other 4xx, a success other than
    /// 200: an answer outside the contract, never retried.
    /// </summary>
    UnexpectedStatus,

    /// <summary>
    /// A 200 whose body exceeds the limit: an answer outside the contract, never retried, the rest of
    /// the body unread (SPEC-042 E54).
    /// </summary>
    BodyTooLarge,

    /// <summary>
    /// A 200 with a body within the limit, to be read as the envelope of the answer.
    /// </summary>
    Answered
}
