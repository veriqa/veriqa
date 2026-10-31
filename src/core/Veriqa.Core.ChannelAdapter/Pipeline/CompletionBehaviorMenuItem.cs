// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// One item of the menu a channel shows after a completed sign-in (SPEC-012 §4.9, §6.7): the label
/// of a button and what pressing it does. Written by an operator in
/// <see cref="CompletionBehaviorOptions.MenuItems"/> or in the per-channel map beside it.
/// </summary>
public sealed class CompletionBehaviorMenuItem
{
    /// <summary>
    /// Label of the button — a Natural Key, localized by the locale of the recipient. An item with a
    /// blank label is outside the domain of the setting.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// What the button does: an absolute URL written as <c>scheme://…</c> makes it a link button,
    /// any other value is callback data handed to the handler the host registers. An item with a
    /// blank action, or with white space at the edges of it, is outside the domain of the setting.
    /// <para>
    /// A URL may carry the placeholder <c>{handle}</c> (this exact case): every occurrence is replaced
    /// by a one-time link handle the server of the integrator exchanges for the identity of the
    /// signed-in user; the URL items of one menu share one handle. A URL without the placeholder is
    /// sent as written. When the handle cannot be issued, the items asking for it are left out.
    /// Callback data is never searched for the placeholder.
    /// </para>
    /// </summary>
    public string Action { get; init; } = string.Empty;
}
