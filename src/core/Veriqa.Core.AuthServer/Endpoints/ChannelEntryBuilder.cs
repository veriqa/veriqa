// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// The entries of a creation answer: the single way in and, when requested, the list of deep-link
/// entries of the channels offered (SPEC-039 C20, C52).
/// </summary>
/// <param name="Entry">The single way in, chosen by L40.</param>
/// <param name="Entries">The list of channel entries; null when the request did not ask for it.</param>
internal sealed record ChannelEntrySet(ChannelEntry Entry, IReadOnlyList<ChannelEntry>? Entries);

/// <summary>
/// Builds the way in that the creation answer carries (SPEC-039 L40, C20), and the list of channel
/// entries when the request asks for it (C52). Both are built FROM THE TRANSACTION at the moment of the
/// answer and stored nowhere: an idempotent repeat therefore gets current entries rather than the ones
/// frozen at the first attempt.
/// </summary>
internal static class ChannelEntryBuilder
{
    /// <summary>
    /// Login confirmation AS THE PRODUCT SHIPS IT — a default-constructed options object, read for one
    /// thing only: the mode a channel gets when no level states one. The shipped value is taken off the
    /// options class rather than restated here, so the two cannot drift.
    /// </summary>
    private static readonly LoginConfirmationOptions Shipped = new();

