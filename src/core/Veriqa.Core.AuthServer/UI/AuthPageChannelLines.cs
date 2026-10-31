// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// The lines the sign-in window shows for one of its channels, ready and worded in the content language of
/// the page. Each is a message of the template mechanism on the core page surface, resolved by the caller
/// over the levels of the transaction of the page — so a renderer, an integrator's own included, only
/// shows them and never words them. A sign-in and a confirmation get the same lines; only the wording of
/// the act differs.
/// </summary>
public sealed class AuthPageChannelLines
{
    /// <summary>
    /// The line once the answer is awaited in the channel — where the person went and what to do there
    /// (message kind <c>channel-answer-line</c>). Empty — the line could not be resolved, and the page shows
    /// none of its own for the channel.
    /// </summary>
    public required string Answer { get; init; }

    /// <summary>
    /// The line once the transition into the channel began (message kind <c>channel-launched-line</c>).
    /// Empty — the line could not be resolved, and the page shows its line without a channel instead.
    /// </summary>
    public required string Launched { get; init; }

    /// <summary>
    /// The line once the QR code of the channel was scanned (message kind <c>qr-scanned-line</c>). Empty —
    /// the line could not be resolved, and the page shows its line without a channel instead.
    /// </summary>
    public required string Scanned { get; init; }

    /// <summary>
    /// The hint of the channel the page shows once the phone is on its way into it (message kind
    /// <c>hop-channel-hint</c>). Empty — no hint for this channel.
    /// </summary>
    public string HopHint { get; init; } = string.Empty;
}
