// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;

using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Reading of the payload of a pressed callback item of the post-sign-in menu (SPEC-012 §4.9): the
/// marker <see cref="CallbackDataPrefixes.MenuItem"/> followed by the value the operator configured for
/// the item. One home for the rule, so every adapter tells a menu press from the rest the same way:
/// a payload with the marker and a value is a press, a payload of the marker alone is a Veriqa payload
/// that stands for nothing and makes the event unrelated, anything else takes the adapter's other paths.
/// </summary>
internal static class MenuItemPayload
{
    /// <summary>
    /// Tells whether a payload carries the marker of a menu item, whether or not a value follows it.
    /// </summary>
    /// <param name="payload">Payload of a pressed button; null — the event carries none.</param>
    /// <returns>true — the payload starts with the marker.</returns>
    public static bool HasMarker([NotNullWhen(true)] string? payload) =>
        payload is not null && payload.StartsWith(CallbackDataPrefixes.MenuItem, StringComparison.Ordinal);

    /// <summary>
    /// Reads the value of a menu item off a payload.
    /// </summary>
    /// <param name="payload">Payload of a pressed button; null — the event carries none.</param>
    /// <param name="value">The value that follows the marker, as the operator stated it.</param>
    /// <returns>true — the payload starts with the marker and a non-empty value follows it.</returns>
    public static bool TryRead(string? payload, [NotNullWhen(true)] out string? value)
    {
        if (HasMarker(payload) && payload.Length > CallbackDataPrefixes.MenuItem.Length)
        {
            value = payload[CallbackDataPrefixes.MenuItem.Length..];
            return true;
        }

        value = null;
        return false;
    }
}
