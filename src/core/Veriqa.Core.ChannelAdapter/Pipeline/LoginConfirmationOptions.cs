// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Login confirmation settings (SPEC-012 §4.4, CFG-040…047). A single axis that consolidated the previous
/// two partially overlapping settings: the /start mode and the completion mode. Configuration section:
/// <see cref="SectionName"/> (<c>Veriqa:LoginConfirmation</c>). Resolved by the shared resolver across
/// ownership levels (§10.6) — the core level is read from these global options. The ui_config record
/// does NOT carry this mode (security, CFG-049).
/// <para>
/// The channel is a DIMENSION of the setting (SPEC-012 CFG-237): <see cref="ModeByChannel"/> is how a
/// level states the mode FOR one channel and <see cref="Mode"/> how it states the mode for every other,
/// so both are read inside one level before the resolution drops to the level below (CFG-236). A tenant
/// record and the client entry of an application state the same pair as <c>LoginConfirmationMode</c>
/// and <c>LoginConfirmationModeByChannel</c>.
/// </para>
/// </summary>
public sealed class LoginConfirmationOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:LoginConfirmation";

    /// <summary>
    /// Login confirmation mode (CFG-040) of every channel that has no entry in
    /// <see cref="ModeByChannel"/> (<c>Veriqa:LoginConfirmation:ModeByChannel:{channel}</c>). Default —
    /// <see cref="LoginConfirmationMode.ChannelDefault"/> (core default, CFG-041): the channel itself
    /// picks the concrete surface.
    /// </summary>
    public LoginConfirmationMode Mode { get; set; } = LoginConfirmationMode.ChannelDefault;

    /// <summary>
    /// Per-channel overrides of <see cref="Mode"/>, keyed by channel type (<c>telegram</c>,
    /// <c>email</c>, …): for example <c>Veriqa:LoginConfirmation:ModeByChannel:email = None</c> completes a
    /// sign-in through email without a confirmation question while every other channel keeps
    /// <see cref="Mode"/> (a confirmation transaction still asks: there <c>None</c> reads as
    /// <c>ChannelDefault</c>, SPEC-039 R36). A
    /// channel without an entry gets <see cref="Mode"/>; an entry naming a channel the transaction or the
    /// deployment does not have affects nothing. A level above the core outranks the map (CFG-238): an
    /// application stating its plain member alone decides the mode of every channel.
    /// </summary>
    public IDictionary<string, LoginConfirmationMode> ModeByChannel { get; init; } =
        new Dictionary<string, LoginConfirmationMode>(StringComparer.OrdinalIgnoreCase);
}
