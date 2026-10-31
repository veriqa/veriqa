// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.UI.Services;

/// <summary>
/// The channels a display list is built from, before any adapter is asked for anything.
/// </summary>
/// <param name="Adapters">The adapters of the channels of the list, in display order.</param>
/// <param name="EffectiveMode">The channel display mode the list was filtered by.</param>
internal sealed record ChannelSetSelection(
    IReadOnlyList<IChannelAdapter> Adapters,
    ChannelDisplayMode EffectiveMode);

/// <summary>
/// The one filter and order of the channels of a display list (SPEC-007 §3, SPEC-002 §4.6, CFG-211): the
/// registered adapters, narrowed by the owner of the channel registration (SPEC-042 R60), by the allowed
/// channel types of the transaction and by the <c>Selective</c> mode or the channel list of the
/// <c>ui_config</c> record, ordered by that list or by
/// <see cref="ChannelDisplayOptions.ChannelOrder"/>. The display list of the sign-in page and of the channel
/// choice page is built over it, and so is the channel set of a transaction the unified hop QR code is
/// judged by (SPEC-025 HOP-182) — one filter, so the two cannot part.
/// </summary>
/// <remarks>
/// It asks no adapter for a deep link and writes nothing. Scoped: it reads the channel adapters, whose
/// lifetime a host may narrow to the request.
/// </remarks>
internal sealed class ChannelSetFilter
{
    /// <summary>
    /// Registered channel adapters.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Channel display settings — the ORDER of the channels alone. The mode is a setting owned by a
    /// level and is resolved through <see cref="_pageSettings"/>, not read from the bound section.
    /// </summary>
    private readonly IOptions<VeriqaOptions> _options;

    /// <summary>
    /// Resolver of the level-owned sign-in page settings — the source of the effective display mode.
    /// </summary>
    private readonly AuthPageSettingsResolver _pageSettings;

    /// <summary>
    /// Core-level declarations of the channels — the source of the owner of a channel registration.
    /// </summary>
    private readonly IEnumerable<CoreChannelDeclaration> _channelDeclarations;

    /// <summary>
    /// Creates the filter.
    /// </summary>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="options">Veriqa settings (ChannelDisplay.ChannelOrder).</param>
    /// <param name="pageSettings">Resolver of the level-owned sign-in page settings.</param>
    /// <param name="channelDeclarations">Core-level declarations of the channels.</param>
    public ChannelSetFilter(
        IEnumerable<IChannelAdapter> adapters,
        IOptions<VeriqaOptions> options,
        AuthPageSettingsResolver pageSettings,
        IEnumerable<CoreChannelDeclaration> channelDeclarations)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pageSettings);
        ArgumentNullException.ThrowIfNull(channelDeclarations);

        _adapters = adapters;
        _options = options;
        _pageSettings = pageSettings;
        _channelDeclarations = channelDeclarations;
    }

    /// <summary>
    /// Selects the channels of a display list, in display order.
    /// </summary>
    /// <param name="allowedChannelTypes">Allowed channel types; an empty set admits every channel.</param>
    /// <param name="resolution">
    /// Resolution context the display mode is resolved over; its tenant is the one the channels are
    /// selected for.
    /// </param>
    /// <param name="displayOverride">Per-request ui_config display override (null — global behavior, CFG-211).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The adapters of the list and the mode it was filtered by.</returns>
    public async ValueTask<ChannelSetSelection> SelectAsync(
        IReadOnlySet<string> allowedChannelTypes,
        ResolutionContext resolution,
        ChannelDisplayOverride? displayOverride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allowedChannelTypes);
        ArgumentNullException.ThrowIfNull(resolution);

        // The method narrows the registered channels by the Mode/ChannelOrder settings and the per-request
        // ui_config display override (CFG-211 narrowing: filter/reorder/mode, never add), then by the
        // allowed channel types and by the owner of the channel registration.

        // Effective mode: the record's mode narrows the global one. A caller that states no override
        // asks the mechanism for the same chain the override was filled from (core → ui_config), so
        // the mode is the resolved value of the key either way and never a field of the bound section.
        var effectiveMode = displayOverride?.Mode
            ?? await _pageSettings.ResolveChannelDisplayModeAsync(resolution, cancellationToken);

        // Normalize the record's channel override the same way as ChannelOrder (drop empty/whitespace,
        // trim, dedup case-insensitive). A non-empty record list REPLACES the global ChannelOrder as the
        // source of both display order and the shown-set filter (CFG-211); an empty/null list — no override.
        var normalizedOverrideChannels = displayOverride?.Channels?
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type.Trim())
            .DistinctBy(type => type, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasChannelSetOverride = normalizedOverrideChannels is { Count: > 0 };

        // Normalize the effective order source once: the record's channel list when it overrides,
        // otherwise the global ChannelOrder. Used uniformly for sorting and Selective/override filtering.
        var normalizedChannelOrder = hasChannelSetOverride
            ? normalizedOverrideChannels!
            : _options.Value.ChannelDisplay.ChannelOrder
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Select(type => type.Trim())
                .DistinctBy(type => type, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Build the set of allowed channels when it must be filtered to a list: either Selective mode,
        // or an explicit record channel-set override (CFG-211 narrowing — only the listed channels show).
        // null — no such filter.
        var selectiveAllowed = (effectiveMode is ChannelDisplayMode.Selective || hasChannelSetOverride)
            ? new HashSet<string>(normalizedChannelOrder, StringComparer.OrdinalIgnoreCase)
            : null;

        var selected = ApplyChannelOrder(_adapters, normalizedChannelOrder)
            .Where(adapter => allowedChannelTypes.Count is 0 || allowedChannelTypes.Contains(adapter.ChannelType))
            .Where(adapter => selectiveAllowed is null || selectiveAllowed.Contains(adapter.ChannelType))
            // SPEC-042 R60: a channel whose registration belongs to another tenant does not exist for this
            // one — no consumer of the set builds an entry into it, issues a hop token for it or accepts a
            // signal of it. Not a failure of the channel, so no log.
            .Where(adapter => !ChannelRegistrationOwnership.IsOwnedByAnotherTenant(
                _channelDeclarations, adapter.ChannelType, resolution.TenantId))
            .ToList();

        return new ChannelSetSelection(selected.AsReadOnly(), effectiveMode);
    }

    /// <summary>
    /// Orders the adapters according to the ChannelOrder configuration.
    /// Adapters not mentioned in ChannelOrder are appended at the end in their original order.
    /// </summary>
    /// <param name="adapters">Registered adapters.</param>
    /// <param name="channelOrder">Desired channel order.</param>
    /// <returns>Ordered sequence of adapters.</returns>
    private static IEnumerable<IChannelAdapter> ApplyChannelOrder(
        IEnumerable<IChannelAdapter> adapters,
        IReadOnlyList<string> channelOrder)
    {
        // Order by index in channelOrder; adapters outside the list get int.MaxValue (go last)
        if (channelOrder.Count is 0)
        {
            return adapters;
        }

        // The incoming channelOrder is already normalized (Trim + distinct, case-insensitive) by the caller.
        // Build a Dictionary for O(1) lookup of a channel's position.
        var orderLookup = channelOrder
            .Select((type, index) => (type, index))
            .ToDictionary(
                x => x.type,
                x => x.index,
                StringComparer.OrdinalIgnoreCase);

        return adapters.OrderBy(a =>
            orderLookup.TryGetValue(a.ChannelType, out var index) ? index : int.MaxValue);
    }
}
