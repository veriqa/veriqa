// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.Identity;

/// <summary>
/// The one formula of the core subject (SPEC-003 CA-022): <c>channel_type:channel_user_id</c> of the channel
/// snapshot. The built-in identity resolution issues it as <c>sub</c>, and the collected claims store keys its
/// records by it (SPEC-041 C37) — before the confirmation, when no identity is resolved yet, and whatever
/// subject a substituted resolution or claims mapper later hands to the application.
/// </summary>
internal static class CoreSubject
{
    /// <summary>
    /// The core subject of a channel snapshot.
    /// </summary>
    /// <param name="snapshot">Channel snapshot of the confirmation.</param>
    /// <returns>The subject <c>channel_type:channel_user_id</c>.</returns>
    public static string From(ChannelIdentitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return $"{snapshot.ChannelType}:{snapshot.ChannelUserId}";
    }
}
