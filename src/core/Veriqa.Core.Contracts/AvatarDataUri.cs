// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Veriqa.Core.Contracts;

/// <summary>
/// The wire format of a user avatar carried as an image rather than a link: an RFC 2397
/// <c>data:&lt;mime&gt;;base64,&lt;payload&gt;</c> URI. This type is the single place that knows the
/// accepted image types, their byte signatures and the size limit, so a channel adapter producing
/// the value, the core validating it and a relying party decoding it apply the same rules.
/// The image type is always determined from the bytes; a declared type is trusted only when the
/// bytes confirm it.
/// </summary>
public static class AvatarDataUri
{
    /// <summary>
    /// Maximum decoded image size in bytes, inclusive (64 KiB). The limit bounds the stored payload of
    /// every token that carries the avatar as the <c>picture</c> claim. The built-in Telegram adapter
    /// hands its avatar over only when the tokens are issued (SPEC-003 CA-245), so that value is kept in
    /// no transaction snapshot. An adapter that puts a data URI into the channel identity snapshot has
    /// it stored there and, as the <c>picture</c> claim, in the resolved identity snapshot, and each
    /// snapshot holds at most 128 KB of serialized JSON (SPEC-001 §3.3). At this size the base64 payload
    /// is 87,384 characters, two thirds of that limit; the rest is left to the 4 KB raw metadata
    /// (SPEC-003 CA-005), the remaining identity fields and the JSON escaping of the payload, which
    /// writes every <c>+</c> of it as a six-character escape.
    /// </summary>
    public const int MaxImageBytes = 65_536;

    /// <summary>URI scheme prefix of a data URI (compared ordinally).</summary>
    private const string DataScheme = "data:";

    /// <summary>Separator between the media type and a base64-encoded payload.</summary>
    private const string Base64Separator = ";base64,";

    /// <summary>Base64 padding character.</summary>
    private const char Base64Padding = '=';

    /// <summary>Characters per base64 quantum.</summary>
    private const int Base64QuantumChars = 4;

    /// <summary>Bytes per base64 quantum.</summary>
    private const int Base64QuantumBytes = 3;

    /// <summary>Longest base64 payload that can decode to <see cref="MaxImageBytes"/> bytes.</summary>
    private const int MaxPayloadChars = (MaxImageBytes + Base64QuantumBytes - 1) / Base64QuantumBytes * Base64QuantumChars;

    /// <summary>JPEG media type.</summary>
    private const string Jpeg = "image/jpeg";

    /// <summary>PNG media type.</summary>
    private const string Png = "image/png";

    /// <summary>WebP media type.</summary>
    private const string WebP = "image/webp";

    /// <summary>GIF media type.</summary>
    private const string Gif = "image/gif";

    /// <summary>Accepted media types; a media type is matched regardless of letter case.</summary>
    private static readonly FrozenSet<string> AllowedContentTypes =
        new[] { Jpeg, Png, WebP, Gif }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Encodes an image as an avatar data URI. The media type is taken from the byte signature.
    /// </summary>
    /// <param name="image">Raw image bytes.</param>
    /// <param name="dataUri">
    /// <c>data:&lt;mime&gt;;base64,&lt;standard base64&gt;</c> on success; null otherwise.
    /// </param>
    /// <returns>
    /// True when the image is 1 to <see cref="MaxImageBytes"/> bytes long and its signature is one
    /// of the accepted image types.
    /// </returns>
    public static bool TryCreate(ReadOnlySpan<byte> image, [NotNullWhen(true)] out string? dataUri)
    {
        dataUri = null;
        if (image.Length > MaxImageBytes || !TryGetContentType(image, out var contentType))
        {
            return false;
        }

        dataUri = string.Concat(DataScheme, contentType, Base64Separator, Convert.ToBase64String(image));
        return true;
    }

