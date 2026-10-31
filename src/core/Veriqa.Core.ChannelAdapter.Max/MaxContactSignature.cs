// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

using Veriqa.Core.ChannelAdapter.Max.Constants;

namespace Veriqa.Core.ChannelAdapter.Max;

/// <summary>
/// The proof that a contact shared in MAX is the sender's own (SPEC-041 L45): a contact sent with the
/// <c>request_contact</c> button carries <c>hash</c> — HMAC-SHA256 of its <c>vcf_info</c> keyed with the
/// bot token (MAX Bot API, keyboard use case). A contact without it, or with one that does not match,
/// is not proven.
/// </summary>
internal static class MaxContactSignature
{
    /// <summary>
    /// Base64 groups four characters; a value without its padding is completed to that length.
    /// </summary>
    private const int Base64QuantumLength = 4;

    /// <summary>
    /// Line breaks a vCard is split by: the CRLF of the format and a bare LF of a lenient writer.
    /// </summary>
    private static readonly string[] VCardLineBreaks =
        [MaxAdapterConstants.VCardLineBreak, MaxAdapterConstants.VCardBareLineBreak];

    /// <summary>
    /// Whether the signature proves the vCard: HMAC-SHA256 of the vCard keyed with the bot token, compared
    /// in constant time. The vendor does not name the encoding of the signature, so both a hex and a
    /// base64 (standard or URL-safe, padded or not) value are read; a vCard whose line breaks arrived
    /// escaped once more than JSON unescapes is also tried with them restored, as the vendor asks.
    /// </summary>
    /// <param name="vcfInfo">The vCard of the contact.</param>
    /// <param name="hash">Signature from the contact attachment; null — none arrived.</param>
    /// <param name="botToken">Token of the bot of the tenant that received the update.</param>
    /// <returns>true — the contact is proven to be the sender's own.</returns>
    public static bool Verify(string vcfInfo, string? hash, string botToken)
    {
        if (string.IsNullOrEmpty(vcfInfo)
            || string.IsNullOrEmpty(hash)
            || string.IsNullOrEmpty(botToken)
            || !TryDecodeHash(hash, out var expected))
        {
            return false;
        }

        var key = Encoding.UTF8.GetBytes(botToken);
        if (Matches(key, vcfInfo, expected))
        {
            return true;
        }

        return vcfInfo.Contains(MaxAdapterConstants.EscapedVCardLineBreak, StringComparison.Ordinal)
            && Matches(key, RestoreLineBreaks(vcfInfo), expected);
    }

    /// <summary>
    /// The phone number of a vCard — the value of its first <c>TEL</c> property, as written there.
    /// </summary>
    /// <param name="vcfInfo">The vCard of the contact.</param>
    /// <returns>The number; null — the vCard names none.</returns>
    public static string? ReadPhone(string? vcfInfo)
    {
        if (string.IsNullOrEmpty(vcfInfo))
        {
            return null;
        }

        var lines = RestoreLineBreaks(vcfInfo).Split(
            VCardLineBreaks,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            // A content line is "[group.]NAME[;params]:value" (RFC 6350 §3.3).
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = line.AsSpan(0, colon);
            var parameters = name.IndexOf(';');
            if (parameters >= 0)
            {
                name = name[..parameters];
            }

            var group = name.LastIndexOf('.');
            if (group >= 0)
            {
                name = name[(group + 1)..];
            }

            if (!name.Equals(MaxAdapterConstants.VCardPhoneProperty, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();
            if (value.StartsWith(MaxAdapterConstants.VCardPhoneUriScheme, StringComparison.OrdinalIgnoreCase))
            {
                value = value[MaxAdapterConstants.VCardPhoneUriScheme.Length..];
            }

            return value.Length is 0 ? null : value;
        }

        return null;
    }

    /// <summary>
    /// Whether the HMAC-SHA256 of the vCard under the key equals the expected signature.
    /// </summary>
    /// <param name="key">Bot token bytes.</param>
    /// <param name="vcfInfo">The vCard as hashed.</param>
    /// <param name="expected">Decoded signature.</param>
    /// <returns>true — the signature matches.</returns>
    private static bool Matches(byte[] key, string vcfInfo, byte[] expected)
    {
        Span<byte> computed = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(vcfInfo), computed);

        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }

    /// <summary>
    /// The vCard with line breaks that arrived escaped once more restored to real ones.
    /// </summary>
    /// <param name="vcfInfo">The vCard.</param>
    /// <returns>The vCard with real line breaks.</returns>
    private static string RestoreLineBreaks(string vcfInfo) =>
        vcfInfo.Replace(
            MaxAdapterConstants.EscapedVCardLineBreak,
            MaxAdapterConstants.VCardLineBreak,
            StringComparison.Ordinal);

    /// <summary>
    /// Decodes a signature written in hex or in base64 (standard or URL-safe, padded or not).
    /// </summary>
    /// <param name="hash">Signature as it arrived.</param>
    /// <param name="bytes">The HMAC-SHA256 value on success.</param>
    /// <returns>true — the value decodes to exactly one HMAC-SHA256.</returns>
    private static bool TryDecodeHash(string hash, [NotNullWhen(true)] out byte[]? bytes)
    {
        var decoded = new byte[HMACSHA256.HashSizeInBytes];

        if (hash.Length == HMACSHA256.HashSizeInBytes * 2
            && Convert.FromHexString(hash, decoded, out _, out var hexBytes) is OperationStatus.Done
            && hexBytes == HMACSHA256.HashSizeInBytes)
        {
            bytes = decoded;
            return true;
        }

        var base64 = hash.Replace('-', '+').Replace('_', '/');
        var padding = (Base64QuantumLength - (base64.Length % Base64QuantumLength)) % Base64QuantumLength;
        base64 = base64.PadRight(base64.Length + padding, '=');

        var buffer = new byte[base64.Length];
        if (Convert.TryFromBase64String(base64, buffer, out var written)
            && written == HMACSHA256.HashSizeInBytes)
        {
            bytes = buffer[..written];
            return true;
        }

        bytes = null;
        return false;
    }
}
