// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Veriqa.Core.ChannelAdapter.Email.Constants;

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// The secrets of the email completion and the form they are kept in: a code drawn from the
/// cryptographic generator (SPEC-041 R12), and SHA-256 fingerprints of a code, of a link token and of
/// the scope of a mail counter. The store holds only the fingerprints, so neither a code, nor a link, nor
/// an address or a channel account in clear text is ever at rest in it.
/// </summary>
internal static class EmailClaimSecrets
{
    /// <summary>
    /// Separator between the parts of a fingerprinted value.
    /// </summary>
    private const char Separator = ':';

    /// <summary>
    /// Numeric format that pads a code with leading zeros to its full length.
    /// </summary>
    private static readonly string CodeFormat =
        "D" + EmailAdapterConstants.ClaimCodeLength.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Draws a new code: <see cref="EmailAdapterConstants.ClaimCodeLength"/> decimal digits, uniformly
    /// distributed, from the cryptographic generator.
    /// </summary>
    /// <returns>The code.</returns>
    public static string GenerateCode() =>
        RandomNumberGenerator
            .GetInt32(EmailAdapterConstants.ClaimCodeExclusiveUpperBound)
            .ToString(CodeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// The fingerprint a code is kept as. The transaction takes part in it, so equal codes of two
    /// transactions are not equal fingerprints.
    /// </summary>
    /// <param name="transactionId">Transaction the code was issued for.</param>
    /// <param name="code">The code.</param>
    /// <returns>Hex SHA-256 fingerprint.</returns>
    public static string HashOfCode(string transactionId, string code) =>
        Convert.ToHexString(CodeDigest(transactionId, code));

    /// <summary>
    /// Whether an entered code matches the fingerprint of the live one — compared in constant time.
    /// </summary>
    /// <param name="storedHash">Fingerprint of the live code (<see cref="HashOfCode"/>).</param>
    /// <param name="transactionId">Transaction the code was issued for.</param>
    /// <param name="enteredCode">Code as entered.</param>
    /// <returns><c>true</c> when the codes are equal.</returns>
    public static bool CodeMatches(string storedHash, string transactionId, string enteredCode) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(storedHash),
            CodeDigest(transactionId, enteredCode));

    /// <summary>
    /// The fingerprint a link token is kept and looked up by.
    /// </summary>
    /// <param name="token">Token of the link.</param>
    /// <returns>Hex SHA-256 fingerprint.</returns>
    public static string HashOfToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// The key of a mail counter: its kind in clear text, its scope — an address, a domain, a channel
    /// account — as a fingerprint only.
    /// </summary>
    /// <param name="kind">Kind of the counter.</param>
    /// <param name="scope">What the counter counts mails of.</param>
    /// <returns>The key.</returns>
    public static string CounterKey(string kind, string scope) =>
        string.Concat(kind, Separator.ToString(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))));

    /// <summary>
    /// SHA-256 digest of a code bound to its transaction.
    /// </summary>
    /// <param name="transactionId">Transaction the code was issued for.</param>
    /// <param name="code">The code.</param>
    /// <returns>The digest.</returns>
    private static byte[] CodeDigest(string transactionId, string code) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(transactionId, Separator.ToString(), code)));
}
