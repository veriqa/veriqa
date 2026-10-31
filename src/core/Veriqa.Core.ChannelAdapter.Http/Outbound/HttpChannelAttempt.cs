// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Outbound;

/// <summary>
/// The end of one attempt of an outbound call, as the retry strategy and then the classification of
/// the answer read it (SPEC-042 L44). It holds no header and no body of the request: nothing here is
/// a secret, and <see cref="Reason"/> is the text the log receives.
/// </summary>
/// <param name="Kind">How the attempt ended.</param>
/// <param name="Reason">Description of the end for the log — the status, the failure; never a body.</param>
internal sealed record HttpChannelAttempt(HttpChannelAttemptKind Kind, string Reason)
{
    /// <summary>
    /// Pause the adapter asked for in <c>Retry-After</c> of a 429; <c>null</c> — none, the retry
    /// schedule applies.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// Body of an answered attempt (<see cref="HttpChannelAttemptKind.Answered"/>); empty otherwise.
    /// </summary>
    public ReadOnlyMemory<byte> Body { get; init; }
}
