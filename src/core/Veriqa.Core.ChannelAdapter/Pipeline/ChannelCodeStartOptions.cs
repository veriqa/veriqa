// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Settings of the short code a sign-in is started by in a channel that declared
/// <c>ChannelCapabilities.CodeStart</c>. Configuration section: Veriqa:CodeStart.
/// </summary>
/// <remarks>
/// The code belongs to the core, so its length is stated here and nowhere in a channel. Its lifetime
/// is the lifetime of the transaction — the section has no setting for it. The attempts to guess it
/// are bounded twice: by the existing limit of sign-in starts per channel user, and — for a start that
/// arrives with a tenant of the event, whose source names any sender it likes — by the budget of
/// unmatched codes of the pair "tenant of the event + channel type" stated here.
/// </remarks>
public sealed class ChannelCodeStartOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:CodeStart";

    /// <summary>
    /// Shortest code a deployment may state, in digits.
    /// </summary>
    public const int MinCodeLength = 6;

    /// <summary>
    /// Longest code a deployment may state, in digits. A value a user sent that is longer than this
    /// names no code of any deployment.
    /// </summary>
    public const int MaxCodeLength = 10;

    /// <summary>
    /// Code length of a deployment that states none.
    /// </summary>
    public const int DefaultCodeLength = MinCodeLength;

    /// <summary>
    /// Number of decimal digits of a code, from <see cref="MinCodeLength"/> to
    /// <see cref="MaxCodeLength"/>. A value outside the range stops the host at start.
    /// </summary>
    public int CodeLength { get; set; } = DefaultCodeLength;

    /// <summary>
    /// How many codes that took no record — unknown, expired, already taken or of another tenant — the
    /// pair "tenant of the event + channel type" may present within one window of
    /// <see cref="UnmatchedCodeWindowSeconds"/>. Once they are spent, every start by a code of the pair is
    /// left unanswered until the window ends, a live code included, and the store is not asked. A matched
    /// code spends nothing. A start with no tenant of the event has no such budget. A value of zero or
    /// less stops the host at start.
    /// </summary>
    public int UnmatchedCodePermitLimit { get; set; } = 10;

    /// <summary>
    /// Length of the window of <see cref="UnmatchedCodePermitLimit"/>, in seconds. A value of zero or
    /// less stops the host at start.
    /// </summary>
    public int UnmatchedCodeWindowSeconds { get; set; } = 60;
}
