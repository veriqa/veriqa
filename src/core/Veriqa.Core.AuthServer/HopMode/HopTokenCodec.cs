// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;

using Microsoft.AspNetCore.DataProtection;

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Turns a <see cref="HopToken"/> into the opaque URL-safe string of a hop URL and back
/// (SPEC-025 HOP-034). The payload is binary, its format version first (SPEC-025 HOP-050: the token is
/// kept short, since it is what the QR code carries), and it is protected by ASP.NET Core Data
/// Protection — the platform's own integrity and opacity, not cryptography of ours: the key ring is the
/// host's, so a token issued by one instance decodes on another.
/// </summary>
/// <remarks>
/// Payload layout: format version (1 byte), issued at and expires at (Unix seconds, unsigned 32-bit
/// big-endian each), the transaction identifier (its fixed-length ASCII form), the length of the
/// channel type in bytes (1 byte) and the channel type (UTF-8). The codec never judges the expiry and
/// never writes to a log: the token is a capability, and neither it nor any field of it may reach one.
/// </remarks>
internal sealed class HopTokenCodec
{
    /// <summary>
    /// Offset of the format version.
    /// </summary>
    private const int VersionOffset = 0;

    /// <summary>
    /// Offset of the moment of issue.
    /// </summary>
    private const int IssuedAtOffset = VersionOffset + sizeof(byte);

    /// <summary>
    /// Offset of the own expiry of the token.
    /// </summary>
    private const int ExpiresAtOffset = IssuedAtOffset + sizeof(uint);

    /// <summary>
    /// Offset of the transaction identifier.
    /// </summary>
    private const int TransactionIdOffset = ExpiresAtOffset + sizeof(uint);

    /// <summary>
    /// Offset of the length of the channel type.
    /// </summary>
    private const int ChannelTypeLengthOffset = TransactionIdOffset + TransactionId.StringLength;

    /// <summary>
    /// Offset of the channel type; everything before it has a fixed length.
    /// </summary>
    private const int ChannelTypeOffset = ChannelTypeLengthOffset + sizeof(byte);

    /// <summary>
    /// Strict UTF-8: a channel type that is not well-formed text is refused on encoding instead of being
    /// silently replaced, so a decoded token always names the channel it was issued for.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Protector of the hop-token purpose.
    /// </summary>
    private readonly IDataProtector _protector;

    /// <summary>
    /// Creates the codec from the Data Protection provider of the host.
    /// </summary>
    /// <param name="dataProtectionProvider">Data Protection provider (the key ring is the host's).</param>
    public HopTokenCodec(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);

