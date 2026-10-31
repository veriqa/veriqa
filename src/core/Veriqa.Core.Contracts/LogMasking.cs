// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace Veriqa.Core.Contracts;

/// <summary>
/// Masking of identifiers written to logs. A channel adapter must not put a raw
/// channel_user_id, phone number, e-mail address or display name into a log record above
/// the Debug level (SPEC-003 §18.3, CA-121); this helper is what it writes instead.
/// </summary>
/// <remarks>
/// The fingerprint is deliberately unsalted. Its purpose is to keep the raw value out of the log
/// while records of the same user still meet, not to make the value unrecoverable for whoever
/// holds the log: a truncated SHA-256 of a short phone number is guessable by brute force. A
/// secret salt would break the meeting it exists for — it differs per replica, so the same user
/// would get a different fingerprint on every node. Whoever needs unrecoverability turns the log
/// level off instead.
/// </remarks>
public static class LogMasking
{
    /// <summary>
    /// Number of leading SHA-256 bytes the fingerprint is built from — 8 bytes, hex-encoded into
    /// the 16 characters the log carries.
    /// </summary>
    private const int FingerprintByteLength = 8;

    /// <summary>
    /// Computes a stable, unsalted fingerprint of an identifier for logging:
    /// the first 8 bytes of its SHA-256, hex-encoded (16 characters).
    /// Records of the same identifier share a fingerprint, so an operator can still
    /// stitch together the steps of a single sign-in.
    /// </summary>
    /// <remarks>
    /// The value is hashed exactly as it is given: the helper does not trim it and does not change
    /// its case. Normalization belongs to the caller, which knows what its identifier is — two
    /// callers normalizing differently would otherwise give one value two fingerprints.
    /// </remarks>
    /// <param name="value">Identifier to mask. Never written to the log itself.</param>
    /// <returns>16 hex characters; an empty string for a null or empty input.</returns>
    public static string Fingerprint(string? value)
    {
        // An absent value is not an error here: a log call must not fail because the identifier
        // it was going to mask turned out to be missing.
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hashBytes.AsSpan(0, FingerprintByteLength));
    }
}
