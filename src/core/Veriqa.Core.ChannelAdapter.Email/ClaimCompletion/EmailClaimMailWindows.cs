// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// Windows of the counters of the claims-completion mails that are not the transaction's own
/// (SPEC-041 E61). The limits within them are settings; the windows are not — a window is fixed from the
/// first mail its counter counts. The counter of a transaction has no window of its own: it lives as long
/// as the transaction does.
/// </summary>
internal static class EmailClaimMailWindows
{
    /// <summary>
    /// Window of the counter of one channel account across its transactions.
    /// </summary>
    public static readonly TimeSpan Subject = TimeSpan.FromHours(24);

    /// <summary>
    /// Window of the counter of one recipient address.
    /// </summary>
    public static readonly TimeSpan Address = TimeSpan.FromHours(1);

    /// <summary>
    /// Window of the counter of one recipient domain.
    /// </summary>
    public static readonly TimeSpan Domain = TimeSpan.FromHours(1);
}
