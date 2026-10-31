// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The unified hop QR code of a page the core renders on the device that started the transaction — the
/// sign-in window and the channel choice page of a confirmation (SPEC-025 HOP-183, HOP-187). The one
/// decision whether such a page shows it, and the one drawing of its picture, for both pages.
/// </summary>
/// <remarks>
/// Scoped: it issues through <see cref="HopQrContent"/>, which reads the channel adapters a host may
/// narrow to the request.
/// </remarks>
internal sealed class HopUnifiedPageQr
{
    /// <summary>
    /// The fewest channels a page shows the unified QR code for: with a single channel there is no choice
    /// to make on the phone, and the QR code of that channel leads into it directly (SPEC-025 HOP-183).
    /// </summary>
    private const int MinChannelCount = 2;

    /// <summary>
    /// The one step that issues hop URLs.
    /// </summary>
    private readonly HopQrContent _hopQrContent;

    /// <summary>
    /// The one answer to whether the page keeps the per-channel QR codes beside the unified one.
    /// </summary>
    private readonly HopUnifiedMode _unifiedMode;

    /// <summary>
    /// QR code generation service.
    /// </summary>
    private readonly IQrCodeService _qrCodeService;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<HopUnifiedPageQr> _logger;

    /// <summary>
    /// Creates the unified QR code of the pages.
    /// </summary>
    /// <param name="hopQrContent">The one step that issues hop URLs.</param>
    /// <param name="unifiedMode">The one answer to whether the page keeps the per-channel QR codes.</param>
    /// <param name="qrCodeService">QR code generation service.</param>
    /// <param name="logger">Logger.</param>
    public HopUnifiedPageQr(
        HopQrContent hopQrContent,
        HopUnifiedMode unifiedMode,
        IQrCodeService qrCodeService,
        ILogger<HopUnifiedPageQr> logger)
    {
        ArgumentNullException.ThrowIfNull(hopQrContent);
        ArgumentNullException.ThrowIfNull(unifiedMode);
        ArgumentNullException.ThrowIfNull(qrCodeService);
        ArgumentNullException.ThrowIfNull(logger);

        _hopQrContent = hopQrContent;
        _unifiedMode = unifiedMode;
        _qrCodeService = qrCodeService;
        _logger = logger;
    }

    /// <summary>
    /// The picture of the unified hop QR code of a page, or null when the page keeps its per-channel
    /// layout. The page shows it when its display list holds two channels or more, its QR visibility is
    /// not <see cref="QrCodeVisibility.Never"/> — a page that shows no QR code gets the buttons alone
    /// (SPEC-025 HOP-187) — and the unified mode applies for the channel types of that list (HOP-182).
    /// </summary>
    /// <remarks>
    /// A picture that cannot be drawn, or a layout that cannot be read, leaves the page its per-channel QR
    /// codes, as a URL that cannot be issued does: hop is observability, not protection (SPEC-025 HOP-001).
    /// </remarks>
    /// <param name="transactionId">Transaction of the page.</param>
    /// <param name="transactionExpiresAt">Expiry of the transaction.</param>
    /// <param name="channels">The display list of the page — the channels an adapter gave a target for.</param>
    /// <param name="resolution">Resolution context the list was built over.</param>
    /// <param name="qrVisibility">Effective QR visibility of the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The picture of the unified QR code and the expiry of its token — the page replaces a QR code whose
    /// token expires before the transaction (SPEC-025 HOP-152) — with the layout of the page: whether it keeps
    /// the per-channel QR codes beside it and whether its "Any channel" tab shows the channel buttons; null —
    /// none on this page.
    /// </returns>
    public async ValueTask<HopUnifiedQrImage?> RenderAsync(
        TransactionId transactionId,
        DateTimeOffset transactionExpiresAt,
        IReadOnlyList<ChannelDisplayInfo> channels,
        ResolutionContext resolution,
        QrCodeVisibility qrVisibility,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(resolution);

        if (channels.Count < MinChannelCount || qrVisibility is QrCodeVisibility.Never)
        {
            return null;
        }

        var channelSet = channels.Select(channel => channel.ChannelType).ToList().AsReadOnly();

        var issued = await _hopQrContent.IssueUnifiedAsync(
            transactionId, transactionExpiresAt, channelSet, resolution, ChannelQrSurface.CorePage, cancellationToken);

        if (issued is null)
        {
            return null;
        }

        try
        {
            // No channel: the pixel scale is a per-channel dimension of its key, and the unified code names
            // none — the flat value of the level answers, as for the QR code of the page entry of a creation
            // answer.
            var qrCodeBase64 = await _qrCodeService.GenerateBase64PngAsync(issued.Url, string.Empty, resolution, cancellationToken);

            // Asked once the unified QR code is issued: only a page that shows it has its layout to choose.
            var layout = await _unifiedMode.ResolvePageLayoutAsync(resolution, cancellationToken);

            return new HopUnifiedQrImage(
                qrCodeBase64,
                issued.ExpiresAt,
                layout.WithChannelQrCodes,
                layout.AnyChannelTabShowsChannelButtons);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "The unified hop QR code could not be drawn or its layout read — the per-channel QR codes are shown");
            return null;
        }
    }
}
