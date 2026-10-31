// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Post-completion behavior settings (SPEC-012 §4.9): what a channel does after a sign-in is
/// completed. Configuration section: <see cref="SectionName"/> (<c>Veriqa:CompletionBehavior</c>).
/// <para>
/// The class is the SHAPE of the section an operator writes and the carrier of the value the product
/// ships with. It is not bound into an options object: every setting of it is resolved by the canonical
/// resolver across ownership levels and per channel (SPEC-012 §10.6), and that resolution is the only
/// way a setting of the section is read.
/// </para>
/// <para>
/// The channel is a DIMENSION of the setting (SPEC-012 CFG-237): <see cref="ActionByChannel"/> is how
/// a level states the action FOR one channel and <see cref="Action"/> how it states the action for
/// every other, so both are read inside one level before the resolution drops to the level below
/// (CFG-236). The menu items and the redirect address are cut by the channel the same way, each
/// through its own pair of members. A level above the core states the same members in the
/// <c>CompletionBehavior</c> section of its own record.
/// </para>
/// </summary>
public sealed class CompletionBehaviorOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:CompletionBehavior";

    /// <summary>
    /// Action applied to every channel that has no entry in <see cref="ActionByChannel"/>. Default —
    /// <see cref="CompletionBehaviorAction.None"/>: nothing is done after a sign-in is completed.
    /// </summary>
    public CompletionBehaviorAction Action { get; init; } = CompletionBehaviorAction.None;

    /// <summary>
    /// Per-channel overrides of <see cref="Action"/>, keyed by channel type (<c>telegram</c>,
    /// <c>whatsapp</c>, a third-party SPI channel type); the key is case-insensitive. A channel absent
    /// from the map takes <see cref="Action"/>.
    /// </summary>
    public IDictionary<string, CompletionBehaviorAction> ActionByChannel { get; init; } =
        new Dictionary<string, CompletionBehaviorAction>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Items of the menu shown by <see cref="CompletionBehaviorAction.Menu"/> on every channel that
    /// has no entry in <see cref="MenuItemsByChannel"/>, in display order. An empty list states
    /// nothing: the items then come from the level below.
    /// </summary>
    public IReadOnlyList<CompletionBehaviorMenuItem> MenuItems { get; init; } = [];

    /// <summary>
    /// Per-channel overrides of <see cref="MenuItems"/>, keyed by channel type; the key is
    /// case-insensitive. A channel absent from the map, or stated with an empty list, takes
    /// <see cref="MenuItems"/>.
    /// </summary>
    public IDictionary<string, IReadOnlyList<CompletionBehaviorMenuItem>> MenuItemsByChannel { get; init; } =
        new Dictionary<string, IReadOnlyList<CompletionBehaviorMenuItem>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Address of the link button shown by <see cref="CompletionBehaviorAction.Redirect"/> on every
    /// channel that has no entry in <see cref="RedirectUrlByChannel"/>: an absolute URL written as
    /// <c>scheme://…</c>. A value with white space at the edges of it is outside the domain of the
    /// setting. Default — <c>null</c>: no address is stated.
    /// <para>
    /// The address may carry the placeholder <c>{handle}</c> (this exact case): every occurrence is
    /// replaced by a one-time link handle the server of the integrator exchanges for the identity of
    /// the signed-in user. An address without the placeholder is sent as written and no handle is
    /// issued. When the handle cannot be issued, the message is not sent.
    /// </para>
    /// </summary>
    public string? RedirectUrl { get; init; }

    /// <summary>
    /// Per-channel overrides of <see cref="RedirectUrl"/>, keyed by channel type; the key is
    /// case-insensitive. A channel absent from the map takes <see cref="RedirectUrl"/>. Every address
    /// of the map is an absolute URL written as <c>scheme://…</c>; one with white space at the edges
    /// of it is outside the domain of the setting. An address of the map may carry the placeholder
    /// <c>{handle}</c> on the terms of <see cref="RedirectUrl"/>.
    /// </summary>
    public IDictionary<string, string> RedirectUrlByChannel { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Label of the link button shown by <see cref="CompletionBehaviorAction.Redirect"/> — a Natural
    /// Key, localized by the locale of the recipient.
    /// </summary>
    public string RedirectButtonText { get; init; } = "Open";
}
