// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Rate-limit policy names a host offers to the channel endpoints it maps. The policies themselves
/// belong to the host — it is the one that defines them on its rate limiter; a channel only names
/// which of them its route belongs to.
/// </summary>
/// <remarks>
/// The policies are offered by the kind of route, not by channel — the base of the channels knows no
/// channel names, and a channel names the kind of each route it maps. Four kinds of channel route need
/// telling apart, and they need different budgets: a webhook, called by the channel platform and
/// protected by a signature or a secret; a route of a sign-in a person goes through in a browser; a page
/// a link of a channel message opens; and a route whose request makes the channel send a message,
/// together with its form and the entry of a code from that message. A policy name left <c>null</c>
/// means the host defined no policy for that kind, and the routes of that kind are mapped without rate
/// limiting.
/// </remarks>
public sealed record ChannelEndpointPolicies
{
    /// <summary>
    /// Policy of the webhook routes (called by the channel platform). <c>null</c> — no limit.
    /// </summary>
    public string? WebhookPolicyName { get; init; }

    /// <summary>
    /// Policy of the routes of a sign-in a person goes through in a browser, for a channel whose sign-in
    /// happens in the browser itself — the start, the address of return and the actions the person sends
    /// from the page of the channel (an answer to a question, the press of a menu item). A page whose
    /// script reads its state on its own is not put under the policy, nor is that state: the script would
    /// spend the budget while the page stays open. <c>null</c> — no limit.
    /// </summary>
    public string? PagePolicyName { get; init; }

    /// <summary>
    /// Policy of the pages a link of a channel message opens — the link a person follows from a message the
    /// channel sent. <c>null</c> — no limit.
    /// </summary>
    public string? MessageLinkPolicyName { get; init; }

    /// <summary>
    /// Policy of the routes whose request makes the channel send a message, together with their form and
    /// the entry of a code from that message. <c>null</c> — no limit.
    /// </summary>
    public string? MessageSendPolicyName { get; init; }
}
