// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// Bounds of the avatar the core asks a channel for when the tokens are issued
/// (<see cref="Veriqa.Core.ChannelAdapter.Abstractions.IChannelAvatarSource"/>).
/// </summary>
internal static class ChannelAvatarConstants
{
    /// <summary>
    /// Time budget of one call of the avatar source, in seconds. Only the browser or the client
    /// waiting for this issuance waits for it; when it runs out the issuance goes on without the
    /// avatar. Longer than the budget the built-in Telegram adapter gives its own requests, so a
    /// channel that gives up by itself answers before the core does.
    /// </summary>
    public const int AvatarBudgetSeconds = 5;
}
