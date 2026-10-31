// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Constants;

/// <summary>
/// Timing of the phone step of claim completion (SPEC-041 C74, L73).
/// </summary>
internal static class ClaimCompletionPhoneStepConstants
{
    /// <summary>
    /// How long the core waits for an answer to the request of an <c>Optional</c> phone number before it
    /// counts the step skipped, when no level states otherwise (SPEC-041 C74).
    /// </summary>
    public static readonly TimeSpan DefaultOptionalWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Time left to the deadline of the transaction the window never reaches into: the skip it ends
    /// with and the finalization that follows have to be done before the transaction expires
    /// (SPEC-041 C74 — the window is always shorter than what remains of the TTL).
    /// </summary>
    public static readonly TimeSpan FinalizationReserve = TimeSpan.FromSeconds(5);
}
