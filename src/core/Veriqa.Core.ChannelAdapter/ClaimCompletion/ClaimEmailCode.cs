// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;

namespace Veriqa.Core.ChannelAdapter.ClaimCompletion;

/// <summary>
/// Shape of the code of the email completion (SPEC-041 R12): one statement for the mail side that draws the
/// code and the block of the web step that takes it back from the user, so the two cannot disagree about
/// what a code looks like.
/// </summary>
internal static class ClaimEmailCode
{
    /// <summary>
    /// Number of decimal digits of a code (SPEC-041 R12: not fewer than six).
    /// </summary>
    public const int Length = 6;

    /// <summary>
    /// Takes a code as the user typed it: every white-space character is dropped (a code copied from a mail
    /// often carries spaces), and what remains must be exactly <see cref="Length"/> ASCII digits.
    /// </summary>
    /// <param name="entered">The code as entered; null — nothing was entered.</param>
    /// <param name="code">The code without white space, when it has the shape of a code.</param>
    /// <returns><c>true</c> when the entry has the shape of a code.</returns>
    public static bool TryClean(string? entered, [NotNullWhen(true)] out string? code)
    {
        code = null;

        if (string.IsNullOrEmpty(entered))
        {
            return false;
        }

        var cleaned = string.Concat(entered.Where(character => !char.IsWhiteSpace(character)));

        if (cleaned.Length != Length || !cleaned.All(char.IsAsciiDigit))
        {
            return false;
        }

        code = cleaned;

        return true;
    }
}
