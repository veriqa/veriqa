// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Veriqa.Core.ChannelAdapter.Http.Outbound;

/// <summary>
/// Idempotency keys of the outbound calls — the <c>webhook-id</c> of a call (SPEC-042 L43, C31). For
/// the adapter a key is opaque and only compared for equality.
/// </summary>
internal static class HttpChannelIdempotencyKey
{
    /// <summary>
    /// Length prefix that marks an absent part: no UTF-8 string has a negative length, so an absent
    /// part never encodes like an empty one.
    /// </summary>
    private const int AbsentPartLength = -1;

    /// <summary>
    /// Format of a random key: the 32 lower-case hex digits of a random GUID, the same alphabet a
    /// derived key is written in.
    /// </summary>
    private const string RandomKeyFormat = "N";

    /// <summary>
    /// Derives a key from its parts: SHA-256 over the parts in order, each prefixed with its length,
    /// written as lower-case hex. The prefix, not a separator, keeps the parts apart — joined with a
    /// separator, a tenant <c>a:b</c> followed by <c>c</c> and a tenant <c>a</c> followed by <c>b:c</c>
    /// would give one key, and the adapter would answer one transaction with the message of another.
    /// </summary>
    /// <param name="parts">Parts of the key in their fixed order; <c>null</c> — an absent part.</param>
    /// <returns>The key.</returns>
    public static string Derive(params ReadOnlySpan<string?> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];

        foreach (var part in parts)
        {
            if (part is null)
            {
                BinaryPrimitives.WriteInt32BigEndian(length, AbsentPartLength);
                hash.AppendData(length);
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(part);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// A key of a call that has no parts to derive it from — a fresh random value, the same for every
    /// attempt of that one call.
    /// </summary>
    /// <returns>The key.</returns>
    public static string NewRandom() => Guid.NewGuid().ToString(RandomKeyFormat);
}
