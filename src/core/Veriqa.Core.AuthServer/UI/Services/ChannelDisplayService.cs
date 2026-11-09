// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.UI.Services;

/// <summary>
/// Channel data to display on the authentication page.
/// </summary>
public sealed class ChannelDisplayInfo
{
    /// <summary>
    /// Channel type (telegram, whatsapp, max).
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Link of the channel button: always the deep link the adapter gives for a button
    /// (<see cref="DeepLinkPurpose.Button"/>) to start authentication in the channel; hop never applies to it.
    /// </summary>
    public required string DeepLinkUrl { get; init; }

    /// <summary>
    /// Content of the QR code of the channel: a hop URL when hop is on for the channel, otherwise the deep
    /// link the adapter gives for a QR code that carries the link itself
    /// (<see cref="DeepLinkPurpose.DirectQr"/>), not <see cref="DeepLinkUrl"/> (SPEC-025 HOP-010); empty
    /// in a list built for <see cref="ChannelQrSurface.NotShown"/> and for a channel whose QR code could
    /// not be drawn. A hop URL is a capability of the transaction and is never written to a log.
    /// </summary>
    public required string QrContent { get; init; }

    /// <summary>
    /// Own expiry of the hop token <see cref="QrContent"/> carries when it is a hop URL (SPEC-025 HOP-149);
    /// null — the content is the deep link of the direct QR code, or the list was built for
    /// <see cref="ChannelQrSurface.NotShown"/>.
    /// A page replaces a QR code whose token expires before the transaction while it is shown (HOP-152);
    /// a renderer that does not read this value shows the QR code until its token expires.
    /// </summary>
    public DateTimeOffset? QrContentExpiresAt { get; init; }

    /// <summary>
    /// QR code as a Base64 PNG string, drawn from <see cref="QrContent"/>; empty in a list built for
    /// <see cref="ChannelQrSurface.NotShown"/> and for a channel whose QR code could not be drawn — a
    /// renderer shows such a channel by its button alone.
    /// </summary>
    public required string QrCodeBase64 { get; init; }

    /// <summary>
    /// Channel name for the button, taken from the adapter's optional
    /// <see cref="IChannelDisplayMetadata"/> (null — no metadata: built-in channels use their own
    /// localized CTA text, a third-party channel falls back to <see cref="ChannelType"/>).
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Inner markup of the channel glyph from the adapter's optional
    /// <see cref="IChannelDisplayMetadata"/> (null — render without an icon).
    /// </summary>
    public string? IconSvgPath { get; init; }

    /// <summary>
    /// Name under which the person sees the chat of the channel, ready to show (for example
    /// <c>@acme_bot</c>): the name a level of the configuration states for the channel, otherwise the
    /// one the adapter's optional <see cref="IChannelBotName"/> reports. null — neither states one, or
    /// what was stated was rejected: a text that names the bot is then worded without a name, never
    /// with the name of the channel in its place.
    /// </summary>
    public string? BotName { get; init; }

    /// <summary>
    /// Hint shown inside this channel's panel, resolved for THIS channel from
    /// <see cref="Configuration.ChannelDisplayOptions.Hints"/> (null — no level states a hint for the
    /// channel, and the panel markup is unchanged). The value is raw and may carry inline markup — the
    /// tags <c>span</c>, <c>strong</c>, <c>em</c>, <c>a</c> and <c>br</c>: a renderer that emits it
    /// either encodes it or checks it itself.
    /// </summary>
    public string? Hint { get; init; }

    /// <summary>
    /// Whether the Email channel accepts an address typed into the sign-in window (its Pull mode). The
    /// panel of an Email channel in Push mode offers the address form as a second way in only when it
    /// does; false — the form would lead to a start that refuses every request, so the panel leaves it
    /// out. Read for the Email channel only; true where the adapter does not answer the question.
    /// </summary>
    public bool EmailPullAvailable { get; init; } = true;

    /// <summary>
    /// What the mail of the Pull mode of the Email channel carries (SPEC-016 §4): the address form names its
    /// button, and the link that reveals it, by this set, and a form whose mail carries a code asks for the
    /// code on the same place once the mail is sent. The user is given one button and no choice of what the
    /// mail carries. In the order of <see cref="EmailPullMethod"/>, without repeats and never empty; read for
    /// the Email channel only, and the magic link alone where the adapter does not answer the question.
    /// </summary>
    public IReadOnlyList<EmailPullMethod> EmailPullMethods { get; init; } = MagicLinkOnly;

