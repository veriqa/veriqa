// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text.Json;

namespace Veriqa.Core.ChannelAdapter.Http.Wire;

/// <summary>
/// The reason, fit for the log, of a received envelope the serializer does not read (SPEC-042 E52, N58).
/// </summary>
internal static class WireParseFailure
{
    /// <summary>
    /// Describes a failure of the serializer by where it happened, never by its message: the message of
    /// a tokenizer error quotes the invalid literal of the body, and the body stays out of the log.
    /// </summary>
    /// <param name="exception">The failure of the serializer.</param>
    /// <returns>The reason: the JSON path and the position the reading stopped at.</returns>
    public static string ReasonOf(JsonException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"the body is not JSON of the envelope shape (path {exception.Path ?? "$"}, line {exception.LineNumber}, byte in line {exception.BytePositionInLine})");
    }
}
