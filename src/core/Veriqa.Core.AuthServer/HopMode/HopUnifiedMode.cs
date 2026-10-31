// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The one answer to "does the unified hop QR code apply" (SPEC-025 HOP-182) and the channel set it is
/// judged by — for every consumer: the resolve of a unified token, its issuance on the pages and in the
/// creation answer, and the receiver of its client signals — and to "how does a page of the unified QR code
/// lay out its channels". No other place reads <see cref="HopQrMode"/> or combines it with the
/// per-channel switch.
/// </summary>
/// <remarks>
/// It writes nothing and asks no adapter for a target. Scoped: the channel set is selected over the
/// channel adapters, whose lifetime a host may narrow to the request.
/// </remarks>
internal sealed class HopUnifiedMode
{
    /// <summary>
    /// The one filter and order of the channels of a display list.
    /// </summary>
    private readonly ChannelSetFilter _channelSetFilter;

    /// <summary>
    /// Resolver of the level-owned page settings — the source of the ui_config display override.
    /// </summary>
    private readonly AuthPageSettingsResolver _pageSettings;

    /// <summary>
    /// The single reading of the effective hop settings.
    /// </summary>
    private readonly HopModeSettings _hopModeSettings;

    /// <summary>
    /// Creates the unified mode.
    /// </summary>
    /// <param name="channelSetFilter">The one filter and order of the channels of a display list.</param>
    /// <param name="pageSettings">Resolver of the level-owned page settings.</param>
    /// <param name="hopModeSettings">The single reading of the effective hop settings.</param>
    public HopUnifiedMode(
        ChannelSetFilter channelSetFilter,
        AuthPageSettingsResolver pageSettings,
        HopModeSettings hopModeSettings)
    {
        ArgumentNullException.ThrowIfNull(channelSetFilter);
        ArgumentNullException.ThrowIfNull(pageSettings);
        ArgumentNullException.ThrowIfNull(hopModeSettings);

        _channelSetFilter = channelSetFilter;
        _pageSettings = pageSettings;
        _hopModeSettings = hopModeSettings;
    }

    /// <summary>
    /// The channel set of a transaction at the moment of the call: the channel types of its display list
    /// before any adapter is asked for a target — the filter and order of the channel choice page, over the
    /// allowed channel types of the transaction and the ui_config display override of its ownership
    /// context. Computed anew on every call.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The channel types, in display order; empty — no channel is left.</returns>
    public async ValueTask<IReadOnlyList<string>> ResolveChannelSetAsync(
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var resolution = TransactionResolutionContext.For(transaction);
        var pageSettings = await _pageSettings.ResolveAsync(resolution, cancellationToken);

        var selection = await _channelSetFilter.SelectAsync(
            transaction.AllowedChannelTypes,
            resolution,
            ChannelDisplayOverride.Of(pageSettings),
            cancellationToken);

        return selection.Adapters
            .Select(adapter => adapter.ChannelType)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Whether the unified hop QR code applies for an act (SPEC-025 HOP-182): the QR mode of the act is
    /// <see cref="HopQrMode.Unified"/> or <see cref="HopQrMode.UnifiedWithChannels"/> and hop is on for at
    /// least one channel of the set. The channels are asked in the order of the set, up to the first one hop
    /// is on for.
    /// </summary>
    /// <remarks>
    /// A failure of the resolution of the settings is not caught: the caller decides what it means.
    /// </remarks>
    /// <param name="context">Resolution context of the act.</param>
    /// <param name="channelSet">The channel set the act is judged by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The hop mode of the first channel hop is on for — the issuance takes the base and the path of hop
    /// URLs from it, which do not depend on the channel; null — the unified mode does not apply: the QR
    /// mode is per channel, the set is empty, or hop is on for no channel of it.
    /// </returns>
    public async ValueTask<HopChannelMode?> ResolveAsync(
        ResolutionContext context,
        IReadOnlyList<string> channelSet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(channelSet);

        if (channelSet.Count is 0)
        {
            return null;
        }

        var issuance = await _hopModeSettings.ResolveIssuanceAsync(context, cancellationToken);
        if (issuance.QrMode is not (HopQrMode.Unified or HopQrMode.UnifiedWithChannels))
        {
            return null;
        }

        foreach (var channelType in channelSet)
        {
            var channelMode = await _hopModeSettings.ResolveChannelAsync(context, channelType, cancellationToken);
            if (channelMode.IsEnabled)
            {
                return channelMode;
            }
        }

        return null;
    }

    /// <summary>
    /// How a page that shows the unified hop QR code lays out its channels: whether it keeps the per-channel QR
    /// codes as well — a tab with the unified QR code followed by a tab per channel: the QR mode of the act is
    /// <see cref="HopQrMode.UnifiedWithChannels"/> — and whether the "Any channel" tab of such a page shows the
    /// buttons of the channels (SPEC-025 HOP-187). Both come from one resolution of the issuance settings.
    /// Asked only of a page the unified QR code was issued for (<see cref="ResolveAsync"/>); for any other the
    /// answer means nothing.
    /// </summary>
    /// <remarks>
    /// A failure of the resolution of the settings is not caught: the caller decides what it means.
    /// </remarks>
    /// <param name="context">Resolution context of the act.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The layout of the page.</returns>
    public async ValueTask<HopUnifiedPageLayout> ResolvePageLayoutAsync(
        ResolutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var issuance = await _hopModeSettings.ResolveIssuanceAsync(context, cancellationToken);

        return new HopUnifiedPageLayout(
            issuance.QrMode is HopQrMode.UnifiedWithChannels,
            issuance.ShowChannelButtonsOnAnyChannelTab);
    }
}
