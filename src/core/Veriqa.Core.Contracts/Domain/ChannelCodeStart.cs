// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// Declaration of a channel whose sign-in can be started by a short code: the core gives the
/// transaction a code, shows it on its own pages, and the user sends it to the bot as a message. The
/// adapter hands what arrived to the core as <see cref="ChannelAuthStartByCodeResult"/>; the core finds
/// the transaction by the code and carries on exactly as after <see cref="ChannelAuthStartResult"/>.
/// </summary>
/// <remarks>
/// The channel states only what it alone knows — the wording of the message its bot expects and
/// whether its link carries the transaction. The code itself belongs to the core: its length, its
/// lifetime and the limit of attempts are the core's, and a channel neither sets nor checks them.
/// <para>
/// The record does not reject a malformed <see cref="MessageTemplate"/>; the core judges it where the
/// channel is shown. A channel with <see cref="LinkCarriesTransaction"/> <c>false</c> and a template
/// the core cannot use is left out of the page, the way a channel that failed to build its link is.
/// </para>
/// </remarks>
public sealed record ChannelCodeStart
{
    /// <summary>
    /// Placeholder of the code inside <see cref="MessageTemplate"/>.
    /// </summary>
    public const string CodePlaceholder = "{code}";

    /// <summary>
    /// Longest <see cref="MessageTemplate"/> the core accepts, in characters.
    /// </summary>
    public const int MaxMessageTemplateLength = 64;

    /// <summary>
    /// The message the user sends to the bot, with <see cref="CodePlaceholder"/> standing for the code
    /// exactly once — for example <c>/veriqa {code}</c> or <c>auth_{code}</c>. <c>null</c> — the user
    /// sends the code alone. The command format is the channel's own: the core substitutes the code
    /// and shows the result, it does not interpret the wording, and the adapter strips the wrapper
    /// before handing the code over.
    /// </summary>
    public string? MessageTemplate { get; init; }

    /// <summary>
    /// The fact about the channel's link (<c>IChannelAdapter.GetDeepLinkAsync</c>). <c>false</c> — the
    /// link only opens the chat and carries no transaction, so the code is the only way to start;
    /// <c>true</c> — the link carries the transaction, and the code is a fallback start beside it, for
    /// a platform that does not always deliver what the link carries.
    /// </summary>
    public bool LinkCarriesTransaction { get; init; }
}
