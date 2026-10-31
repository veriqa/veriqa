// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Veriqa.Core.ChannelAdapter.Http.Constants;

namespace Veriqa.Core.ChannelAdapter.Http;

/// <summary>
/// The transport signature of the wire contract, both directions (SPEC-042 C31): the symmetric
/// Standard Webhooks scheme <c>v1</c> — HMAC-SHA256 over the raw bytes <c>id.timestamp.body</c>, keyed
/// by the transport secret of the registration. During a rotation the sender signs with both secrets
/// and the receiver accepts either, so a secret changes without downtime.
/// </summary>
public static class HttpChannelSignature
{
    /// <summary>
    /// Message of the refusal of a secret list that carries no secret.
    /// </summary>
    private const string NoSecretMessage = "At least one transport secret is required.";

    /// <summary>
    /// Message of the refusal of a secret outside the Standard Webhooks serialization. The value
    /// itself is never part of it.
    /// </summary>
    private const string InvalidSecretMessage =
        "A transport secret is not in the Standard Webhooks serialization (whsec_ followed by a base64 key of 24 to 64 bytes).";

    /// <summary>
    /// Signs a message: the value of the <c>webhook-signature</c> header — <c>v1,&lt;base64&gt;</c> per
    /// secret, space-separated, in the order of <paramref name="secrets"/>.
    /// </summary>
    /// <param name="webhookId">Value of the <c>webhook-id</c> header of the message.</param>
    /// <param name="timestamp">Signing moment; the <c>webhook-timestamp</c> header carries it in unix seconds.</param>
    /// <param name="body">Raw bytes of the body exactly as they are sent.</param>
    /// <param name="secrets">One or two secrets in the <c>whsec_</c> serialization, the current one first.</param>
    /// <returns>The value of the <c>webhook-signature</c> header.</returns>
    /// <exception cref="ArgumentException">The identifier is empty, no secret is given, or a secret is not in the <c>whsec_</c> serialization.</exception>
    public static string Sign(
        string webhookId,
        DateTimeOffset timestamp,
        ReadOnlySpan<byte> body,
        IReadOnlyList<string> secrets)
    {
        ArgumentException.ThrowIfNullOrEmpty(webhookId);

        var keys = DecodeSecrets(secrets);
        var unixSeconds = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var content = SignedContent(webhookId, unixSeconds, body);

        var signatures = new string[keys.Length];
        for (var index = 0; index < keys.Length; index++)
        {
            var hash = HMACSHA256.HashData(keys[index], content);
            signatures[index] = HttpChannelConstants.SignatureSchemePrefix + Convert.ToBase64String(hash);
        }

        return string.Join(HttpChannelConstants.SignatureSeparator, signatures);
    }