    /// <summary>
    /// Builds the channel entries of a transaction.
    /// </summary>
    /// <param name="transaction">Transaction the entries lead to.</param>
    /// <param name="includeChannelEntries">Whether the list of channel entries is built besides the
    /// single way in (C52).</param>
    /// <param name="request">Current HTTP request — the fallback source of the public base address.</param>
    /// <param name="resolution">Ownership context of the transaction.</param>
    /// <param name="channelDisplayService">Preparer of the per-channel entry material.</param>
    /// <param name="hopQrContent">The one step that issues the unified hop URL.</param>
    /// <param name="pageSettingsResolver">Resolver of the level-owned page settings (display override).</param>
    /// <param name="qrMatrixSource">Server-side QR generator — the module matrix and pixel scale the
    /// image of the answer is drawn from.</param>
    /// <param name="configResolver">Canonical level resolver — the source of the confirmation surface.</param>
    /// <param name="serverOptions">OIDC server options — the source of the stated issuer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entries, or the failure of the channel preparation.</returns>
    public static async Task<Result<ChannelEntrySet>> BuildAsync(
        Transaction transaction,
        bool includeChannelEntries,
        HttpRequest request,
        ResolutionContext resolution,
        IChannelDisplayService channelDisplayService,
        HopQrContent hopQrContent,
        AuthPageSettingsResolver pageSettingsResolver,
        IQrModuleMatrixSource qrMatrixSource,
        IConfigurationResolver configResolver,
        OidcServerOptions serverOptions,
        CancellationToken cancellationToken)
    {
        // The method picks the kind of the single entry per L40 and fills it; on request it also lists
        // the deep-link entries of every channel of the same display list (C52)

        // The effective set is what the transaction itself states: a preferred channel narrows it to
        // one, otherwise the allowed set applies (an empty one means "every registered channel").
        // Narrowing to the preferred channel is sound because a transaction never carries one outside
        // its own allowed set: a request stating such a pair is refused at creation, so the entry
        // built here can never lead into a channel whose answer would later be refused.
        IReadOnlySet<string> effectiveChannels = string.IsNullOrWhiteSpace(transaction.RequestedChannelType)
            ? transaction.AllowedChannelTypes
            : new HashSet<string>(StringComparer.Ordinal) { transaction.RequestedChannelType };

        var pageSettings = await pageSettingsResolver.ResolveAsync(resolution, cancellationToken);

        var displayOverride = ChannelDisplayOverride.Of(pageSettings);

        // The creation answer is its own projection of the channel QR codes: a hop token it carries lives
        // as long as the transaction (SPEC-025 HOP-153).
        var displayResult = await channelDisplayService.GetChannelDisplayDataAsync(
            transaction.Id,
            transaction.ExpiresAt,
            effectiveChannels,
            resolution,
            displayOverride,
            ChannelQrSurface.CreationAnswer,
            cancellationToken);

        if (displayResult.IsFailure)
        {
            // Not a single channel offered a way in — the preferred one is not registered or not
            // allowed, or the only one failed to build its link. There is nothing to show, and the page
            // is not an answer either: it would open onto the same empty set.
            return Result<ChannelEntrySet>.Failure(
                displayResult.Error.Code, displayResult.Error.Message, displayResult.Error.Category);
        }

        var channels = displayResult.Value;

        // The list of C52 takes the second and the third condition of L40 and NOT the first: it is the
        // very form for several channels. It keeps the channels with an addressable point whose question
        // is not asked on the core's page, in the order of the display list the configuration gave. A
        // channel whose link only opens the chat is not such a point either: its start is a message with
        // a code, the answer has no field for one, and the link alone starts nothing — the page of the
        // core is where the code is shown.
        List<ChannelEntry>? entries = null;
        if (includeChannelEntries)
        {
            entries = [];
            foreach (var channel in channels)
            {
                if (LeadsIntoChannel(channel) && !await AsksOnCorePageAsync(channel))
                {
                    entries.Add(await BuildDeepLinkEntryAsync(channel));
                }
            }
        }

        // The second condition is read off the deep link itself rather than off a list of channels: a
        // channel that has no addressable point until the user has typed something answers with a
        // relative start path (the Email adapter in Pull mode does exactly that). A list built above has
        // already asked the only channel the same filters, and its outcome is the entry it holds or not: the
        // channel is not asked twice and the image is not drawn a second time.
        var single = channels.Count is 1 ? channels[0] : null;

        if (single is not null
            && (entries is not null
                ? entries.Count is 1
                : LeadsIntoChannel(single) && !await AsksOnCorePageAsync(single)))
        {
            var deepLinkEntry = entries is [var listed] ? listed : await BuildDeepLinkEntryAsync(single);

            return Result<ChannelEntrySet>.Success(new ChannelEntrySet(deepLinkEntry, entries));
        }

        var pageUrl = BuildEntryPageUrl(transaction, request, serverOptions);

        // The QR code of the page entry carries the unified hop URL when the unified mode applies for the
        // channel set of this answer, whatever the number of its channels (SPEC-025 HOP-183): the phone
        // that scans it gets the same channel choice page the url opens. The url and the expiry of the
        // entry do not change; the token lives as long as the transaction (HOP-153).
        var unifiedHopUrl = await hopQrContent.IssueUnifiedAsync(
            transaction.Id,
            transaction.ExpiresAt,
            channels.Select(channel => channel.ChannelType).ToList().AsReadOnly(),
            resolution,
            ChannelQrSurface.CreationAnswer,
            cancellationToken);

        var pageEntry = new ChannelEntry
        {
            Kind = ChannelEntryKinds.PageUrl,
            // Strictly null: the channel is not chosen yet, and naming one here would be a guess.
            ChannelType = null,
            DisplayName = null,
            Url = pageUrl,
            // No channel: the pixel scale is a per-channel dimension of its key, and an unnamed
            // channel matches no per-channel step — the flat value of the level answers.
            Qr = await RenderQrAsync(unifiedHopUrl?.Url ?? pageUrl, string.Empty),
            ValidUntil = transaction.ExpiresAt
        };

        return Result<ChannelEntrySet>.Success(new ChannelEntrySet(pageEntry, entries));

        // The third condition of L40, and the one that beats the other two: with the question of a
        // channel configured onto the core's own page, its deep link would send the user into a channel
        // where no prompt is ever going to appear, and the transaction would hang to its TTL. The mode is
        // a per-channel setting (CFG-237), so it is asked about the channel of the entry. It is read from
        // the CONFIGURED value of the axis — the surface effective for a channel is not known before a
        // channel has acted; a chain no level answers is the mode the product ships with.
        async Task<bool> AsksOnCorePageAsync(ChannelDisplayInfo channel)
        {
            var resolved = await configResolver.ResolveAsync(
                LoginConfirmationConfigKeys.LoginConfirmation,
                resolution,
                ConfigDimensionValues.Of((ConfigDimensionNames.Channel, channel.ChannelType)),
                cancellationToken);

            var mode = resolved.SourceLevel is null ? Shipped.Mode : resolved.Value;

            return mode is LoginConfirmationMode.OnWebPage;
        }

        // A deep-link entry of one channel — the form shared by the single way in and the list (C20, C52).
        // The url is always the deep link of the button; the QR code carries a hop URL when hop is on for
        // the channel, and the link of a direct QR code otherwise.
        async Task<ChannelEntry> BuildDeepLinkEntryAsync(ChannelDisplayInfo channel) =>
            new ChannelEntry
            {
                Kind = ChannelEntryKinds.DeepLink,
                ChannelType = channel.ChannelType,
                DisplayName = ChannelNames.GetChannelName(channel.ChannelType, channel.DisplayName),
                Url = channel.DeepLinkUrl,
                // The PNG prepared for the sign-in page (channel.QrCodeBase64) does not go out: the image
                // of the answer is drawn again over the same QR content and channel, with the mark. A
                // channel whose QR code could not be drawn comes with no content, and its entry carries
                // the address alone.
                Qr = string.IsNullOrEmpty(channel.QrContent)
                    ? null
                    : await RenderQrAsync(channel.QrContent, channel.ChannelType),
                ValidUntil = transaction.ExpiresAt
            };

        // The image of the answer leaves for a screen of the integrator, where no footer of the core
        // exists, so it carries the attribution mark in its pixels (SPEC-015 §4.18, SPEC-039 C20). It is
        // drawn HERE, from the module matrix, rather than inside the QR service: that service also feeds
        // the sign-in page, whose footer already shows the mark, and a mark in its picture would repeat it.
        async Task<string> RenderQrAsync(string content, string channelType)
        {
            var matrix = await qrMatrixSource.GenerateModuleMatrixAsync(
                content,
                channelType,
                resolution,
                cancellationToken);

            var png = QrAttributionImage.ComposePng(matrix);

            return AuthPageConstants.PngDataUriPrefix + Convert.ToBase64String(png);
        }
    }

