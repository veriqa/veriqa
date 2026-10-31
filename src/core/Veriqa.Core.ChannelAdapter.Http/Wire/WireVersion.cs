// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

using Veriqa.Core.ChannelAdapter.Http.Constants;

namespace Veriqa.Core.ChannelAdapter.Http.Wire;

/// <summary>
/// Reading of the wire version <c>major.minor</c> of a received envelope (SPEC-042 C40, L42): the
/// receiver compares only the major part — minor versions differ by additions it is able to ignore.
/// </summary>
internal static class WireVersion
{
    /// <summary>
    /// Reads the major part of a wire version.
    /// </summary>
    /// <param name="version">Version as the envelope spells it.</param>
    /// <param name="major">The major part; 0 when the value is not a version.</param>
    /// <returns><c>true</c> when the value is two non-negative integers separated by a dot.</returns>
    public static bool TryGetMajor(string version, out int major)
    {
        ArgumentNullException.ThrowIfNull(version);

        major = 0;

        var separator = version.IndexOf(HttpChannelConstants.WireVersionSeparator, StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }

        if (!int.TryParse(version.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMajor)
            || !int.TryParse(version.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        major = parsedMajor;
        return true;
    }
}
