// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Domain;

namespace Veriqa.Core.ChannelAdapter.Meta;

/// <summary>
/// Proof that a webhook request comes from Meta, the same for every channel of the Messenger
/// Platform family: the <c>GET</c> handshake a subscription is verified with, and the
/// <c>X-Hub-Signature-256</c> signature of a <c>POST</c> delivery. The secrets are the caller's:
/// a channel resolves the credentials of the tenant of the request and hands them in.
/// </summary>
/// <remarks>
/// The log messages name no channel: the logger a channel passes carries its category, and that is
/// what tells whose webhook was rejected.
/// </remarks>
internal static class MetaWebhookVerification
{
    /// <summary>
    /// Header carrying the SHA-256 signature Meta signs a webhook delivery with.
    /// </summary>
    public const string SignatureHeader = "X-Hub-Signature-256";

    /// <summary>
    /// Prefix of the signature header value; the hexadecimal HMAC follows it.
    /// </summary>
    public const string SignaturePrefix = "sha256=";

    /// <summary>
    /// Length (bytes) of an HMAC-SHA256 signature and of a SHA-256 hash.
    /// </summary>
    public const int Sha256SizeBytes = 32;

    /// <summary>
    /// Query parameter of the webhook <c>GET</c> handshake naming its mode.
    /// </summary>
    public const string HubModeQueryKey = "hub.mode";

    /// <summary>
    /// The only handshake mode Meta sends: a subscription being verified.
    /// </summary>
    public const string HubSubscribeMode = "subscribe";

    /// <summary>
    /// Query parameter of the webhook <c>GET</c> handshake carrying the verify token.
    /// </summary>
    public const string HubVerifyTokenQueryKey = "hub.verify_token";

    /// <summary>
    /// Query parameter of the webhook <c>GET</c> handshake whose value is echoed back to Meta.
    /// </summary>
    public const string HubChallengeQueryKey = "hub.challenge";

    /// <summary>
    /// Checks the <c>GET</c> handshake Meta verifies a webhook subscription with.
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="configuredToken">Verify token of the tenant.</param>
    /// <param name="logger">Logger of the channel the request is addressed to.</param>
    /// <returns>true — the request names the subscribe mode and carries the configured token.</returns>
    public static bool IsValidHandshake(ChannelInboundRequest request, string? configuredToken, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(logger);

        request.Query.TryGetValue(HubModeQueryKey, out var mode);
        if (!string.Equals(mode, HubSubscribeMode, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            logger.LogError("Webhook rejected: WebhookVerifyToken is not configured.");
            return false;
        }

        request.Query.TryGetValue(HubVerifyTokenQueryKey, out var suppliedToken);

        // Fixed-length SHA-256 hashes are compared, so the comparison takes the same time whatever
        // the supplied token is and leaks neither its content nor the length of the configured one
        // (CA-033).
        Span<byte> configuredHash = stackalloc byte[Sha256SizeBytes];
        Span<byte> suppliedHash = stackalloc byte[Sha256SizeBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(configuredToken), configuredHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken ?? string.Empty), suppliedHash);

        return CryptographicOperations.FixedTimeEquals(configuredHash, suppliedHash);
    }

    /// <summary>
    /// Checks the <c>X-Hub-Signature-256</c> signature of a <c>POST</c> delivery: the HMAC-SHA256 of
    /// the body under the app secret.
    /// </summary>
    /// <param name="request">Neutral inbound request envelope.</param>
    /// <param name="appSecret">App secret of the tenant.</param>
    /// <param name="logger">Logger of the channel the request is addressed to.</param>
    /// <returns>true — the signature is of this very body under the configured secret.</returns>
    public static bool IsValidSignature(ChannelInboundRequest request, string? appSecret, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(appSecret))
        {
            logger.LogError("Webhook rejected: AppSecret is not configured.");
            return false;
        }

        if (!request.Headers.TryGetValue(SignatureHeader, out var signatureHeader)
            || string.IsNullOrWhiteSpace(signatureHeader)
            || !signatureHeader.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Webhook rejected: header {SignatureHeader} is missing or malformed.",
                SignatureHeader);
            return false;
        }

        // A value that is not the hexadecimal form of a SHA-256 signature cannot be one; rejecting
        // it by its shape reveals nothing about the secret.
        if (!TryReadSignature(signatureHeader, out var supplied))
        {
            logger.LogWarning("Webhook rejected: the signature is not a SHA-256 value.");
            return false;
        }

        // The envelope carries the body byte for byte, so the HMAC is computed over exactly what
        // arrived; the two signatures are compared in constant time (CA-033).
        Span<byte> expected = stackalloc byte[Sha256SizeBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), request.Body.Span, expected);

        var isValid = CryptographicOperations.FixedTimeEquals(expected, supplied);
        if (!isValid)
        {
            logger.LogWarning("Webhook rejected: invalid signature.");
        }

        return isValid;
    }

    /// <summary>
    /// Reads the signature bytes out of the value of the signature header.
    /// </summary>
    /// <param name="signatureHeader">Header value, already known to start with the signature prefix.</param>
    /// <param name="signature">Signature bytes on success.</param>
    /// <returns>true — the value is the hexadecimal form of a SHA-256 signature.</returns>
    private static bool TryReadSignature(string signatureHeader, out byte[] signature)
    {
        try
        {
            signature = Convert.FromHexString(signatureHeader.AsSpan(SignaturePrefix.Length).Trim());
        }
        catch (FormatException)
        {
            signature = [];
        }

        return signature.Length == Sha256SizeBytes;
    }
}