    /// <summary>
    /// What <see cref="EmailPullMethods"/> is where nothing states it: the mail carries the magic link.
    /// </summary>
    internal static readonly IReadOnlyList<EmailPullMethod> MagicLinkOnly = [EmailPullMethod.MagicLink];

    /// <summary>
    /// The message the person sends to the channel to start the sign-in: the short code of the
    /// transaction put into the message template the channel declared, the code written without
    /// separators — the form that goes to the clipboard. null — the channel declared no start by code,
    /// or it keeps a working link and its spare code is switched off or could not be issued. The value addresses the
    /// transaction for as long as it lives and is never written to a log.
    /// </summary>
    public string? StartMessage { get; init; }

    /// <summary>
    /// <see cref="StartMessage"/> as a person reads it: the same message with the digits of the code
    /// set in groups. null exactly when <see cref="StartMessage"/> is.
    /// </summary>
    public string? StartMessageDisplay { get; init; }

    /// <summary>
    /// Whether <see cref="StartMessage"/> is a spare way in: true — <see cref="DeepLinkUrl"/> and the QR
    /// code carry the transaction as those of any channel do, and the message is for the case the link
    /// did not start the sign-in; false — the link only opens the chat and the message is the only
    /// start. Not read while <see cref="StartMessage"/> is null.
    /// </summary>
    public bool StartMessageIsFallback { get; init; }

    /// <summary>
    /// Whether the chat of the channel is a page of the core itself — the page of a web provider channel
    /// (its adapter implements <see cref="IChannelOwnPage"/>): the outcome of the transaction is shown on
    /// that page, not in a messenger. A page that follows the transaction without taking part in it — the
    /// scanner page of the unified hop QR code — opens such a channel in its own tab (SPEC-025 HOP-186),
    /// so the phone is left with one tab when the transaction ends. false — the chat is a messenger or a
    /// mail client. Internal: the scanner page is rendered by the built-in renderer alone (SPEC-025
    /// HOP-197), so a renderer an integrator substitutes never gets a page this value acts on.
    /// </summary>
    internal bool ChatIsCorePage { get; init; }

    /// <summary>
    /// Whether the message is the only start of the channel: its link carries no transaction, so a
    /// surface that cannot show the message has no way into the channel to offer.
    /// </summary>
    internal bool StartsByMessageOnly => StartMessage is not null && !StartMessageIsFallback;
}

/// <summary>
/// Per-request display override resolved from the ui_config record (SPEC-002 §4.6, CFG-211).
/// It can only narrow the global display (filter/reorder the shown channels, switch the mode),
/// never add a channel that is not registered or not allowed by acr_values.
/// </summary>
/// <param name="Channels">Channel codes from the record; null/empty — no set/order override (global).</param>
/// <param name="Mode">Channel display mode from the record; null — the global Mode.</param>
public sealed record ChannelDisplayOverride(
    IReadOnlyList<string>? Channels,
    ChannelDisplayMode? Mode)
{
    /// <summary>
    /// The override the effective settings of a page state. The mode is a key with a core level of its
    /// own, so the chain answers it on an ordinary request and the override carries the effective mode.
    /// null is what is left of "global behavior": no level spoke at all — the core level stating a value
    /// it cannot read — and the channel set filter then resolves the mode itself.
    /// </summary>
    /// <param name="pageSettings">Effective settings of the page.</param>
    /// <returns>The override; null — none.</returns>
    internal static ChannelDisplayOverride? Of(AuthPageSettings pageSettings) =>
        pageSettings.Channels is null && pageSettings.ChannelDisplayMode is null
            ? null
            : new ChannelDisplayOverride(pageSettings.Channels, pageSettings.ChannelDisplayMode);
}

