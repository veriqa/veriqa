// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;
using System.Net.Mime;
using System.Text.Json;
using Microsoft.Net.Http.Headers;
using Veriqa.Core.AuthServer.Constants;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// A client signal of the launch page as its body states it (SPEC-025 HOP-066b): a JSON object of a fixed
/// schema of three string fields. Nothing of the body beyond these fields is read or kept.
/// </summary>
/// <param name="Signal">
/// The signal — <see cref="HopChannelStatusKeys.InterstitialOpened"/> or
/// <see cref="HopChannelStatusKeys.ChannelLaunched"/>: the values of the field are the codes of the channel
/// statuses the signals publish (SPEC-025 HOP-161).
/// </param>
/// <param name="Fingerprint">
/// The browser fingerprint (SPEC-025 HOP-090b), only with <see cref="HopChannelStatusKeys.InterstitialOpened"/>;
/// its form is checked. The value never reaches a log above <c>Debug</c>.
/// </param>
/// <param name="Channel">
/// The channel type, only with <see cref="HopChannelStatusKeys.ChannelLaunched"/> — the field of the unified
/// token (SPEC-025 HOP-191); whether it is admitted is decided by the receiver, not by the parsing.
/// </param>
internal sealed record HopClientSignal(string Signal, string? Fingerprint, string? Channel)
{
    /// <summary>
    /// Name of the field carrying the signal.
    /// </summary>
    internal const string SignalField = "signal";

    /// <summary>
    /// Name of the field carrying the browser fingerprint.
    /// </summary>
    internal const string FingerprintField = "fingerprint";

    /// <summary>
    /// Name of the field carrying the channel type.
    /// </summary>
    internal const string ChannelField = "channel";

    /// <summary>
    /// Parses the body of a client signal (SPEC-025 HOP-066b, HOP-090b). A pure function of the declared
    /// media type and the bytes of the body: it knows nothing of the token or the transaction.
    /// <para>
    /// The body is JSON under <c>application/json</c> and under <c>text/plain</c> alike, whatever the
    /// parameters of the media type: <c>sendBeacon</c> sends a string as <c>text/plain;charset=UTF-8</c>,
    /// and a beacon declared as <c>application/json</c> would need a preflight.
    /// </para>
    /// </summary>
    /// <param name="contentType">The <c>Content-Type</c> of the request, as it came; null when absent.</param>
    /// <param name="body">The body, already read under its limit.</param>
    /// <param name="signal">The signal; null when the body is refused.</param>
    /// <param name="rejection">
    /// Why the body is refused — <see cref="HopSignalRejection.MediaType"/>,
    /// <see cref="HopSignalRejection.Malformed"/> or <see cref="HopSignalRejection.FingerprintFormat"/>;
    /// meaningless when the body is accepted.
    /// </param>
    /// <returns><c>true</c> when the body is a signal of the fixed schema.</returns>
    public static bool TryParse(
        string? contentType,
        ReadOnlyMemory<byte> body,
        [NotNullWhen(true)] out HopClientSignal? signal,
        out HopSignalRejection rejection)
    {
        signal = null;

        if (!IsAcceptedMediaType(contentType))
        {
            rejection = HopSignalRejection.MediaType;
            return false;
        }

        if (ReadFields(body) is not { } fields)
        {
            rejection = HopSignalRejection.Malformed;
            return false;
        }

        if (fields.Signal is not (HopChannelStatusKeys.InterstitialOpened or HopChannelStatusKeys.ChannelLaunched))
        {
            rejection = HopSignalRejection.Malformed;
            return false;
        }

        // A value of a wrong form refuses the whole signal, not the value alone (SPEC-025 HOP-090b); so does
        // a fingerprint on a signal that does not carry one.
        if (fields.Fingerprint is { } fingerprint
            && (fields.Signal is not HopChannelStatusKeys.InterstitialOpened || !IsFingerprintForm(fingerprint)))
        {
            rejection = HopSignalRejection.FingerprintFormat;
            return false;
        }

        if (fields.Channel is not null && fields.Signal is HopChannelStatusKeys.InterstitialOpened)
        {
            rejection = HopSignalRejection.Malformed;
            return false;
        }

        rejection = default;
        signal = fields;
        return true;
    }

    /// <summary>
    /// Whether the declared media type is one the body is read under: <c>application/json</c> or
    /// <c>text/plain</c>, compared without its parameters and without regard to case.
    /// </summary>
    /// <param name="contentType">The declared media type; null when absent.</param>
    /// <returns><c>true</c> when the body is read.</returns>
    private static bool IsAcceptedMediaType(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            return false;
        }

        return parsed.MediaType.Equals(MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase)
            || parsed.MediaType.Equals(MediaTypeNames.Text.Plain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the three fields of the schema out of the body. The body must be a JSON object whose members
    /// are the fields of the schema, each at most once and each a string.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <returns>The fields as they came, the signal not yet checked; null when the body is not of the schema.</returns>
    private static HopClientSignal? ReadFields(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            string? signal = null;
            string? fingerprint = null;
            string? channel = null;

            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (member.Value.ValueKind is not JsonValueKind.String)
                {
                    return null;
                }

                // A string value is never null, so a filled slot is a repeated member.
                var value = member.Value.GetString();
                switch (member.Name)
                {
                    case SignalField when signal is null:
                        signal = value;
                        break;
                    case FingerprintField when fingerprint is null:
                        fingerprint = value;
                        break;
                    case ChannelField when channel is null:
                        channel = value;
                        break;
                    default:
                        return null;
                }
            }

            return signal is null ? null : new HopClientSignal(signal, fingerprint, channel);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // JsonException — the body is not JSON; InvalidOperationException — a name or a value is not
            // well-formed UTF-8 and cannot be read as text.
            return null;
        }
    }

    /// <summary>
    /// Whether a fingerprint is of its form (SPEC-025 HOP-090b): <see cref="HopModeConstants.FingerprintLength"/>
    /// characters of the base64url alphabet (RFC 4648 §5), no padding.
    /// </summary>
    /// <param name="fingerprint">The value.</param>
    /// <returns><c>true</c> when the value is of the form.</returns>
    private static bool IsFingerprintForm(string fingerprint)
    {
        if (fingerprint.Length != HopModeConstants.FingerprintLength)
        {
            return false;
        }

        foreach (var character in fingerprint)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