    /// <summary>
    /// Verifies the signature of a received message.
    /// </summary>
    /// <param name="webhookId">Value of the <c>webhook-id</c> header; <c>null</c> — the header is absent.</param>
    /// <param name="webhookTimestamp">Value of the <c>webhook-timestamp</c> header; <c>null</c> — absent.</param>
    /// <param name="webhookSignature">Value of the <c>webhook-signature</c> header; <c>null</c> — absent.</param>
    /// <param name="body">Raw bytes of the body exactly as they were received.</param>
    /// <param name="secrets">One or two secrets in the <c>whsec_</c> serialization, the current one first.</param>
    /// <param name="now">Moment of the receipt, against which the signing moment is checked.</param>
    /// <returns>
    /// <c>false</c> when any header is missing or empty, the timestamp is not an integer or lies more
    /// than five minutes away from <paramref name="now"/>, or no <c>v1</c> signature of the header
    /// matches any of the secrets; otherwise <c>true</c>. A moment exactly five minutes away is accepted.
    /// </returns>
    /// <exception cref="ArgumentException">No secret is given, or a secret is not in the <c>whsec_</c> serialization.</exception>
    public static bool Verify(
        string? webhookId,
        string? webhookTimestamp,
        string? webhookSignature,
        ReadOnlySpan<byte> body,
        IReadOnlyList<string> secrets,
        DateTimeOffset now)
    {
        // Order of the checks: the secrets are arguments of the caller and are refused before
        // anything of the message is looked at; then the headers, the window of the timestamp, and
        // only then the hashes — a stale or unsigned message costs no HMAC.
        var keys = DecodeSecrets(secrets);

        if (string.IsNullOrEmpty(webhookId)
            || string.IsNullOrEmpty(webhookTimestamp)
            || string.IsNullOrEmpty(webhookSignature))
        {
            return false;
        }

        if (!long.TryParse(webhookTimestamp, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var signedAt))
        {
            return false;
        }

        // Both bounds are computed from the receipt moment, so an extreme timestamp cannot overflow
        // the comparison the way a difference of the two values could.
        var receivedAt = now.ToUnixTimeSeconds();
        if (signedAt < receivedAt - HttpChannelConstants.SignatureToleranceSeconds
            || signedAt > receivedAt + HttpChannelConstants.SignatureToleranceSeconds)
        {
            return false;
        }

        // The signed content carries the timestamp exactly as the header spelled it.
        var content = SignedContent(webhookId, webhookTimestamp, body);
        var expected = new byte[keys.Length][];
        for (var index = 0; index < keys.Length; index++)
        {
            expected[index] = HMACSHA256.HashData(keys[index], content);
        }

        // Every candidate of the header is compared with every expected hash in constant time and
        // without an early exit, so the position of a match does not show in the timing.
        Span<byte> candidate = stackalloc byte[HMACSHA256.HashSizeInBytes];
        var matched = false;
        foreach (var signature in webhookSignature.Split(HttpChannelConstants.SignatureSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!signature.StartsWith(HttpChannelConstants.SignatureSchemePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var encoded = signature.AsSpan(HttpChannelConstants.SignatureSchemePrefix.Length);
            if (!Convert.TryFromBase64Chars(encoded, candidate, out var written)
                || written != HMACSHA256.HashSizeInBytes)
            {
                continue;
            }

            foreach (var hash in expected)
            {
                matched |= CryptographicOperations.FixedTimeEquals(candidate, hash);
            }
        }

        return matched;
    }

    /// <summary>
    /// Decodes the secrets of a signature operation, refusing an empty list and any value outside the
    /// <c>whsec_</c> serialization.
    /// </summary>
    /// <param name="secrets">Secrets in the <c>whsec_</c> serialization.</param>
    /// <returns>The decoded keys, in the order of the secrets.</returns>
    /// <exception cref="ArgumentException">No secret is given, or a secret is not a valid serialization.</exception>
    private static byte[][] DecodeSecrets(IReadOnlyList<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        if (secrets.Count is 0)
        {
            throw new ArgumentException(NoSecretMessage, nameof(secrets));
        }

        var keys = new byte[secrets.Count][];
        for (var index = 0; index < secrets.Count; index++)
        {
            var secret = secrets[index];
            if (secret is null || !HttpChannelTransportSecret.TryDecode(secret, out var key))
            {
                throw new ArgumentException(InvalidSecretMessage, nameof(secrets));
            }

            keys[index] = key;
        }

        return keys;
    }

    /// <summary>
    /// Builds the signed content: the UTF-8 bytes of <c>id.timestamp.</c> followed by the raw body.
    /// </summary>
    /// <param name="webhookId">Message identifier.</param>
    /// <param name="timestamp">Signing moment in unix seconds, as the header spells it.</param>
    /// <param name="body">Raw body bytes.</param>
    /// <returns>The bytes the HMAC is computed over.</returns>
    private static byte[] SignedContent(string webhookId, string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = string.Concat(
            webhookId,
            HttpChannelConstants.SignedContentSeparator,
            timestamp,
            HttpChannelConstants.SignedContentSeparator);

        var prefixLength = Encoding.UTF8.GetByteCount(prefix);
        var content = new byte[prefixLength + body.Length];
        Encoding.UTF8.GetBytes(prefix, content);
        body.CopyTo(content.AsSpan(prefixLength));

        return content;
    }
}
