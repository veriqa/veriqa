// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// The cookies in which the script of the sign-in window remembers the TYPE of the channel the user went
/// through on this device — never the account in that channel. The script writes and reads them; the server
/// never reads them and only hands the script their names, their lifetime and their attributes
/// (<see cref="AuthPageScriptChannelCookie"/>), so the script spells none of them.
/// </summary>
internal static class RememberedChannelCookies
{
    /// <summary>
    /// Name of the cookie of the hint of the last channel (key <c>AuthPageDesign.RememberChannel.Hint</c>):
    /// written without a request of the user, apart from the cookie of the checkbox, so that a write of the
    /// hint never reads as the user's choice.
    /// </summary>
    public const string LastChannelName = "veriqa_last_channel";

    /// <summary>
    /// Name of the cookie of the checkbox that remembers the channel (key
    /// <c>AuthPageDesign.RememberChannel.Checkbox</c>): written only while the user keeps the box ticked, and
    /// deleted once they untick it.
    /// </summary>
    public const string RememberedChannelName = "veriqa_remembered_channel";

    /// <summary>
    /// Attributes of every remembered channel cookie besides its lifetime: the whole host, sent on a top-level
    /// navigation from another site — the way back from the application into the window — and over HTTPS
    /// only.
    /// </summary>
    public const string Attributes = "Path=/; SameSite=Lax; Secure";

    /// <summary>Lifetime of a remembered channel cookie, in days, counted from its last write.</summary>
    private const int LifetimeDays = 180;

    /// <summary>Lifetime of a remembered channel cookie, in seconds — the value of its <c>Max-Age</c>.</summary>
    public static int MaxAgeSeconds { get; } = (int)TimeSpan.FromDays(LifetimeDays).TotalSeconds;

    /// <summary>The cookie of the hint of the last channel, as the script receives it.</summary>
    public static AuthPageScriptChannelCookie LastChannel { get; } = new(LastChannelName, MaxAgeSeconds, Attributes);

    /// <summary>The cookie of the checkbox that remembers the channel, as the script receives it.</summary>
    public static AuthPageScriptChannelCookie RememberedChannel { get; } =
        new(RememberedChannelName, MaxAgeSeconds, Attributes);
}
