// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Veriqa.Core.AuthServer.ClaimCompletion;

/// <summary>
/// The form token of the claims completion (SPEC-041 N78): the right to act on the form of the web step on
/// the page of the device that confirmed the sign-in. The token travels only to its holder — in the link of
/// the channel message or in the page that was opened with it — and the transaction keeps its fingerprint
/// (<c>ClaimCompletionState.FormTokenHash</c>), so neither the store nor its backups can hand the right out.
/// </summary>
/// <remarks>
/// The token is 32 random bytes as Base64Url text; the fingerprint is the SHA-256 of the UTF-8 bytes of that
/// text, as Base64Url text. A presented token is proven by comparing the bytes of the two fingerprints in
/// constant time. Neither the token nor its fingerprint is ever written to a log.
/// </remarks>
internal static class ClaimFormToken
{
    /// <summary>
    /// Number of random bytes of a token.
    /// </summary>
    private const int TokenByteLength = 32;

    /// <summary>
    /// Issues a new token.
    /// </summary>
    /// <returns>The token, for its holder only, and its fingerprint, for the transaction.</returns>
    public static (string Token, string Hash) Issue()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));

        return (token, Base64Url.EncodeToString(Fingerprint(token)));
    }

    /// <summary>
    /// Whether a presented token is the one a fingerprint was taken from.
    /// </summary>
    /// <param name="token">Presented token; null or empty — none.</param>
    /// <param name="hash">Stored fingerprint; null — no token was issued.</param>
    /// <returns>true only when both are there and the fingerprint of the token equals the stored one.</returns>
    public static bool Matches(string? token, string? hash)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(hash) || !Base64Url.IsValid(hash))
        {
            return false;
        }

        // FixedTimeEquals answers false on a length mismatch without comparing, and compares equal lengths
        // in time independent of where the bytes differ.
        return CryptographicOperations.FixedTimeEquals(Fingerprint(token), Base64Url.DecodeFromChars(hash));
    }

    /// <summary>
    /// Takes the fingerprint of a token.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <returns>SHA-256 of the UTF-8 bytes of the token.</returns>
    private static byte[] Fingerprint(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
