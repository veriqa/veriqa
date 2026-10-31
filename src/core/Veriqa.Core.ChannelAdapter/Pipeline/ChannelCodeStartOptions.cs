// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Settings of the short code a sign-in is started by in a channel that declared
/// <c>ChannelCapabilities.CodeStart</c>. Configuration section: Veriqa:CodeStart.
/// </summary>
/// <remarks>
/// The code belongs to the core, so its length is stated here and nowhere in a channel. Its lifetime
/// is the lifetime of the transaction, and the attempts to guess it are bounded by the existing limit
/// of sign-in starts per channel user — the section has no setting for either.
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
}
