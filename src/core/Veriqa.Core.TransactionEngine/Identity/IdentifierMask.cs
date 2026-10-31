// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Identity;

/// <summary>
/// The one masking of a user identifier the core writes where the identifier itself must not go — the
/// attribution of an event leaving the process, an audit record, a log line: at most the last two characters
/// stay readable.
/// </summary>
internal static class IdentifierMask
{
    /// <summary>
    /// Number of trailing characters of an identifier left unmasked.
    /// </summary>
    private const int UnmaskedTailLength = 2;

    /// <summary>
    /// Character the masked part of an identifier is replaced with.
    /// </summary>
    private const char MaskCharacter = '*';

    /// <summary>
    /// Masks an identifier, leaving at most the last two characters readable. A value that short
    /// carries no distinguishing tail, so it is masked whole.
    /// </summary>
    /// <param name="value">Identifier to mask.</param>
    /// <returns>Masked identifier of the same length.</returns>
    public static string Mask(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length <= UnmaskedTailLength)
        {
            return new string(MaskCharacter, value.Length);
        }

        var masked = new string(MaskCharacter, value.Length - UnmaskedTailLength);

        return string.Concat(masked, value.AsSpan(value.Length - UnmaskedTailLength));
    }
}
