// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.UI.Services;

/// <summary>
/// The one test of whether the address a channel offered is a point a user can be sent to — shared by
/// the entries of a creation answer and by the step that turns a deep link into the content of a QR code.
/// </summary>
internal static class ChannelAddress
{
    /// <summary>
    /// Whether the address of a channel is one a user can be sent to as it is.
    /// </summary>
    /// <remarks>
    /// A rooted PATH is not: the Email adapter in Pull mode answers with the start path of its own
    /// page, because until the person has typed an address there is no addressable point to send them
    /// to. Excluding the <c>file</c> scheme is what makes the check say so on every platform — on Unix
    /// <see cref="Uri.TryCreate(string?, UriKind, out Uri?)"/> reads a rooted path as an absolute
    /// <c>file:</c> URI, and that path would otherwise pass for a deep link.
    /// </remarks>
    /// <param name="url">Address the channel offered.</param>
    /// <returns><see langword="true"/> when the address is absolute and addresses a channel.</returns>
    public static bool IsAbsoluteChannelAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile;
}