/// <summary>
/// Interface of the service that prepares channel data for the authentication page.
/// </summary>
public interface IChannelDisplayService
{
    /// <summary>
    /// Prepares channel data for display: deep links, the content of the QR codes and the QR codes.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="transactionExpiresAt">
    /// Expiry of the transaction — the upper bound of the expiry of a hop token a QR code carries.
    /// </param>
    /// <param name="allowedChannelTypes">Allowed channel types.</param>
    /// <param name="resolution">
    /// Resolution context of the request, built ONCE at the entry of the sign-in page and passed
    /// explicitly. <see cref="ResolutionContext.Core"/> outside a request — the degenerate N=1 of the
    /// same path, not a separate branch (CFG-202).
    /// </param>
    /// <param name="displayOverride">Per-request ui_config display override (null — global behavior, CFG-211).</param>
    /// <param name="qrSurface">
    /// Where the QR codes are shown; it decides the expiry of a hop token they carry (SPEC-025 HOP-153),
    /// and <see cref="ChannelQrSurface.NotShown"/> builds the list without QR codes at all.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of channel data to display.</returns>
    Task<Result<IReadOnlyList<ChannelDisplayInfo>>> GetChannelDisplayDataAsync(
        TransactionId transactionId,
        DateTimeOffset transactionExpiresAt,
        IReadOnlySet<string> allowedChannelTypes,
        ResolutionContext resolution,
        ChannelDisplayOverride? displayOverride,
        ChannelQrSurface qrSurface,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Service that prepares channel data for the authentication page (SPEC-007 §3, §4).
/// Aggregates deep links and QR codes from the adapters of the channels <see cref="ChannelSetFilter"/>
/// selects — the effective channel display mode, <see cref="ChannelDisplayOptions.ChannelOrder"/>, the
/// per-request display override and the owner of the channel registration.
/// </summary>
internal sealed class ChannelDisplayService : IChannelDisplayService
{
    /// <summary>
    /// Registered channel adapters.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// QR code generation service.
    /// </summary>
    private readonly IQrCodeService _qrCodeService;

    /// <summary>
    /// The one filter and order of the channels of a display list.
    /// </summary>
    private readonly ChannelSetFilter _channelSetFilter;

    /// <summary>
    /// Gathering point of the level-owned sign-in page settings (SPEC-012 §10.6) — the source of the
    /// effective hint of a channel. The hint is a setting owned by a level (core/tenant and below) and
    /// cut by the <c>channel</c> dimension inside it, so it is resolved for a named channel rather
    /// than read from options directly.
    /// </summary>
    private readonly AuthPageSettingsResolver _pageSettings;

    /// <summary>
    /// Configuration resolver the configured name of the chat of a channel is read by
    /// (<see cref="ChannelBotNames"/>).
    /// </summary>
    private readonly IConfigurationResolver _configurationResolver;

    /// <summary>
    /// The step that turns the deep link of a channel into the content of its QR code.
    /// </summary>
    private readonly HopQrContent _hopQrContent;

    /// <summary>
    /// Issuer of the short code of a transaction — the second address of the transaction a channel
    /// that starts by a code shows instead of, or besides, a link that carries it.
    /// </summary>
    private readonly IChannelStartCodeIssuer _startCodeIssuer;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ChannelDisplayService> _logger;

    /// <summary>
    /// Creates the channel data preparation service.
    /// </summary>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="qrCodeService">QR code generation service.</param>
    /// <param name="channelSetFilter">The one filter and order of the channels of a display list.</param>
    /// <param name="pageSettings">Resolver of the level-owned sign-in page settings.</param>
    /// <param name="configurationResolver">Configuration resolver the name of the chat is read by.</param>
    /// <param name="hopQrContent">Step that turns a deep link into the content of a QR code.</param>
    /// <param name="startCodeIssuer">Issuer of the short code of a transaction.</param>
    /// <param name="logger">Logger.</param>
    public ChannelDisplayService(
        IEnumerable<IChannelAdapter> adapters,
        IQrCodeService qrCodeService,
        ChannelSetFilter channelSetFilter,
        AuthPageSettingsResolver pageSettings,
        IConfigurationResolver configurationResolver,
        HopQrContent hopQrContent,
        IChannelStartCodeIssuer startCodeIssuer,
        ILogger<ChannelDisplayService> logger)
    {
        _adapters = adapters;
        _qrCodeService = qrCodeService;
        _channelSetFilter = channelSetFilter;
        _pageSettings = pageSettings;
        _configurationResolver = configurationResolver;
        _hopQrContent = hopQrContent;
        _startCodeIssuer = startCodeIssuer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ChannelDisplayInfo>>> GetChannelDisplayDataAsync(
        TransactionId transactionId,
        DateTimeOffset transactionExpiresAt,
        IReadOnlySet<string> allowedChannelTypes,
        ResolutionContext resolution,
        ChannelDisplayOverride? displayOverride,
        ChannelQrSurface qrSurface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        // The method collects a deep link and QR for each channel of the display list, which the one
        // filter of the channel set selects and orders (Mode/ChannelOrder and the per-request ui_config
        // display override, CFG-211).

        var result = new List<ChannelDisplayInfo>();
        var selection = await _channelSetFilter.SelectAsync(allowedChannelTypes, resolution, displayOverride, cancellationToken);

        // Every deep link and QR below is built by an adapter from ITS TENANT'S credentials, and the
        // tenant of this request is the one the resolution context states (CFG-202, CA-164). Without
        // the scope the whole page would be built from the core credentials, whoever is signing in.
        // The context is the page's own, so a request that states no tenant opens the scope for the
        // default implicit one — the self-hosted behaviour unchanged.
        using var tenantScope = ChannelTenantContext.BeginScope(resolution.TenantId);

        // One code per transaction, whichever of its channels shows it: the first channel that starts
        // by a code asks the issuer, the rest of this list reuse the answer — a refusal included.
        Result<ChannelStartCode>? startCode = null;

        foreach (var adapter in selection.Adapters)
        {
            // A third-party adapter may both fail and throw here. Neither may take the whole sign-in page
            // down: the channel is skipped exactly like a Failure, the rest render. A failure to read the
            // hop settings or to protect a hop token is treated the same way rather than falling back to
            // the deep link, which would hide the failure while hop is on. A picture that cannot be drawn
            // is not such a failure: the channel stays with its button (DrawQrCodeAsync).
            string deepLinkUrl;
            ChannelQrContent qrContent;
            string qrCodeBase64;
            bool emailPullAvailable;
            IReadOnlyList<EmailPullMethod>? emailPullMethods = null;
            ChannelStartMessageText? startMessage = null;
            var startMessageIsFallback = false;

            try
            {
                var deepLinkResult = await adapter.GetDeepLinkAsync(
                    new DeepLinkRequest { TransactionId = transactionId, Purpose = DeepLinkPurpose.Button },
                    cancellationToken);
                if (deepLinkResult.IsFailure)
                {
                    LogDeepLinkFailure(adapter.ChannelType, deepLinkResult.Error);
                    continue;
                }

                deepLinkUrl = deepLinkResult.Value;
                emailPullAvailable = adapter is not IEmailPullAvailability pull
                    || await pull.IsPullAvailableAsync(resolution, cancellationToken);
                if (emailPullAvailable && adapter is IEmailPullMethods pullMethods)
                {
                    emailPullMethods = await pullMethods.GetPullMethodsAsync(resolution, cancellationToken);
                }

                // A channel that starts by a code gets the message to send before anything of its QR
                // code is built. Where the link carries no transaction the message is the only start,
                // so a channel left without one is skipped like a channel without a link, and no hop
                // token is issued for it. A channel whose link does carry the transaction stays: the
                // link works, only the spare way in is missing.
                // The spare code of a channel whose link carries the transaction is switched by a
                // setting of the channel: switched off, the channel is treated as one that declared no
                // start by a code, and no code is asked of the issuer on its behalf.
                if (adapter.Capabilities.CodeStart is { } codeStart
                    && (!codeStart.LinkCarriesTransaction
                        || await _pageSettings.ResolveSpareStartCodeEnabledAsync(
                            resolution, adapter.ChannelType, cancellationToken)))
                {
                    startMessageIsFallback = codeStart.LinkCarriesTransaction;
                    startCode ??= await IssueStartCodeAsync(
                        transactionId, resolution.TenantId, transactionExpiresAt, cancellationToken);
                    startMessage = ComposeStartMessage(adapter.ChannelType, codeStart, startCode);

                    if (startMessage is null && !startMessageIsFallback)
                    {
                        continue;
                    }
                }

                if (qrSurface is ChannelQrSurface.NotShown)
                {
                    // A list shown as buttons only: no QR code of the channel is on the page, so no hop
                    // token is issued for one and no picture is drawn (SPEC-025 HOP-145).
                    qrContent = new ChannelQrContent(string.Empty, HopTokenExpiresAt: null);
                    qrCodeBase64 = string.Empty;
                }
                else
                {
                    // After the deep link and before the picture (SPEC-025 HOP-010): the QR code is drawn
                    // from its content, which hop may turn into a hop URL; the button keeps the deep link.
                    // Hop decides by the link of the button — the address of the channel is the same
                    // whatever the purpose of the link.
                    qrContent = await _hopQrContent.ResolveQrContentAsync(
                        new HopIssueRequest(transactionId, transactionExpiresAt, adapter.ChannelType, resolution, qrSurface),
                        deepLinkUrl,
                        cancellationToken);

                    // A QR code hop did not wrap carries the link itself, so the adapter is asked for the
                    // link of that purpose: a template the deployment states for a direct QR code reaches
                    // the picture, and the button keeps its own. The link is one fate with the channel: a
                    // failure skips the channel, as a failure of the link of the button does.
                    if (qrContent.HopTokenExpiresAt is null)
                    {
                        var directQrResult = await adapter.GetDeepLinkAsync(
                            new DeepLinkRequest { TransactionId = transactionId, Purpose = DeepLinkPurpose.DirectQr },
                            cancellationToken);
                        if (directQrResult.IsFailure)
                        {
                            LogDeepLinkFailure(adapter.ChannelType, directQrResult.Error);
                            continue;
                        }

                        qrContent = qrContent with
                        {
                            Content = SelectDirectQrLink(adapter.ChannelType, deepLinkUrl, directQrResult.Value)
                        };
                    }

                    (qrContent, qrCodeBase64) = await DrawQrCodeAsync(
                        qrContent, adapter.ChannelType, resolution, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Channel {ChannelType} failed to produce a deep link or the content of its QR code — the channel is skipped",
                    adapter.ChannelType);
                continue;
            }

            // Optional display metadata (public SPI): a third-party adapter may declare a button
            // label and a glyph. Built-in adapters do not implement it — they keep their localized
            // CTA texts and bundled icons, so behavior stays 1:1.
            var (displayName, iconSvgPath) = ReadDisplayMetadata(adapter, _logger);

            // The name of the chat, for the texts that tell the person where to send the code, by the one
            // rule the text of a question names it by too. Asked under the tenant of the resolution
            // context, the one the scope above is opened for: the name is that tenant's bot.
            var botName = await ChannelBotNames.ReadAsync(
                adapter, _configurationResolver, resolution, _logger, cancellationToken);

            // The hint is asked for THIS channel: the channel is a dimension of the key (CFG-237), so
            // the level that owns the hint of this channel answers it and the service picks nothing
            // out of a resolved map. A channel no level states a hint for gets null, and its panel is
            // rendered exactly as it is today.
            var hint = await _pageSettings.ResolveChannelHintAsync(resolution, adapter.ChannelType, cancellationToken);

            result.Add(new ChannelDisplayInfo
            {
                ChannelType = adapter.ChannelType,
                DeepLinkUrl = deepLinkUrl,
                QrContent = qrContent.Content,
                QrContentExpiresAt = qrContent.HopTokenExpiresAt,
                QrCodeBase64 = qrCodeBase64,
                DisplayName = displayName,
                IconSvgPath = iconSvgPath,
                BotName = botName,
                Hint = hint,
                EmailPullAvailable = emailPullAvailable,
                EmailPullMethods = emailPullMethods ?? ChannelDisplayInfo.MagicLinkOnly,
                StartMessage = startMessage?.Text,
                StartMessageDisplay = startMessage?.DisplayText,
                StartMessageIsFallback = startMessageIsFallback,
                ChatIsCorePage = adapter is IChannelOwnPage
            });
        }

        // If no available channel was found — return an error
        // so AuthorizeEndpoint can handle the situation correctly (503)
        if (result.Count is 0)
        {
            _logger.LogError(
                "No available channels found for transaction {TransactionId}. Adapters: {AdapterCount}, Mode: {Mode}, AllowedChannelTypes: {AllowedCount}",
                transactionId,
                _adapters.Count(),
                selection.EffectiveMode,
                allowedChannelTypes.Count);

            return Result<IReadOnlyList<ChannelDisplayInfo>>.Failure(
                ChannelDisplayErrorCodes.NoChannelsAvailable, "No authentication channels available");
        }

        return Result<IReadOnlyList<ChannelDisplayInfo>>.Success(result.AsReadOnly());
    }

    /// <summary>
    /// Writes the warning of a channel skipped because its adapter gave no deep link — one line, whichever
    /// purpose the link was asked for.
    /// </summary>
    /// <param name="channelType">Type of the channel.</param>
    /// <param name="error">Error the adapter returned.</param>
    private void LogDeepLinkFailure(string channelType, TransactionError error) =>
        _logger.LogWarning(
            "Failed to get a deep link for channel {ChannelType}: {Error}",
            channelType,
            error.Message);

    /// <summary>
    /// The link a QR code hop did not wrap carries: the link the adapter gave for a direct QR code, when it
    /// is of the form of the link of the button. Hop and the panel of the channel were chosen by the form
    /// of the link of the button — an address of the channel or the path of a page of the core — so a link
    /// that is empty or of the other form would make the picture disagree with them: the Email adapter
    /// whose Push mail for a direct QR code was not built answers with its Pull path beside a
    /// <c>mailto:</c> button. Such a link is not drawn; the QR code carries the link of the button, and
    /// the deviation is written to the log at the Debug level: the line repeats on every build of the
    /// page while the configuration stays wrong, and the Email adapter has already written the cause.
    /// </summary>
    /// <param name="channelType">Channel type, for the log.</param>
    /// <param name="buttonLink">Link of the channel button.</param>
    /// <param name="directQrLink">Link the adapter gave for a direct QR code.</param>
    /// <returns>The link the QR code carries.</returns>
    private string SelectDirectQrLink(string channelType, string buttonLink, string? directQrLink)
    {
        if (!string.IsNullOrWhiteSpace(directQrLink)
            && ChannelAddress.IsAbsoluteChannelAddress(directQrLink) == ChannelAddress.IsAbsoluteChannelAddress(buttonLink))
        {
            return directQrLink;
        }

        _logger.LogDebug(
            "Channel {ChannelType} gave a direct QR link that is empty or not of the form of its button link — the QR code carries the button link",
            channelType);

        return buttonLink;
    }

    /// <summary>
    /// Draws the QR code of a channel from its content. A picture that cannot be drawn — the encoder
    /// rejects a content longer than a QR code holds, and the content of a channel may be a whole
    /// prefilled mail — leaves the channel without a QR code and with its button: the content and the
    /// picture come back empty, as in a list built for <see cref="ChannelQrSurface.NotShown"/>.
    /// </summary>
    /// <remarks>
    /// The log line names the type of the fault and nothing the fault says: the content is a capability
    /// of the transaction.
    /// </remarks>
    /// <param name="qrContent">Content of the QR code.</param>
    /// <param name="channelType">Type of the channel.</param>
    /// <param name="resolution">Resolution context of the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The content with its picture, or the empty pair when the picture was not drawn.</returns>
    private async Task<(ChannelQrContent Content, string QrCodeBase64)> DrawQrCodeAsync(
        ChannelQrContent qrContent,
        string channelType,
        ResolutionContext resolution,
        CancellationToken cancellationToken)
    {
        try
        {
            return (
                qrContent,
                await _qrCodeService.GenerateBase64PngAsync(qrContent.Content, channelType, resolution, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "The QR code of channel {ChannelType} was not drawn ({ExceptionType}) — the channel is shown without a QR code",
                channelType,
                ex.GetType().Name);

            return (new ChannelQrContent(string.Empty, HopTokenExpiresAt: null), string.Empty);
        }
    }

    /// <summary>
    /// Asks the issuer for the code of the transaction. A fault of the code storage is turned into a
    /// refusal here rather than left to the caller: the caller skips a channel on any exception, and a
    /// channel whose link carries the transaction must stay on the page without its spare code.
    /// </summary>
    /// <remarks>
    /// The log line names the type of the fault and nothing the fault says: what a storage reports about
    /// a record it failed to read or write may quote the code, and the code addresses the transaction.
    /// </remarks>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="tenantId">Tenant of the transaction — the tenant the resolution context of the page
    /// states, which every caller builds from the transaction or from the client it is created for.</param>
    /// <param name="transactionExpiresAt">Expiry of the transaction — the expiry of its code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The code, or the refusal to issue one.</returns>
    private async Task<Result<ChannelStartCode>> IssueStartCodeAsync(
        TransactionId transactionId,
        string? tenantId,
        DateTimeOffset transactionExpiresAt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _startCodeIssuer.GetOrIssueAsync(
                transactionId, tenantId, transactionExpiresAt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "The storage of start codes failed ({ExceptionType}) for transaction {TransactionId} — no channel of this page gets a start code",
                ex.GetType().Name,
                transactionId);

            return Result<ChannelStartCode>.Failure(
                ChannelAdapterErrorCodes.StartCodeIssueFailed, "The storage of start codes failed");
        }
    }

    /// <summary>
    /// Builds the message of the start by a code for one channel and reports why there is none. The
    /// code itself is never part of a log line.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <param name="codeStart">The start by a code as the channel declared it.</param>
    /// <param name="startCode">The code of the transaction, or the refusal to issue one.</param>
    /// <returns>The message; null — no code was issued, or the declared template cannot carry one.</returns>
    private ChannelStartMessageText? ComposeStartMessage(
        string channelType,
        ChannelCodeStart codeStart,
        Result<ChannelStartCode> startCode)
    {
        if (startCode.IsFailure)
        {
            _logger.LogWarning(
                "No start code for channel {ChannelType}: {Error}",
                channelType,
                startCode.Error.Message);
            return null;
        }

        var message = ChannelStartMessage.Compose(codeStart, startCode.Value.Code);
        if (message is null)
        {
            _logger.LogWarning(
                "Channel {ChannelType} declared a start message template that cannot carry the code: it must " +
                "hold the code placeholder exactly once and be at most {Limit} characters long",
                channelType,
                ChannelCodeStart.MaxMessageTemplateLength);
        }

        return message;
    }

    /// <summary>
    /// Reads the optional display metadata of a third-party adapter (public SPI) and degrades it to
    /// safe values: a blank label becomes null (the caller falls back to the channel type — an empty
    /// string would render a nameless tab and button), an over-long label or glyph is dropped
    /// (an unbounded label stretches the CTA past the card, an unbounded glyph bloats every page
    /// render and its allowlist scan), and glyph markup that fails the allowlist is dropped as well
    /// (the channel renders without an icon). The adapter's properties are third-party code
    /// on the sign-in page path, so a throwing getter must not take the whole page down either —
    /// including a spurious timeout, which is why the catch is unfiltered (there is no cancellation
    /// token on this path, so no exception here can be a real cancellation of an awaited operation).
    /// <para>
    /// Shared with the hop launch page, which names the same channel on its button: a label this
    /// reading rejects is rejected there as well.
    /// </para>
    /// </summary>
    /// <param name="adapter">Channel adapter.</param>
    /// <param name="logger">Logger a rejected value is reported to.</param>
    /// <returns>Display label and glyph markup; null for each value that is absent or rejected.</returns>
    internal static (string? DisplayName, string? IconSvgPath) ReadDisplayMetadata(
        IChannelAdapter adapter,
        ILogger logger)
    {
        // The one reading of the metadata, the text of a question included: a label the pages show is the
        // label the question names the channel by, and a failure of either property drops both everywhere.
        var (displayName, iconSvgPath) = ChannelNames.ReadDeclaredMetadata(adapter, logger);

        if (iconSvgPath is not null && iconSvgPath.Length > CustomChannelConstants.MaxIconSvgPathLength)
        {
            logger.LogWarning(
                "Channel {ChannelType} declared glyph markup of {Length} characters, over the " +
                "{Limit}-character limit — rendering without an icon",
                adapter.ChannelType,
                iconSvgPath.Length,
                CustomChannelConstants.MaxIconSvgPathLength);
            iconSvgPath = null;
        }

        if (iconSvgPath is not null && !ChannelIconSvg.IsValidCustomPath(iconSvgPath))
        {
            logger.LogWarning(
                "Channel {ChannelType} declared glyph markup that is not allowed on the sign-in page " +
                "(only plain SVG shape elements with quoted attributes) — rendering without an icon",
                adapter.ChannelType);
            iconSvgPath = null;
        }

        return (displayName, iconSvgPath);
    }
}
