// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Veriqa.Sample.PseudoChannels.Adapter;

/// <summary>
/// Signature of the relay webhook: HMAC-SHA256 over the raw body bytes, keyed by the relay secret.
/// One place computes it for both sides — the host signs with <see cref="Compute"/>, the adapter
/// checks with <see cref="IsValid"/>.
/// </summary>
public static class PseudoChannelSignature
{
    /// <summary>
    /// Length of the hex form of an HMAC-SHA256.
    /// </summary>
    private const int HexLength = HMACSHA256.HashSizeInBytes * 2;

    /// <summary>
    /// Computes the value of the <see cref="PseudoChannelConstants.SignatureHeader"/> header.
    /// </summary>
    /// <param name="relaySecret">Relay secret; must not be empty.</param>
    /// <param name="body">Raw body bytes, exactly as they are sent.</param>
    /// <returns>Header value: the prefix followed by the lowercase hex of the HMAC.</returns>
    public static string Compute(string relaySecret, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(relaySecret);

        Span<byte> hash = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(relaySecret), body, hash);

        return PseudoChannelConstants.SignaturePrefix + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Checks a signature header value against the body. The comparison is fixed-time, and every
    /// malformed input — no header, no secret, a wrong prefix, a non-hex tail — answers "invalid".
    /// </summary>
    /// <param name="relaySecret">Relay secret; an empty one validates nothing.</param>
    /// <param name="body">Raw body bytes, exactly as they arrived.</param>
    /// <param name="headerValue">Value of the signature header, or null when it is absent.</param>
    /// <returns>Whether the signature matches the body.</returns>
    public static bool IsValid(string relaySecret, ReadOnlySpan<byte> body, string? headerValue)
    {
        if (string.IsNullOrEmpty(relaySecret)
            || headerValue is null
            || !headerValue.StartsWith(PseudoChannelConstants.SignaturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var hex = headerValue.AsSpan(PseudoChannelConstants.SignaturePrefix.Length);
        if (hex.Length != HexLength)
        {
            return false;
        }

        Span<byte> presented = stackalloc byte[HMACSHA256.HashSizeInBytes];
        if (Convert.FromHexString(hex, presented, out _, out var written) != OperationStatus.Done
            || written != presented.Length)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(relaySecret), body, expected);

        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
