// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

using Microsoft.Extensions.Configuration;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Checks of a value as the deployment STATED it in the configuration text (SPEC-012 §8.2) — the ones
/// the startup validators share for what the binder drops or converts into something the text did not
/// say: a value that names no member of a closed set, and a value that is not an integer.
/// </summary>
internal static class StatedConfigurationText
{
    /// <summary>
    /// Failure of a stated value that names no member of a closed set; null when the member states no
    /// value or names a member.
    /// </summary>
    /// <typeparam name="TEnum">Closed set the value must name a member of.</typeparam>
    /// <param name="member">Configuration member holding the value.</param>
    /// <param name="address">Address of the member for the message.</param>
    /// <returns>Failure message, or null.</returns>
    public static string? UnknownStatedMember<TEnum>(IConfigurationSection member, string address)
        where TEnum : struct, Enum
    {
        var stated = member.Value;

        if (string.IsNullOrWhiteSpace(stated)
            || (Enum.TryParse<TEnum>(stated, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)))
        {
            return null;
        }

        return $"{address} is '{stated}', which is not a known value. "
            + $"Allowed values: {string.Join(", ", Enum.GetNames<TEnum>())}.";
    }

    /// <summary>
    /// Failure of a stated value that is not an integer; null when the member states no value or an
    /// integer.
    /// </summary>
    /// <param name="member">Configuration member holding the value.</param>
    /// <param name="address">Address of the member for the message.</param>
    /// <returns>Failure message, or null.</returns>
    public static string? UnparsableStatedInteger(IConfigurationSection member, string address)
    {
        var stated = member.Value;

        if (string.IsNullOrWhiteSpace(stated)
            || int.TryParse(stated, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return $"{address} is '{stated}', which is not an integer.";
    }
}