        _protector = dataProtectionProvider.CreateProtector(HopModeConstants.HopTokenProtectorPurpose);
    }

    /// <summary>
    /// Encodes the token into the base64url form (without padding) of its protected payload.
    /// </summary>
    /// <param name="token">Token to encode. The expiry is encoded as is, even when it is not later than the issue.</param>
    /// <returns>Opaque URL-safe token string.</returns>
    /// <exception cref="ArgumentException">
    /// The channel type is null, longer than <see cref="HopModeConstants.MaxTokenChannelTypeLength"/> or
    /// not well-formed text; the transaction identifier is the default one; a moment lies outside the
    /// range the payload carries (1970 to 2106).
    /// </exception>
    public string Encode(HopToken token)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (token.ChannelType is null || token.ChannelType.Length > HopModeConstants.MaxTokenChannelTypeLength)
        {
            throw new ArgumentException(
                $"The channel type of a hop token must not be null or longer than {HopModeConstants.MaxTokenChannelTypeLength} characters.",
                nameof(token));
        }

        var transactionId = token.TransactionId.ToString();
        if (transactionId.Length != TransactionId.StringLength)
        {
            throw new ArgumentException("A hop token must carry a transaction identifier.", nameof(token));
        }

        // A UTF-16 unit takes at most three UTF-8 bytes, so the byte length of a channel type within the
        // cap always fits the one-byte length field.
        var channelType = StrictUtf8.GetBytes(token.ChannelType);

        var payload = new byte[ChannelTypeOffset + channelType.Length];
        payload[VersionOffset] = HopModeConstants.HopTokenFormatVersion;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(IssuedAtOffset), ToPayloadSeconds(token.IssuedAt));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(ExpiresAtOffset), ToPayloadSeconds(token.ExpiresAt));
        Encoding.ASCII.GetBytes(transactionId, payload.AsSpan(TransactionIdOffset, TransactionId.StringLength));
        payload[ChannelTypeLengthOffset] = (byte)channelType.Length;
        channelType.CopyTo(payload.AsSpan(ChannelTypeOffset));

        return Base64Url.EncodeToString(_protector.Protect(payload));
    }

    /// <summary>
    /// Decodes a presented token string. Never throws: whatever does not decode into a token of the
    /// current format — no value, not base64url, a payload Data Protection refuses (another purpose, a
    /// forgery, a key no longer in the ring), an unknown version, a truncated or overlong payload, a
    /// malformed transaction identifier or an overlong channel type — yields <c>false</c>.
    /// </summary>
    /// <param name="value">Presented token string.</param>
    /// <param name="token">Decoded token; its expiry is returned as is, not judged.</param>
    /// <returns><c>true</c> when the value is a token of the current format.</returns>
    public bool TryDecode(string? value, [NotNullWhen(true)] out HopToken? token)
    {
        token = null;

        if (string.IsNullOrEmpty(value) || !Base64Url.IsValid(value))
        {
            return false;
        }

        byte[] payload;
        try
        {
            payload = _protector.Unprotect(Base64Url.DecodeFromChars(value));
        }
        catch (CryptographicException)
        {
            // Data Protection reports every refusal — tampering, another purpose, a missing key — as this
            // one exception type. Nothing of the value is logged: the token is a capability.
            return false;
        }

        return TryReadPayload(payload, out token);
    }

    /// <summary>
    /// Reads the fields of an unprotected payload, refusing any layout but the current one exactly.
    /// </summary>
    /// <param name="payload">Unprotected payload.</param>
    /// <param name="token">Token read from the payload.</param>
    /// <returns><c>true</c> when the payload is of the current format and every field is valid.</returns>
    private static bool TryReadPayload(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out HopToken? token)
    {
        token = null;

        if (payload.Length < ChannelTypeOffset
            || payload[VersionOffset] != HopModeConstants.HopTokenFormatVersion
            || payload.Length != ChannelTypeOffset + payload[ChannelTypeLengthOffset])
        {
            return false;
        }

        var channelTypeBytes = payload[ChannelTypeOffset..];
        if (!Utf8.IsValid(channelTypeBytes))
        {
            return false;
        }

        var channelType = StrictUtf8.GetString(channelTypeBytes);
        if (channelType.Length > HopModeConstants.MaxTokenChannelTypeLength)
        {
            return false;
        }

        // A byte outside ASCII decodes to '?', which the identifier format refuses.
        var transactionIdText = Encoding.ASCII.GetString(payload.Slice(TransactionIdOffset, TransactionId.StringLength));
        if (!TransactionId.TryParse(transactionIdText, out var transactionId))
        {
            return false;
        }

        token = new HopToken(
            transactionId,
            channelType,
            DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(payload[IssuedAtOffset..])),
            DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(payload[ExpiresAtOffset..])));

        return true;
    }

    /// <summary>
    /// Converts a moment into the unsigned Unix seconds of the payload, truncating the fraction of a
    /// second.
    /// </summary>
    /// <param name="moment">Moment to convert.</param>
    /// <returns>Unix seconds of the moment.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The moment lies outside the range the payload carries.</exception>
    private static uint ToPayloadSeconds(DateTimeOffset moment)
    {
        var seconds = moment.ToUnixTimeSeconds();
        if (seconds is < 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(moment), "A moment of a hop token must lie between 1970 and 2106.");
        }

        return (uint)seconds;
    }
}
