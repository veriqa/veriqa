// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Buffers;
using System.Globalization;
using System.Text;
using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The presentation of a hop URL — <c>GET {HopPath}/{hop_token}</c> (SPEC-025 HOP-060, HOP-074): the link
/// a phone opens after scanning the QR code of a channel. The endpoint checks the token and the
/// transaction it names and answers by the answer matrix; the only writes it makes to the transaction are
/// the one-time observations of a person's presentation (HOP-188). A channel whose link carries no
/// transaction is answered with the page of its start message in place of the launch page and of the
/// redirect. The Email channel is taken to its mail, never to a page of the channel that would open it:
/// the mail is its target under the redirect style, as the target of any other channel is, except that
/// while it accepts a typed address the page of the style <c>Interstitial</c> is the page of the channel
/// buttons laid out for it alone, which opens the mail by itself.
/// </summary>
internal static class HopResolveEndpoint
{
    /// <summary>
    /// The longest UTF-8 form of one Unicode scalar value, in bytes.
    /// </summary>
    private const int MaxUtf8BytesPerRune = 4;

    /// <summary>
    /// The prefix of a <c>mailto:</c> link, built from the scheme name of <see cref="Uri"/>.
    /// </summary>
    private static readonly string MailtoPrefix = Uri.UriSchemeMailto + ":";

