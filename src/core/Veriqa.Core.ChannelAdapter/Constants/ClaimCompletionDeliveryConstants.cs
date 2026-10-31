// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Constants;

/// <summary>
/// Retry policy of the messages claim completion delivers into the user's chat — the row "message
/// send error" of SPEC-003 §12.2: up to two retries, 500 ms and then 1000 ms apart.
/// </summary>
internal static class ClaimCompletionDeliveryConstants
{
    /// <summary>
    /// Pause before each retry, in order; their number is the number of retries after the first
    /// attempt.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
        new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000) }.AsReadOnly();
}