    /// <summary>
    /// Parses and validates an avatar data URI. Never throws: any malformed or rejected value
    /// yields false.
    /// </summary>
    /// <param name="value">Candidate value, e.g. a <c>picture</c> claim.</param>
    /// <param name="image">
    /// The decoded image on success, with the canonical (lower-case) media type confirmed by the
    /// byte signature; null otherwise.
    /// </param>
    /// <returns>
    /// True when the value is <c>data:&lt;accepted mime&gt;;base64,&lt;payload&gt;</c>, the payload is
    /// valid base64 without whitespace, decodes to 1 to <see cref="MaxImageBytes"/> bytes, and the
    /// byte signature matches the declared media type.
    /// </returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out AvatarImage? image)
    {
        // Validation runs cheapest-first: prefix and media type, then the payload length (which
        // bounds the allocation before decoding), then the decode itself and the signature check.
        image = null;
        if (value is null || !value.StartsWith(DataScheme, StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = value.IndexOf(Base64Separator, DataScheme.Length, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return false;
        }

        var declaredType = value[DataScheme.Length..separatorIndex];
        if (!AllowedContentTypes.Contains(declaredType))
        {
            return false;
        }

        var payload = value.AsSpan(separatorIndex + Base64Separator.Length);
        if (!TryGetDecodedLength(payload, out var decodedLength))
        {
            return false;
        }

        var content = new byte[decodedLength];
        if (!Convert.TryFromBase64Chars(payload, content, out var written) || written != decodedLength)
        {
            return false;
        }

        if (!TryGetContentType(content, out var detectedType)
            || !string.Equals(detectedType, declaredType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        image = new AvatarImage(detectedType, content);
        return true;
    }

    /// <summary>
    /// Tells whether a value is an accepted avatar: an image data URI accepted by
    /// <see cref="TryParse"/> or an absolute https URL (OIDC Core 1.0 section 5.1). Never throws.
    /// </summary>
    /// <param name="value">Candidate value, e.g. <c>ChannelIdentitySnapshot.AvatarUrl</c>.</param>
    /// <returns>True when the value is one of the two accepted forms.</returns>
    public static bool IsAcceptedAvatar([NotNullWhen(true)] string? value)
    {
        return TryParse(value, out _) || IsAbsoluteHttpsUrl(value);
    }

    /// <summary>
    /// Tells whether a value is an absolute URL of the https scheme.
    /// </summary>
    private static bool IsAbsoluteHttpsUrl([NotNullWhen(true)] string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines the image media type from the byte signature. The length is not checked, so a
    /// caller that downloads an image over https applies its own download limit.
    /// </summary>
    /// <param name="image">Image bytes (at least the leading signature bytes).</param>
    /// <param name="contentType">One of the accepted media types on success; null otherwise.</param>
    /// <returns>True when the signature is recognised.</returns>
    public static bool TryGetContentType(ReadOnlySpan<byte> image, [NotNullWhen(true)] out string? contentType)
    {
        contentType = image switch
        {
            [0xFF, 0xD8, 0xFF, ..] => Jpeg,
            [0x89, 0x50, 0x4E, 0x47, ..] => Png,
            [0x47, 0x49, 0x46, 0x38, ..] => Gif,
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => WebP,
            _ => null
        };
        return contentType is not null;
    }

    /// <summary>
    /// Computes the exact decoded length of a canonical base64 payload (length a multiple of four,
    /// at most two trailing padding characters) and checks it against the size limit. A payload
    /// with embedded whitespace decodes to fewer bytes than computed here, so the caller's
    /// written-length comparison rejects it.
    /// </summary>
    private static bool TryGetDecodedLength(ReadOnlySpan<char> payload, out int decodedLength)
    {
        decodedLength = 0;
        if (payload.IsEmpty || payload.Length > MaxPayloadChars || payload.Length % Base64QuantumChars != 0)
        {
            return false;
        }

        var paddingLength = payload[^1] != Base64Padding ? 0 : payload[^2] != Base64Padding ? 1 : 2;
        decodedLength = payload.Length / Base64QuantumChars * Base64QuantumBytes - paddingLength;
        return decodedLength is >= 1 and <= MaxImageBytes;
    }
}
