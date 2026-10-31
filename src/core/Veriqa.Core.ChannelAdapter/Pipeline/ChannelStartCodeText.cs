// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The one reading of what a user sent as the short code of a sign-in: which characters a code may be
/// written with and how many digits it may have. The pipeline brings a received code to its stored form
/// through it, and a channel of the delivery that has to tell a code from any other typed message asks
/// the same reading — so the two cannot part on what a code looks like.
/// </summary>
/// <remarks>
/// The bounds are the core's (<see cref="ChannelCodeStartOptions"/>): a channel states no length of its
/// own, it only asks whether a text can be a code of any deployment. Nothing here writes the text to a
/// log — a code is a secret of its transaction.
/// </remarks>
internal static class ChannelStartCodeText
{
    /// <summary>
    /// Character users put between the groups of digits of a code besides whitespace.
    /// </summary>
    private const char GroupSeparator = '-';

    /// <summary>
    /// Brings what a user sent as a code to the form codes are stored in: whitespace and the group
    /// separator are dropped, and what remains must be decimal digits — no fewer than the shortest and
    /// no more than the longest code a deployment may state.
    /// </summary>
    /// <param name="input">The text as the user sent it.</param>
    /// <param name="code">The digits of the code; null when the text is not a code of any deployment.</param>
    /// <returns>true — the text can be a code of some deployment.</returns>
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? code)
    {
        code = null;

        if (input is null)
        {
            return false;
        }

        Span<char> digits = stackalloc char[ChannelCodeStartOptions.MaxCodeLength];
        var length = 0;

        foreach (var character in input)
        {
            if (char.IsWhiteSpace(character) || character == GroupSeparator)
            {
                continue;
            }

            if (!char.IsAsciiDigit(character) || length == digits.Length)
            {
                return false;
            }

            digits[length++] = character;
        }

        if (length < ChannelCodeStartOptions.MinCodeLength)
        {
            return false;
        }

        code = new string(digits[..length]);
        return true;
    }

    /// <summary>
    /// Tells whether a typed message can be the code of a sign-in in some deployment, without keeping
    /// its digits — the question of a channel that hands such a message to the core as a code and any
    /// other one as a message that names no transaction.
    /// </summary>
    /// <param name="input">The text as the user sent it.</param>
    /// <returns>true — the text can be a code of some deployment.</returns>
    public static bool CanBeCode(string? input) => TryNormalize(input, out _);
}
