// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The one step that turns the deep link of a channel into the content of its QR code (SPEC-025 HOP-010):
/// a hop URL when hop is on for the channel — by its switch, or by its declaration when its link carries
/// no transaction (<see cref="HopChannelModes"/>) — the deep link itself otherwise. Both projections of a
/// channel QR code — the pages the core renders and the server-to-server creation answer — take their
/// content from here, so they cannot part. Hop is a property of the QR code alone: the button of a
/// channel and the <c>url</c> of a creation answer keep the deep link.
/// </summary>
/// <remarks>
/// A hop token and a hop URL are a capability of the transaction: nothing here writes either of them to
/// a log, at any level. Scoped: whether the unified QR code applies is answered by
/// <see cref="HopUnifiedMode"/>, which reads the channel adapters a host may narrow to the request.
/// </remarks>
internal sealed class HopQrContent
{
    /// <summary>
    /// The single reading of the effective hop settings.
    /// </summary>
    private readonly HopModeSettings _settings;

    /// <summary>
    /// The one answer to the hop mode of a channel.
    /// </summary>
    private readonly HopChannelModes _channelModes;

    /// <summary>
    /// The one answer to whether the unified hop QR code applies.
    /// </summary>
    private readonly HopUnifiedMode _unifiedMode;

    /// <summary>
    /// Codec of hop tokens.
    /// </summary>
    private readonly HopTokenCodec _codec;

    /// <summary>
    /// Clock the moment of issue is read from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<HopQrContent> _logger;

    /// <summary>
    /// Creates the step.
    /// </summary>
    /// <param name="settings">Reading of the effective hop settings.</param>
    /// <param name="channelModes">The one answer to the hop mode of a channel.</param>
    /// <param name="unifiedMode">The one answer to whether the unified hop QR code applies.</param>
    /// <param name="codec">Codec of hop tokens.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public HopQrContent(
        HopModeSettings settings,
        HopChannelModes channelModes,
        HopUnifiedMode unifiedMode,
        HopTokenCodec codec,
        TimeProvider timeProvider,
        ILogger<HopQrContent> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(channelModes);
        ArgumentNullException.ThrowIfNull(unifiedMode);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _channelModes = channelModes;
        _unifiedMode = unifiedMode;
        _codec = codec;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Issues a hop token and builds its hop URL over the base and the path of the given mode — the part
    /// shared by the per-channel and the unified mode. It neither asks whether hop is on nor reads the mode
    /// of a channel: the caller has decided both. The token's own expiry follows the projection
    /// (SPEC-025 HOP-149, HOP-153): the creation answer — the expiry of the transaction; a core page —
    /// the configured lifetime of a token from now, never past the expiry of the transaction, or the
    /// expiry of the transaction when no lifetime is configured.
    /// </summary>
    /// <param name="request">What the token is issued for; an empty channel type issues the unified token.</param>
    /// <param name="mode">Hop mode whose base and path the URL is built over; its switch is not consulted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hop URL and the token's own expiry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="mode"/> is null.</exception>
    /// <exception cref="ArgumentException">The mode states no base of hop URLs.</exception>
    public async ValueTask<HopIssuedUrl> IssueHopUrlAsync(
        HopIssueRequest request,
        HopChannelMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mode);

        if (mode.BaseUrl is null)
        {
            throw new ArgumentException("A hop URL cannot be built without a base of hop URLs.", nameof(mode));
        }

        var issuedAt = _timeProvider.GetUtcNow();
        var expiresAt = await ResolveTokenExpiryAsync(request, issuedAt, cancellationToken);

        var token = _codec.Encode(new HopToken(request.TransactionId, request.ChannelType, issuedAt, expiresAt));

        // The base and the path already come in their one form (no trailing slash, one leading slash),
        // so they are joined as they are — the route of the resolve endpoint is built over the same path.
        var url = $"{mode.BaseUrl}{mode.HopPath}{HopUrlForm.PathSeparator}{token}";