    /// <summary>
    /// Maps the presentation route under its own rate-limit policy (SPEC-025 HOP-061). No other method is
    /// mapped here.
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="hopPath">Normalized base path of the resolve endpoint (<see cref="HopModeSettings.HopPath"/>).</param>
    /// <param name="rateLimitPolicyName">Rate limiting policy name.</param>
    /// <returns>The route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapHopResolveEndpoint(
        this IEndpointRouteBuilder endpoints,
        string hopPath,
        string rateLimitPolicyName)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(hopPath);
        ArgumentException.ThrowIfNullOrEmpty(rateLimitPolicyName);

        endpoints.MapGet(HopUrlForm.RouteTemplate(hopPath), HandlePresentationAsync)
            .RequireRateLimiting(rateLimitPolicyName);

        return endpoints;
    }

    /// <summary>
    /// Answers a presentation of a hop URL: HTTPS first, then who presented it (step 1), then the check of
    /// the token and the transaction (steps 2–3); a refusal is written down and answered with the refusal
    /// page, an admitted presentation is observed (steps 4–5), a preview gets the neutral page, and a person
    /// is taken into the channel by a target the adapter derives again right now (steps 6–7). A person who
    /// presented the unified token chooses the channel on the phone instead: the targets of every channel of
    /// the set are derived, no redirect style applies, and the answer is the scanner page with a button per
    /// channel (SPEC-025 HOP-143, HOP-144, HOP-146). The token of a channel answered with that page gets
    /// the same page with that one channel, which opens the mail of the channel by itself.
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="presentationCheck">The check of steps 2–3.</param>
    /// <param name="refusalJournal">Where a refusal is written down.</param>
    /// <param name="resolvePages">The pages of the endpoint.</param>
    /// <param name="observationSteps">The observations of steps 4–5.</param>
    /// <param name="channelDisplayService">The display list of the channels — step 6 of the unified token.</param>
    /// <param name="pageSettingsResolver">
    /// Resolver of the page settings the display list is narrowed by and the scanner page is rendered with,
    /// and of the setting that switches the spare start code of a channel.
    /// </param>
    /// <param name="supersededObservation">The observation of a superseded token.</param>
    /// <param name="signalReceiver">
    /// The receiver of client signals — the one writer of the record of a launched channel, asked to make
    /// it where the answer is a redirect to a mail and no page is left to report the start.
    /// </param>
    /// <returns>The answer.</returns>
    private static async Task<IResult> HandlePresentationAsync(
        HttpContext httpContext,
        HopPresentationCheck presentationCheck,
        HopRefusalJournal refusalJournal,
        HopResolvePages resolvePages,
        HopObservationSteps observationSteps,
        IChannelDisplayService channelDisplayService,
        AuthPageSettingsResolver pageSettingsResolver,
        HopSupersededObservation supersededObservation,
        HopSignalReceiver signalReceiver)
    {
        var cancellationToken = httpContext.RequestAborted;

        // No answer of this route is cached: a target is single-use, and a refusal describes a moment
        // (SPEC-025 HOP-067). The directive is the one of every page of the contour, which the anti-forgery
        // tokens of the cancel form of the scanner page leave as it is.
        CorePageCaching.Forbid(httpContext.Response);

        // HTTPS only (SPEC-025 HOP-062) — a protection of the endpoint as a whole, like its rate limit,
        // not a row of the answer matrix: the token is neither decoded nor recorded.
        if (!httpContext.Request.IsHttps)
        {
            return Results.BadRequest();
        }

        // Step 1: who presented the URL, told by the headers alone and before the token is looked at.
        var requestKind = HopRequestClassifier.Classify(httpContext.Request.Headers);
        var requestMarker = HopRequestMarkers.Of(requestKind);

        // Steps 2–3.
        var outcome = await presentationCheck.CheckAsync(
            httpContext.GetRouteValue(HopModeConstants.HopTokenRouteParameter) as string,
            HopExpiryBasis.TokenAndTransaction,
            cancellationToken);

        // A token the page has already replaced (SPEC-025 HOP-155), for the per-channel and the unified token
        // alike: one log entry, whatever the check decided after the expiries; the answer below does not
        // depend on it.
        supersededObservation.Observe(requestKind, outcome);

        if (outcome.Refusal is not null)
        {
            // A refusal is recorded whoever presented the URL: the kind travels in the record as a field.
            // The page is the same for a person and for a preview (SPEC-025 HOP-070).
            await refusalJournal.RecordAsync(outcome, requestMarker, cancellationToken);

            return await resolvePages.RefusalPageAsync(httpContext, cancellationToken);
        }

        // Steps 4–5: observed for a person only, best-effort — they change neither the answer nor the page,
        // and a target that is not obtained later does not undo them (SPEC-025 HOP-064, HOP-074). The
        // settings they read are the ones the launch page decides its client signals by (HOP-066c).
        var observationSettings = await observationSteps.ObserveAsync(httpContext, requestKind, outcome);

        // A preview is never taken into the channel, whatever the redirect style: it gets a page with no
        // target, and the adapter is not even asked for one (SPEC-025 HOP-072a).
        if (requestKind is HopRequestKind.Preview)
        {
            return await resolvePages.NeutralPageAsync(httpContext, cancellationToken);
        }

        // Step 6 of the unified token (SPEC-025 HOP-143): the display list of the transaction, whose
        // service asks the adapter of every channel of the set for its target under the tenant of the
        // transaction and leaves out a channel that gives none. No channel left is the refusal of step 6;
        // steps 4–5 are not undone.
        if (outcome is { UnifiedChannelSet: not null, Transaction: { } unifiedTransaction })
        {
            var pageSettings = await pageSettingsResolver.ResolveAsync(
                TransactionResolutionContext.For(unifiedTransaction), cancellationToken);

            var channels = await ObtainChannelsAsync(
                unifiedTransaction,
                unifiedTransaction.AllowedChannelTypes,
                pageSettings,
                channelDisplayService,
                cancellationToken);
            if (channels is null)
            {
                return await RefuseTargetAsync();
            }

            // Step 7 of the unified token: no 302, no launch page and no automatic start — the page of the
            // channel buttons, on which the person chooses the channel (SPEC-025 HOP-146).
            return await resolvePages.ScannerPageAsync(
                httpContext,
                unifiedTransaction,
                pageSettings,
                channels,
                observationSettings,
                launchTarget: null,
                cancellationToken);
        }

        if (outcome is not { Transaction: { } transaction, Adapter: { } adapter, ChannelMode: { } channelMode })
        {
            throw new InvalidOperationException(
                "An admitted hop presentation carries its transaction, adapter and channel mode.");
        }

        // A channel answered with the page of the channel buttons — the style Interstitial of an Email
        // channel that accepts a typed address: steps 6–7 are those of that page, taken for this one
        // channel (SPEC-025 HOP-195). Under Direct the answer is the redirect below.
        if (await HopChannelModes.AnswersOnButtonsPageAsync(
                adapter, channelMode, TransactionResolutionContext.For(transaction), cancellationToken))
        {
            return await AnswerOnButtonsPageAsync();
        }

        // Step 6: the target is derived again, by the adapter, for this presentation — neither the token
        // nor the query states it (SPEC-025 HOP-005, HOP-040). The target of the Email channel is the mail
        // itself: the phone that scanned the code opens the mail client.
        var opensMail = adapter.ChannelType is ChannelTypes.Email;
        var target = opensMail
            ? await ObtainMailAsync(adapter, transaction, cancellationToken)
            : await ObtainTargetAsync(adapter, transaction, cancellationToken);
        if (target is null)
        {
            return await RefuseTargetAsync();
        }

        // A channel whose link carries no transaction: the target only opens its chat, and what starts
        // the transaction there is the message with its code. The answer is the page that shows the
        // message, whatever the redirect style — a redirect would leave the person in a chat with nothing
        // to send. The code was issued when the QR code was shown and is only read here: a code that is
        // gone — spent by a start, or lost by its storage — is not issued again, and the presentation has
        // no way into the channel left.
        if (HopChannelModes.StartsByCodeOnly(adapter))
        {
            var startMessage = await ObtainStartMessageAsync(
                httpContext.RequestServices, adapter, transaction, cancellationToken);
            if (startMessage is not { } message)
            {
                return await RefuseTargetAsync();
            }

            return await resolvePages.CodeStartPageAsync(
                httpContext, transaction, adapter, target, message, observationSettings, cancellationToken);
        }

        // Step 7: the redirect style is the form of the answer (SPEC-025 HOP-065, HOP-067).
        if (channelMode.RedirectStyle is not HopRedirectStyle.Direct)
        {
            // A channel whose link carries the transaction and that also starts by a code keeps the
            // message with the code on its launch page, as a way on when the chat opened and nothing
            // started in it. The spare code is switched by a setting of the channel, and switched off
            // the code is not read at all. The code is only read here; without one the page is the
            // page it would be for a channel that has no spare start — that is no refusal.
            var spareStartMessage = adapter.Capabilities.CodeStart is { LinkCarriesTransaction: true }
                && await pageSettingsResolver.ResolveSpareStartCodeEnabledAsync(
                    TransactionResolutionContext.For(transaction), adapter.ChannelType, cancellationToken)
                ? await ObtainStartMessageAsync(httpContext.RequestServices, adapter, transaction, cancellationToken)
                : null;

            return await resolvePages.InterstitialPageAsync(
                httpContext, transaction, adapter, target, spareStartMessage, observationSettings, cancellationToken);
        }

        // A 302, never a permanent redirect: the target is single-use. A target no Location header can
        // carry is no way into the channel either — the same refusal, not a failed response.
        if (ToLocationValue(target) is not { } location)
        {
            return await RefuseTargetAsync();
        }

        // A redirect to a mail opens the mail client with no page left to report the start, so the passage
        // itself is what is observed, by the record an accepted signal makes: the mark once, the status on
        // every such presentation (SPEC-025 HOP-192).
        // Without the settings of the presentation nothing is recorded, and the next presentation gets
        // the same answer and tries again.
        if (opensMail && observationSettings is not null)
        {
            await signalReceiver.RecordLaunchedByRedirectAsync(transaction, adapter.ChannelType, observationSettings);
        }

        return Results.Redirect(location);

        // The answer for a channel answered with the page of the channel buttons — the Email channel that
        // accepts a typed address, under the style Interstitial: the page with this one channel, holding
        // the button that opens the mail and the link that reveals the field for the address.
        async Task<IResult> AnswerOnButtonsPageAsync()
        {
            var pageSettings = await pageSettingsResolver.ResolveAsync(
                TransactionResolutionContext.For(transaction), cancellationToken);

            // Step 6: the display entry of the channel, with the target its adapter derives for this
            // presentation, by the service that builds the list of the page for the unified token.
            var channels = await ObtainChannelsAsync(
                transaction,
                new HashSet<string>(StringComparer.Ordinal) { adapter.ChannelType },
                pageSettings,
                channelDisplayService,
                cancellationToken);

            // The page is answered only for a mail — by the sign the page itself lays out the button of the
            // mail by. A target that is not one (the mail was not composed, and the target is the start of
            // Pull) is no way into the channel, as for any channel: the refusal, not a page with the field
            // for a typed address alone.
            if (channels is not [var channel, ..] || !DefaultAuthPageRenderer.IsEmailPushLink(channel.DeepLinkUrl))
            {
                return await RefuseTargetAsync();
            }

            // Step 7: no redirect — the page opens the mail by itself, once per tab. A transaction that
            // already has the answer of the channel and waits for the person on a page of the core — for
            // its question or for the missing claims (SPEC-025 HOP-179) — has no use for the mail any more:
            // the page is shown, and it does not open the mail.
            var launchTarget = transaction.IsAwaitingWebConfirmation() || transaction.IsCompletingClaims()
                ? null
                : channel.DeepLinkUrl;

            return await resolvePages.ScannerPageAsync(
                httpContext, transaction, pageSettings, [channel], observationSettings, launchTarget,
                cancellationToken);
        }

        async Task<IResult> RefuseTargetAsync()
        {
            await refusalJournal.RecordAsync(
                outcome with { Refusal = HopRefusal.TargetNotObtained }, requestMarker, cancellationToken);

            return await resolvePages.RefusalPageAsync(httpContext, cancellationToken);
        }
    }

    /// <summary>
    /// The target in the form a <c>Location</c> header carries: ASCII only. Every non-ASCII character is
    /// written as the percent-encoded bytes of its UTF-8 form — the mapping of an IRI to a URI (RFC 3987
    /// §3.1), the same form a browser gives the target when it follows it from the launch page, and the
    /// form <c>mailto</c> allows for an internationalized domain (RFC 6068 §2). Percent signs already in
    /// the target are kept, so an encoded sequence stays as it is.
    /// </summary>
    /// <param name="target">The target the adapter derived.</param>
    /// <returns>The header value, or null when the target cannot be one: it contains a control character
    /// or text that is not well-formed UTF-16.</returns>
    private static string? ToLocationValue(string target)
    {
        var location = new StringBuilder(target.Length);
        Span<byte> utf8 = stackalloc byte[MaxUtf8BytesPerRune];
        var rest = target.AsSpan();

        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var consumed) is not OperationStatus.Done)
            {
                return null;
            }

            rest = rest[consumed..];

            if (rune.IsAscii)
            {
                if (Rune.IsControl(rune))
                {
                    return null;
                }

                location.Append((char)rune.Value);
                continue;
            }

            var length = rune.EncodeToUtf8(utf8);
            foreach (var octet in utf8[..length])
            {
                location.Append('%').Append(octet.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return location.ToString();
    }

    /// <summary>
    /// The display list of the channels of the transaction, over its ownership context and the display
    /// override of its page settings — the list of the channel choice page (SPEC-025 HOP-143). The page
    /// shows no QR code of a channel, so none is drawn and no hop token is issued for one (HOP-146).
    /// </summary>
    /// <param name="transaction">The transaction the token names.</param>
    /// <param name="channelTypes">
    /// The channels the list is taken over: the set of the transaction for the unified token, the one
    /// channel of the token otherwise.
    /// </param>
    /// <param name="pageSettings">Effective page settings of the transaction.</param>
    /// <param name="channelDisplayService">The display list of the channels.</param>
    /// <param name="cancellationToken">Cancellation token of the request.</param>
    /// <returns>The channels that gave a target, in display order; null — none did.</returns>
    private static async Task<IReadOnlyList<ChannelDisplayInfo>?> ObtainChannelsAsync(
        Transaction transaction,
        IReadOnlySet<string> channelTypes,
        AuthPageSettings pageSettings,
        IChannelDisplayService channelDisplayService,
        CancellationToken cancellationToken)
    {
        var channels = await channelDisplayService.GetChannelDisplayDataAsync(
            transaction.Id,
            transaction.ExpiresAt,
            channelTypes,
            TransactionResolutionContext.For(transaction),
            ChannelDisplayOverride.Of(pageSettings),
            ChannelQrSurface.NotShown,
            cancellationToken);

        return channels.IsSuccess ? channels.Value : null;
    }

    /// <summary>
    /// The mail the Email channel is entered by — the target of its presentation where the answer is
    /// chosen by the redirect style: the deep link of the adapter when it is a <c>mailto:</c> link. The
    /// adapter gives one when Push is its effective mode; a target of any other form is no mail to open.
    /// </summary>
    /// <param name="adapter">Adapter of the Email channel.</param>
    /// <param name="transaction">The transaction the token names.</param>
    /// <param name="cancellationToken">Cancellation token of the request.</param>
    /// <returns>The <c>mailto:</c> link, or null when none was obtained.</returns>
    private static async Task<string?> ObtainMailAsync(
        IChannelAdapter adapter,
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        var target = await ObtainTargetAsync(adapter, transaction, cancellationToken);

        return target is not null && target.StartsWith(MailtoPrefix, StringComparison.OrdinalIgnoreCase)
            ? target
            : null;
    }

    /// <summary>
    /// Reads the message with the code of the transaction for a channel that starts by a code — as its
    /// only start, or as the spare one beside a link that carries the transaction. The code is looked up
    /// by the transaction alone and is never issued here.
    /// </summary>
    /// <remarks>
    /// No storage of codes in the host, no live code of the transaction, a declared template that cannot
    /// carry the code and a failure of the storage all mean the same to the visitor — nothing to send —
    /// and are answered alike: as one refusal where the message is the only start, and as a launch page
    /// without the spare code where it is not. The failure is not written to the log: what a storage says about
    /// a record it failed to read may quote the code, and the code addresses the transaction.
    /// </remarks>
    /// <param name="services">Services of the request.</param>
    /// <param name="adapter">Adapter of the channel of the token.</param>
    /// <param name="transaction">The transaction the token names.</param>
    /// <param name="cancellationToken">Cancellation token of the request.</param>
    /// <returns>The message in its two forms, or null when there is none to show.</returns>
    private static async Task<ChannelStartMessageText?> ObtainStartMessageAsync(
        IServiceProvider services,
        IChannelAdapter adapter,
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        // The storage is registered by the channel layer; a host composed without that layer has none,
        // and no code was ever issued there.
        if (adapter.Capabilities.CodeStart is not { } codeStart
            || services.GetService<IChannelStartCodeStore>() is not { } startCodeStore)
        {
            return null;
        }

        try
        {
            var startCode = await startCodeStore.FindByTransactionAsync(transaction.Id, cancellationToken);

            return startCode is null ? null : ChannelStartMessage.Compose(codeStart, startCode.Code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the adapter for the target of the transaction, under the tenant of the transaction: the deep
    /// link is built from that tenant's credentials, whoever opened the link. The request names the hop QR
    /// code as the purpose of the link: this is where the scanned code leads, so a template the deployment
    /// states for that purpose reaches the target. A scan answered with the page of buttons does not come
    /// here — the buttons of that page are asked for as buttons.
    /// </summary>
    /// <remarks>
    /// A failure, an empty target and an exception of the adapter all mean the same to the visitor — no
    /// way into the channel — and are recorded as one refusal. Neither the error nor the exception goes to
    /// the log: what an adapter says about a target it failed to build may quote the target, and the target
    /// is a capability of the transaction.
    /// </remarks>
    /// <param name="adapter">Adapter of the channel of the token.</param>
    /// <param name="transaction">The transaction the token names.</param>
    /// <param name="cancellationToken">Cancellation token of the request.</param>
    /// <returns>The target, or null when it was not obtained.</returns>
    private static async Task<string?> ObtainTargetAsync(
        IChannelAdapter adapter,
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        using var tenantScope = ChannelTenantContext.BeginScope(transaction.GetTenantId());

        try
        {
            var deepLink = await adapter.GetDeepLinkAsync(
                new DeepLinkRequest { TransactionId = transaction.Id, Purpose = DeepLinkPurpose.HopQr },
                cancellationToken);

            return deepLink.IsSuccess && !string.IsNullOrWhiteSpace(deepLink.Value) ? deepLink.Value : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
