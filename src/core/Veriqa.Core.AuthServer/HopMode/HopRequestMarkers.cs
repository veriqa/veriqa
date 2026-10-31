// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Markers of who presented a hop URL, as a refusal record carries them (SPEC-025 HOP-114): the kind of
/// the request does not suppress the record, it travels in it as a field — the <c>Details</c> of the
/// audit event and a field of the log entry.
/// </summary>
internal static class HopRequestMarkers
{
    /// <summary>
    /// The URL was presented by a person.
    /// </summary>
    public const string Human = "request=human";

    /// <summary>
    /// The URL was presented by a link-preview agent or a prefetch.
    /// </summary>
    public const string Preview = "request=preview";

    /// <summary>
    /// The URL was presented by a client signal of the launch page (<c>POST</c>, SPEC-025 HOP-190): the
    /// kind of the presenter is not judged there, the method is what the record names.
    /// </summary>
    public const string Signal = "request=signal";

    /// <summary>
    /// The marker of a kind of request.
    /// </summary>
    /// <param name="kind">Who presented the URL.</param>
    /// <returns>The marker.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not a member of the enumeration.</exception>
    public static string Of(HopRequestKind kind) => kind switch
    {
        HopRequestKind.Human => Human,
        HopRequestKind.Preview => Preview,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of a hop request.")
    };
}
