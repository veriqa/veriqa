// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;

using Veriqa.Core.ChannelAdapter.Email.Constants;

namespace Veriqa.Core.ChannelAdapter.Email.Services;

/// <summary>
/// Single source of the Push-mode correlation token generator (SPEC-016 §5): a high-entropy,
/// single-use, URL-safe token of <see cref="EmailAdapterConstants.TokenByteLength"/> random bytes.
/// Two forms of the same entropy: Base64Url for a token carried in the text of a mail or in a link,
/// lower-case Base32 for a token that is the recipient local part, whose case is not preserved on the
/// way (SPEC-016 §5.4).
/// </summary>
internal static class EmailPushTokenGenerator
{
    /// <summary>
    /// Alphabet of the local-part token: the RFC 4648 Base32 alphabet in lower case. None of its
    /// characters changes under case folding, so an address folded to lower case keeps the token intact.
    /// </summary>
    private const string LocalPartTokenAlphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>
    /// Bits one character of <see cref="LocalPartTokenAlphabet"/> encodes.
    /// </summary>
    private const int LocalPartBitsPerChar = 5;

    /// <summary>
    /// Bits in one byte of the random source.
    /// </summary>
    private const int BitsPerByte = 8;

    /// <summary>
    /// Generates a high-entropy token for the recipient local part: lower-case Base32
    /// without padding of <see cref="EmailAdapterConstants.TokenByteLength"/> random bytes.
    /// </summary>
    /// <returns>The token string.</returns>
    public static string GenerateLocalPartToken()
    {
        // Encodes the bytes five bits per character, most significant bit first (RFC 4648 order); the
        // bits left over after the last byte are left-aligned into one final character.
        var tokenBytes = RandomNumberGenerator.GetBytes(EmailAdapterConstants.TokenByteLength);
        var charMask = (1 << LocalPartBitsPerChar) - 1;
        var builder = new StringBuilder(
            ((tokenBytes.Length * BitsPerByte) + LocalPartBitsPerChar - 1) / LocalPartBitsPerChar);

        var buffer = 0;
        var bitsInBuffer = 0;

        foreach (var tokenByte in tokenBytes)
        {
            buffer = (buffer << BitsPerByte) | tokenByte;
            bitsInBuffer += BitsPerByte;

            while (bitsInBuffer >= LocalPartBitsPerChar)
            {
                bitsInBuffer -= LocalPartBitsPerChar;
                builder.Append(LocalPartTokenAlphabet[(buffer >> bitsInBuffer) & charMask]);
            }

            // Only the bits not yet encoded are kept, so the buffer never outgrows one byte plus a remainder.
            buffer &= (1 << bitsInBuffer) - 1;
        }

        if (bitsInBuffer > 0)
        {
            builder.Append(LocalPartTokenAlphabet[(buffer << (LocalPartBitsPerChar - bitsInBuffer)) & charMask]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Generates a high-entropy URL-safe token (Base64Url without padding) of
    /// <see cref="EmailAdapterConstants.TokenByteLength"/> random bytes.
    /// </summary>
    /// <returns>The token string.</returns>
    public static string GenerateUrlSafeToken()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(EmailAdapterConstants.TokenByteLength);
        return Convert.ToBase64String(tokenBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
