// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Values of the <c>surface</c> dimension of the template key an outcome receipt is addressed with
/// (SPEC-036 TPL-123): WHERE the terminal text is shown. Constants, never magic strings — a render
/// point names the surface it shows on the same way it names the kind it shows.
/// <para>
/// The in-channel surfaces are TWO rather than one per channel, because the one thing today's
/// wordings differ in there is the presence of an emoji: a messenger renders it as a status, a
/// third-party platform is not guaranteed to render it at all. The <c>channel</c> dimension stays
/// declared beside the surface, so telling one messenger from another later needs a line of
/// configuration and no code (SPEC-036 TPL-117).
/// </para>
/// </summary>
public static class OutcomeReceiptSurfaces
{
    /// <summary>
    /// Inside a channel that declares <see cref="ChannelCapabilities.RendersEmoji"/> as
    /// <c>true</c>: emoji render.
    /// </summary>
    public const string InChannelMessenger = "in-channel-messenger";

    /// <summary>
    /// Inside a channel that does not declare <see cref="ChannelCapabilities.RendersEmoji"/> as
    /// <c>true</c>, where emoji are not guaranteed to render.
    /// </summary>
    public const string InChannelPlain = "in-channel-plain";

    /// <summary>
    /// The magic-link confirmation page of the Email adapter (SPEC-016 §4.3).
    /// </summary>
    public const string EmailConfirmPage = "email-confirm-page";

    /// <summary>
    /// The confirmation page of the core, where the question is asked on the web (SPEC-039 R37) — the
    /// same place the question itself is addressed at, so it is spelled by that one constant (SPEC-036 §4.3).
    /// </summary>
    public const string CorePage = MessageSurfaces.CorePage;

    /// <summary>
    /// The interaction page of a confirmation transaction (SPEC-039 C15): a transaction with no
    /// browser OIDC callback leaves the user on that page, so its status line is a terminal screen.
    /// </summary>
    public const string InteractionPage = "interaction-page";

    /// <summary>
    /// The in-channel surface a channel is worded for. It is the ONE rule of the two in-channel
    /// surfaces, and it is a property of the channel rather than of the render point (SPEC-036
    /// TPL-123), so every point showing a receipt inside a channel asks it here instead of restating
    /// the rule.
    /// </summary>
    /// <remarks>
    /// The channel alone answers, through <see cref="ChannelCapabilities.RendersEmoji"/>: a channel
    /// that declares it renders emoji is worded as a messenger whoever ships it, and a channel that
    /// declares <c>false</c> or nothing is worded plain. The core keeps no list of channel types
    /// here — the product's own adapters declare the capability like any other.
    /// </remarks>
    /// <param name="adapter">Adapter of the channel the receipt is shown in.</param>
    /// <returns>The surface value the receipt is addressed with.</returns>
    public static string ForChannel(IChannelAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        return adapter.Capabilities.RendersEmoji switch
        {
            true => InChannelMessenger,
            false or null => InChannelPlain
        };
    }
}
