// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Constants;

namespace Veriqa.Core.ChannelAdapter.DependencyInjection;

/// <summary>
/// Settings of one channel registration made through <c>ChannelAdapterBuilder.AddChannel</c>.
/// Every property carries the value the short registration overloads use, so a channel states only
/// what it needs to differ in.
/// </summary>
/// <remarks>
/// The object is what lets the built-in channels travel the very same registration path as a
/// third-party one: their own status texts and their verification handshake are declared here
/// instead of being switched on the channel's name inside the pipeline. A channel that declares
/// nothing gets exactly the behaviour a third-party channel used to get.
/// </remarks>
public sealed record ChannelRegistrationOptions
{
    /// <summary>
    /// Whether the generic webhook endpoint <c>POST /api/channels/{channelType}/webhook</c> (plus the
    /// optional tenant-segment variant) must be mapped for this channel. <c>false</c> — an
    /// outbound/polling-only channel, or a channel that maps an inbound route of its own through
    /// <see cref="Abstractions.IChannelEndpointRegistrar"/>.
    /// </summary>
    public bool MapWebhook { get; init; } = true;

    /// <summary>
    /// Status text (Natural Key) shown to the user when processing fails. It is the ONE status text a
    /// registration still states: the terminal outcome of a transaction is a message of the mechanism
    /// (SPEC-036 TPL-123) and is resolved by address, while an error the channel reports is not an
    /// outcome of the transaction at all (SPEC-036 §1.3).
    /// </summary>
    public string ErrorMessageText { get; init; } = CustomChannelConstants.ErrorMessageText;

    /// <summary>
    /// Natural Key of the reply to an AuthStart event whose transaction cannot be served
    /// (missing or expired; already decided — only where the core web page asks the question, in the
    /// channel a finished transaction stays silent). <c>null</c> — the channel stays silent, on every
    /// surface.
    /// One text for every reason: the wording must not tell "never existed" from "expired"
    /// (SPEC-039 C15).
    /// </summary>
    /// <remarks>
    /// A reply costs the installation what its platform charges for a message outside a template, so
    /// whether one is sent at all is the channel's declaration and not a rule of the pipeline. The
    /// shipped Telegram and MAX channels declare a text; WhatsApp does not.
    /// </remarks>
    public string? StaleLinkReplyText { get; init; }

    /// <summary>
    /// Natural Key of the reply to a start by a code no live transaction answers to
    /// (<c>ChannelAuthStartByCodeResult</c>): unknown, expired, already used. One text for every
    /// reason, as with a link: the wording must not tell whether such a code ever existed.
    /// <c>null</c> — the reply is <see cref="StaleLinkReplyText"/>, so a channel that stays silent on a
    /// link it cannot serve stays silent on such a code as well.
    /// </summary>
    /// <remarks>
    /// A text of its own because the user of a code followed no link: a channel started by a code may
    /// have none at all, and a sentence about an expired link sends its reader looking for one.
    /// </remarks>
    public string? UnmatchedCodeReplyText { get; init; }

    /// <summary>
    /// Natural Key of the reply to an event of a user that names no transaction
    /// (<c>ChannelUnaddressedResult</c>). <c>null</c> — no reply.
    /// </summary>
    public string? UnaddressedReplyText { get; init; }

    /// <summary>
    /// Name of the query parameter carrying the challenge of the platform's webhook verification
    /// handshake (the platform sends a GET to the webhook path and expects the challenge echoed back).
    /// <c>null</c> — the platform has no such handshake and no GET route is mapped.
    /// </summary>
    /// <remarks>
    /// The GET route is validated by the adapter's own <c>ValidateWebhookAsync</c>, exactly like the
    /// POST one: the verify token, its header or query spelling, and the mode check are the adapter's
    /// business. The core only knows where to read the value it has to echo.
    /// </remarks>
    public string? WebhookVerificationQueryKey { get; init; }
}
