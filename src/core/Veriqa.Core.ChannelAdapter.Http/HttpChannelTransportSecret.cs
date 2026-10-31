// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;

using Veriqa.Core.ChannelAdapter.Http.Constants;

namespace Veriqa.Core.ChannelAdapter.Http;

/// <summary>
/// The Standard Webhooks serialization of a transport secret (SPEC-042 C30, C31): <c>whsec_</c>
/// followed by the key in base64. One decoder for the start-up checks of a registration and for the
/// signature of the wire, so the two cannot disagree about what a valid secret is.
/// </summary>
internal static class HttpChannelTransportSecret
{
    /// <summary>
    /// Decodes a serialized transport secret. The value itself never leaves this method in any other
    /// form than the decoded key: a caller reports a failure by the configuration key, never by the
    /// value.
    /// </summary>
    /// <param name="serialized">Secret in the <c>whsec_</c> serialization.</param>
    /// <param name="key">Decoded key; <c>null</c> when the value is not a valid secret.</param>
    /// <returns>
    /// <c>true</c> when the value carries the prefix, the rest is valid base64 and the decoded key is
    /// between <see cref="HttpChannelConstants.MinTransportSecretBytes"/> and
    /// <see cref="HttpChannelConstants.MaxTransportSecretBytes"/> bytes long.
    /// </returns>
    public static bool TryDecode(string serialized, [NotNullWhen(true)] out byte[]? key)
    {
        ArgumentNullException.ThrowIfNull(serialized);

        key = null;

        if (!serialized.StartsWith(HttpChannelConstants.TransportSecretPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encoded = serialized[HttpChannelConstants.TransportSecretPrefix.Length..];
        var buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(encoded.Length)];

        if (!Convert.TryFromBase64String(encoded, buffer, out var written)
            || written < HttpChannelConstants.MinTransportSecretBytes
            || written > HttpChannelConstants.MaxTransportSecretBytes)
        {
            return false;
        }

        key = buffer[..written];
        return true;
    }
}