        return new HopIssuedUrl(url, expiresAt);
    }

    /// <summary>
    /// Issues the unified hop URL of a transaction — the one QR code of the transaction, whose token names
    /// no channel (SPEC-025 HOP-183) — when the unified mode applies for the channel set of the caller
    /// (HOP-182). The token's own expiry follows the projection, as for a per-channel token (HOP-153).
    /// </summary>
    /// <remarks>
    /// Hop is observability, not protection (SPEC-025 HOP-001): a unified URL that cannot be issued leaves
    /// the caller its per-channel QR codes rather than failing it. So a failure to read the settings or to
    /// protect the token is caught here and reported without the URL or the token.
    /// </remarks>
    /// <param name="transactionId">Transaction the token names.</param>
    /// <param name="transactionExpiresAt">Expiry of the transaction — the upper bound of the token's own.</param>
    /// <param name="channelSet">
    /// The channel set the mode is judged by: the channel types of the display list the caller has built.
    /// </param>
    /// <param name="resolution">Resolution context of the act.</param>
    /// <param name="surface">Where the QR code is shown; <see cref="ChannelQrSurface.NotShown"/> is refused.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The unified hop URL and its expiry; null — the unified mode does not apply, no base of hop URLs is
    /// effective, or the issuance failed (the last two with a warning).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="channelSet"/> or <paramref name="resolution"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="surface"/> shows no QR code.</exception>
    public async ValueTask<HopIssuedUrl?> IssueUnifiedAsync(
        TransactionId transactionId,
        DateTimeOffset transactionExpiresAt,
        IReadOnlyList<string> channelSet,
        ResolutionContext resolution,
        ChannelQrSurface surface,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channelSet);
        ArgumentNullException.ThrowIfNull(resolution);

        if (surface is ChannelQrSurface.NotShown)
        {
            throw new ArgumentException("A list shown without QR codes carries no unified QR code.", nameof(surface));
        }

        try
        {
            var mode = await _unifiedMode.ResolveAsync(resolution, channelSet, cancellationToken);
            if (mode is null)
            {
                return null;
            }

            if (mode.BaseUrl is null)
            {
                _logger.LogWarning(
                    "The unified hop QR code applies, but no effective HTTPS base of hop URLs is stated — the "
                    + "issuer is not an absolute HTTPS URL; the per-channel QR codes are shown");
                return null;
            }

            return await IssueHopUrlAsync(
                new HopIssueRequest(transactionId, transactionExpiresAt, string.Empty, resolution, surface),
                mode,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "The unified hop URL could not be issued — the per-channel QR codes are shown");
            return null;
        }
    }

    /// <summary>
    /// The content of the QR code of one channel (SPEC-025 HOP-010, HOP-012): a hop URL when hop is on for
    /// the channel, the deep link unchanged otherwise. The decision is per channel, by the effective switch
    /// alone — the same for built-in and third-party adapters and whatever the length of the deep link
    /// (SPEC-025 HOP-051). The one channel the switch does not decide for is the channel whose link carries
    /// no transaction: its QR code is a hop URL with the switch off as well, since the page of the resolve
    /// endpoint is where the phone that scanned it is shown the message to send.
    /// </summary>
    /// <remarks>
    /// The deep link stays the content when the address has no addressable point (such a QR code is shown
    /// nowhere), when hop is off for the channel, and when no base of hop URLs is effective for the act —
    /// the last with a warning, since hop was asked for and cannot be given. A failure of the reading of
    /// the settings or of the protection of the token is not caught here: the caller skips the channel,
    /// as it does for a failing adapter, rather than quietly showing the deep link while hop is on.
    /// </remarks>
    /// <param name="request">What the token would be issued for; the channel type must name a channel.</param>
    /// <param name="deepLink">Deep link the adapter of the channel returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Content of the QR code, with the own expiry of the hop token when the content is a hop URL — what a
    /// page replaces the QR code by before the token expires (SPEC-025 HOP-152).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="deepLink"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The channel type is empty — the unified token is issued through <see cref="IssueHopUrlAsync"/> alone.
    /// </exception>
    public async ValueTask<ChannelQrContent> ResolveQrContentAsync(
        HopIssueRequest request,
        string deepLink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(deepLink);

        var deepLinkContent = new ChannelQrContent(deepLink, HopTokenExpiresAt: null);

        if (!ChannelAddress.IsAbsoluteChannelAddress(deepLink))
        {
            return deepLinkContent;
        }

        var mode = await _channelModes.ResolveAsync(request.Resolution, request.ChannelType, cancellationToken);

        if (!mode.IsEnabled)
        {
            return deepLinkContent;
        }

        if (mode.BaseUrl is null)
        {
            _logger.LogWarning(
                "Hop is on for channel {ChannelType}, but no effective HTTPS base of hop URLs is stated — "
                + "the issuer is not an absolute HTTPS URL; the QR code carries the deep link",
                request.ChannelType);
            return deepLinkContent;
        }

        var issued = await IssueHopUrlAsync(request, mode, cancellationToken);

        return new ChannelQrContent(issued.Url, issued.ExpiresAt);
    }

    /// <summary>
    /// Own expiry of a token issued now for the projection of the request. The lifetime setting is read
    /// only for a core page: the creation answer ties the token to the transaction whatever it states.
    /// </summary>
    /// <param name="request">What the token is issued for.</param>
    /// <param name="issuedAt">Moment of issue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Expiry of the token; never later than the expiry of the transaction.</returns>
    private async ValueTask<DateTimeOffset> ResolveTokenExpiryAsync(
        HopIssueRequest request,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken)
    {
        if (request.Surface is ChannelQrSurface.CreationAnswer)
        {
            return request.TransactionExpiresAt;
        }

        var issuance = await _settings.ResolveIssuanceAsync(request.Resolution, cancellationToken);

        // A lifetime reaching past the transaction — or a transaction already over — leaves the token the
        // expiry of the transaction: it is issued all the same, and the resolve endpoint judges it. The lifetime
        // is compared with the time left rather than added to the moment of issue: only its lower bound is
        // validated, and a lifetime near TimeSpan.MaxValue would overflow the sum.
        return issuance.TokenLifetime is { } lifetime && lifetime < request.TransactionExpiresAt - issuedAt
            ? issuedAt + lifetime
            : request.TransactionExpiresAt;
    }
}