    /// <summary>
    /// Tells whether the link of a channel is a way in the answer may hand out by itself: an absolute
    /// address that carries the transaction. A relative start path has no addressable point, and the link
    /// of a channel that starts by a message with a code opens a chat in which nothing has started.
    /// </summary>
    /// <param name="channel">Channel display data.</param>
    /// <returns>true — the link goes out as a deep-link entry.</returns>
    private static bool LeadsIntoChannel(ChannelDisplayInfo channel) =>
        !channel.StartsByMessageOnly && ChannelAddress.IsAbsoluteChannelAddress(channel.DeepLinkUrl);

    /// <summary>
    /// Builds the absolute URL of the entry page of a transaction.
    /// </summary>
    /// <remarks>
    /// One rule with a deterministic order: the issuer the operator stated is the public address of
    /// the server and wins; without one, the address the current request arrived at is used — behind a
    /// reverse proxy the already wired-up forwarded-headers middleware has corrected it by then. No
    /// parsing of <c>X-Forwarded-*</c> here and no "public base address" option of our own. The stated
    /// issuer is brought to its base the way the base of a hop URL is (surrounding whitespace and a
    /// trailing slash removed), so one issuer gives one base to every URL built over it.
    /// </remarks>
    /// <param name="transaction">Transaction the page addresses.</param>
    /// <param name="request">Current HTTP request.</param>
    /// <param name="serverOptions">OIDC server options.</param>
    /// <returns>Absolute URL of the entry page.</returns>
    private static string BuildEntryPageUrl(
        Transaction transaction,
        HttpRequest request,
        OidcServerOptions serverOptions)
    {
        var issuer = serverOptions.Issuer;
        var baseAddress = string.IsNullOrWhiteSpace(issuer)
            ? $"{request.Scheme}://{request.Host}{request.PathBase}"
            : issuer.Trim().TrimEnd('/');

        var sessionId = Uri.EscapeDataString(SessionIdMapper.ToSessionId(transaction.Id));

        return $"{baseAddress}{OidcEndpoints.TransactionEntryPage}"
            + $"?{OidcConstants.SessionIdParameterName}={sessionId}";
    }
}
