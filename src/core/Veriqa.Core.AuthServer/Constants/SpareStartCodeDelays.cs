// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Constants;

/// <summary>
/// How long a page keeps the spare start code of a channel out of sight before it offers it unasked. The
/// code is the way on when the link of the channel opened the chat and nothing started there, so it is
/// shown once that has had time to happen. The values reach the scripts of the pages as data; no script
/// states a delay of its own.
/// </summary>
internal static class SpareStartCodeDelays
{
    /// <summary>
    /// Delay on a page nobody has left for a channel yet, counted from the load of the page, in
    /// milliseconds.
    /// </summary>
    public const int IdleMilliseconds = 8000;

    /// <summary>
    /// Delay after a transition into a channel began, counted from that moment, in milliseconds.
    /// </summary>
    public const int AfterTransitionMilliseconds = 6000;
}
