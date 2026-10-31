// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// Content of a <see cref="ChannelMessageKind.ActionButtons"/> message: the buttons drawn under the
/// message text (<see cref="ChannelMessage.Text"/>) by the post-sign-in actions (SPEC-012 §4.9).
/// </summary>
public sealed record ActionButtonsContent
{
    /// <summary>
    /// Buttons of the message; the order of the list is the order they are displayed in.
    /// </summary>
    public required IReadOnlyList<ChannelActionButton> Buttons { get; init; }

    /// <summary>
    /// Label of the control a platform shows instead of the buttons when it folds them into a list
    /// to pick from; already localized by the caller. Null — the adapter uses its own base text.
    /// </summary>
    public string? ChooserLabel { get; init; }
}

/// <summary>
/// One button of an <see cref="ActionButtonsContent"/>: a link the platform opens, or a callback
/// whose payload comes back to the adapter as an inbound event.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="Url"/> and <see cref="CallbackData"/> is set. A button that carries
/// neither or both is malformed, and an adapter does not draw it.
/// </remarks>
public sealed record ChannelActionButton
{
    /// <summary>
    /// Label of the button, already localized by the caller.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Address a link button opens; null for a callback button. Whether the platform accepts the
    /// scheme of the address is the platform's call.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Full payload of a callback button, the ownership marker included (see
    /// <see cref="Veriqa.Core.ChannelAdapter.Constants.CallbackDataPrefixes"/>); null for a link
    /// button. An adapter passes it to the platform as is and checks it against the payload budget
    /// the platform names.
    /// </summary>
    public string? CallbackData { get; init; }
}
