// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// The link handle — the one place that knows its shape. A handle travels only to its holder, in the
/// address of a post-completion button; <see cref="ILinkHandleStore"/> keeps its fingerprint
/// (<see cref="LinkHandleRecord.HandleHash"/>).
/// </summary>
/// <remarks>
/// A handle is <see cref="Length"/> characters of <c>[0-9A-Za-z]</c> drawn from the cryptographic random
/// generator — about 131 bits, and safe in a deep-link parameter of any platform without escaping. The
/// fingerprint is the SHA-256 of the UTF-8 bytes of the handle, as lowercase hexadecimal text. Neither
/// the handle nor its fingerprint is ever written to a log or put into an exception message.
/// </remarks>
internal static class LinkHandle
{
    /// <summary>
    /// Number of characters of a handle.
    /// </summary>
    public const int Length = 22;

    /// <summary>
    /// Characters a handle is drawn from.
    /// </summary>
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// Issues a new handle.
    /// </summary>
    /// <returns>The handle, for its holder only, and its fingerprint, for the store.</returns>
    public static (string Handle, string Hash) Issue()
    {
        var handle = RandomNumberGenerator.GetString(Alphabet, Length);

        return (handle, HashOf(handle));
    }

    /// <summary>
    /// Takes the fingerprint of a handle — an issued one or a value presented as one. Any non-empty
    /// text has a fingerprint: a value that is not a handle simply finds no record.
    /// </summary>
    /// <param name="handle">The handle.</param>
    /// <returns>Lowercase hexadecimal SHA-256 of the UTF-8 bytes of the handle.</returns>
    public static string HashOf(string handle)
    {
        ArgumentException.ThrowIfNullOrEmpty(handle);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(handle)));
    }
}
