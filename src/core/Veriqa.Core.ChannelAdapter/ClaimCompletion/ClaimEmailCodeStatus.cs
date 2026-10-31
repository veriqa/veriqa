// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// Verdict on a code entered for the email completion (SPEC-041 R12, E61).
/// </summary>
internal enum ClaimEmailCodeStatus
{
    /// <summary>
    /// The code matches the live one, which is spent by the match.
    /// </summary>
    Matched,

    /// <summary>
    /// The code does not match; attempts remain.
    /// </summary>
    Mismatch,

    /// <summary>
    /// The live code has no attempts left.
    /// </summary>
    Exhausted,

    /// <summary>
    /// The live code has expired.
    /// </summary>
    Expired,

    /// <summary>
    /// No code was sent in the transaction.
    /// </summary>
    NoCode
}
