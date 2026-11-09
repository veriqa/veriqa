// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text;
using Microsoft.Extensions.Options;
using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.Endpoints;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.InitiatorContext;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The pages the resolve endpoint answers with, each built over the context it may read: the refusal and
/// the neutral page over the request and the core level only, the two launch pages and the scanner page
/// over their transaction (SPEC-025 HOP-066, HOP-070, HOP-072a, HOP-146). Every page is a <c>200</c> HTML page:
/// the first three of the core contour's service-page family, the scanner page of the built-in renderer of
/// the sign-in window. A renderer of the host's own (<c>UseAuthPageRenderer</c>) never receives the scanner
/// page: the built-in renderer's page script sends the hop signals, whose format is not published.
/// </summary>
internal sealed class HopResolvePages
{
    /// <summary>
    /// Locale-file localizer of the page texts.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _localizer;

    /// <summary>
    /// Factory the subject of a confirmation is worded by.
    /// </summary>
    private readonly IConfirmationPromptContextFactory _promptContextFactory;

    /// <summary>
    /// Canonical layer resolver — the reader of the subject-display surfaces and of the configured name
    /// of the chat.
    /// </summary>
    private readonly IConfigurationResolver _configurationResolver;

    /// <summary>
    /// Resolver of the effective page branding.
    /// </summary>
    private readonly CorePageBrandingResolver _brandingResolver;

    /// <summary>
    /// Resolver of the text overrides of the launch page, stated by the levels of its transaction.
    /// </summary>
    private readonly CorePageTextResolver _textResolver;

    /// <summary>
    /// Resolver of the line of a begun transition into the channel of the launch page.
    /// </summary>
    private readonly CorePageChannelLineResolver _channelLineResolver;

    /// <summary>
    /// Registry of the languages the deployment carries.
    /// </summary>
    private readonly IAuthPageLanguageRegistry _languageRegistry;

    /// <summary>
    /// Options holding the default language.
    /// </summary>
    private readonly IOptions<VeriqaOptions> _veriqaOptions;

    /// <summary>
    /// The context of a page of a live transaction that never leaves itself — the scanner page is one —
    /// and the lines such a page ends on.
    /// </summary>
    private readonly TransactionPageContextBuilder _pageContextBuilder;

    /// <summary>
    /// The built-in renderer of the sign-in window, which renders the scanner page whatever renderer the
    /// host substituted for the sign-in window.
    /// </summary>
    private readonly DefaultAuthPageRenderer _authPageRenderer;

    /// <summary>
    /// Logger a rejected display label of a third-party channel is reported to.
    /// </summary>
    private readonly ILogger<HopResolvePages> _logger;

    /// <summary>
    /// Settings of the transaction engine, read per page: how long a stalled confirmed transaction lives
    /// bounds how long the launch page follows one.
    /// </summary>
    private readonly IOptionsMonitor<TransactionEngineOptions> _engineOptions;

    /// <summary>
    /// Normalizer of the User-Agent: the type of the device the launch page is opened on names the button
    /// under its outcome.
    /// </summary>
    private readonly IUserAgentNormalizer _userAgentNormalizer;

    /// <summary>
    /// Creates the page set.
    /// </summary>
    /// <param name="localizer">Locale-file localizer.</param>
    /// <param name="promptContextFactory">Factory of the confirmation context.</param>
    /// <param name="configurationResolver">Canonical layer resolver.</param>
    /// <param name="brandingResolver">Resolver of the effective page branding.</param>
    /// <param name="textResolver">Resolver of the text overrides of the launch page.</param>
    /// <param name="channelLineResolver">Resolver of the line of a begun transition into the channel.</param>
    /// <param name="languageRegistry">Registry of supported languages.</param>
    /// <param name="veriqaOptions">Veriqa options.</param>
    /// <param name="pageContextBuilder">Builder of the context of the scanner page.</param>
    /// <param name="authPageRenderer">Built-in renderer of the sign-in window.</param>
    /// <param name="engineOptions">Settings of the transaction engine.</param>
    /// <param name="userAgentNormalizer">Normalizer of the User-Agent.</param>
    /// <param name="logger">Logger.</param>
    public HopResolvePages(
        IConfirmationPromptLocalizer localizer,
        IConfirmationPromptContextFactory promptContextFactory,
        IConfigurationResolver configurationResolver,
        CorePageBrandingResolver brandingResolver,
        CorePageTextResolver textResolver,
        CorePageChannelLineResolver channelLineResolver,
        IAuthPageLanguageRegistry languageRegistry,
        IOptions<VeriqaOptions> veriqaOptions,
        TransactionPageContextBuilder pageContextBuilder,
        DefaultAuthPageRenderer authPageRenderer,
        IOptionsMonitor<TransactionEngineOptions> engineOptions,
        IUserAgentNormalizer userAgentNormalizer,
        ILogger<HopResolvePages> logger)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(promptContextFactory);
        ArgumentNullException.ThrowIfNull(configurationResolver);
        ArgumentNullException.ThrowIfNull(brandingResolver);
        ArgumentNullException.ThrowIfNull(textResolver);
        ArgumentNullException.ThrowIfNull(channelLineResolver);
        ArgumentNullException.ThrowIfNull(languageRegistry);
        ArgumentNullException.ThrowIfNull(veriqaOptions);
        ArgumentNullException.ThrowIfNull(pageContextBuilder);
        ArgumentNullException.ThrowIfNull(authPageRenderer);
        ArgumentNullException.ThrowIfNull(engineOptions);
        ArgumentNullException.ThrowIfNull(userAgentNormalizer);
        ArgumentNullException.ThrowIfNull(logger);

        _localizer = localizer;
        _promptContextFactory = promptContextFactory;
        _configurationResolver = configurationResolver;
        _brandingResolver = brandingResolver;
        _textResolver = textResolver;
        _channelLineResolver = channelLineResolver;
        _languageRegistry = languageRegistry;
        _veriqaOptions = veriqaOptions;
        _pageContextBuilder = pageContextBuilder;
        _authPageRenderer = authPageRenderer;
        _engineOptions = engineOptions;
        _userAgentNormalizer = userAgentNormalizer;
        _logger = logger;
    }

    /// <summary>
    /// The refusal page (SPEC-025 HOP-070) — the same for every refusal and for every kind of request.
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    public async Task<IResult> RefusalPageAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        var branding = await _brandingResolver.ResolveAsync(ResolutionContext.Core, cancellationToken);

        return Html(HopNoticePage.BuildRefusal(_localizer, RequestLanguage(httpContext), branding));
    }

    /// <summary>
    /// The neutral page a recognized preview gets where a person would be taken into the channel
    /// (SPEC-025 HOP-072a).
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    public async Task<IResult> NeutralPageAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        var branding = await _brandingResolver.ResolveAsync(ResolutionContext.Core, cancellationToken);

        return Html(HopNoticePage.BuildNeutral(_localizer, RequestLanguage(httpContext), branding));
    }

    /// <summary>
    /// The launch page of the transaction (SPEC-025 HOP-066): its language, the branding of its levels and,
    /// for a confirmation the deployment shows the subject of here, that subject (HOP-069a). Once a
    /// transition into the channel began the page follows the outcome of the transaction (HOP-186).
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="transaction">The transaction the presented token names.</param>
    /// <param name="adapter">Adapter of the channel the target leads into.</param>
    /// <param name="target">The target the adapter derived for this presentation.</param>
    /// <param name="spareStartMessage">
    /// The message with the code of the transaction the channel keeps as a spare way in; null — the
    /// channel has none or the code could not be read, and the page goes without it.
    /// </param>
    /// <param name="observationSettings">
    /// The observation settings of this presentation — the one reading of them per request, made by its
    /// observation steps; null — they could not be read, and the page goes without client signals.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    public async Task<IResult> InterstitialPageAsync(
        HttpContext httpContext,
        Transaction transaction,
        IChannelAdapter adapter,
        string target,
        ChannelStartMessageText? spareStartMessage,
        HopObservationSettings? observationSettings,
        CancellationToken cancellationToken)
    {
        var parts = await LaunchPagePartsAsync(
            httpContext, transaction, adapter, target, spareStartMessage, cancellationToken);

        // What the page follows the outcome by once a transition began: the status of the transaction,
        // asked by the session identifier the page of the channel buttons of this transaction is handed
        // as well, the question of the core that page leaves for, how long it goes on asking once the
        // transaction is confirmed, and the lines that page ends on. The lines are worded in the content
        // language of this page — the language its own texts
        // and its lang attribute go by — and not in the language of the transaction, so the status line
        // never speaks a language the page does not state. A receipt of a confirmation may name a caller
        // value only where the page shows the subject (SPEC-039 R38).
        var pathBase = httpContext.Request.PathBase.Value ?? string.Empty;
        var contentLanguage = HopLaunchPageHeading.ContentLanguage(_localizer, transaction.Type, parts.Language);
        var outcomeWatch = new HopInterstitialOutcomeWatch(
            pathBase + TransactionStatusEndpoint.BuildStatusPath(SessionIdMapper.ToSessionId(transaction.Id)),
            TransactionPageContextBuilder.ConfirmationQuestionUrl(transaction) is { } questionUrl
                ? pathBase + questionUrl
                : null,
            TransactionOutcomeWatchScript.ConfirmedWatchLimit(_engineOptions.CurrentValue),
            await _pageContextBuilder.OutcomeLinesAsync(
                transaction,
                parts.Texts,
                contentLanguage,
                showsSubject: parts.Content.SubjectText is not null,
                cancellationToken),
            OutcomeCloseAction.ApplicationName(
                transaction, OutcomeCloseAction.OpenedOn(httpContext, _userAgentNormalizer)));

        // The line of a begun transition is the line the sign-in window shows once the answer is awaited in
        // the channel, and the hint is the hint that window shows once the phone is on its way into it — both
        // naming the channel by the rule the tabs of that window are named with, in the content language of
        // this page.
        var channelName = ChannelNames.LocalizeChannelName(
            _localizer, parts.Texts, parts.Content.ChannelType, parts.Content.ChannelDisplayName, contentLanguage);
        var channelLines = await _channelLineResolver.ForLaunchPageAsync(
            transaction,
            parts.Content.ChannelType,
            channelName,
            parts.Content.BotName,
            contentLanguage,
            cancellationToken);

        var html = HopInterstitialPage.Build(
            parts.Content,
            outcomeWatch,
            channelLines.Answer,
            channelLines.HopHint,
            SignalScriptOf(observationSettings),
            httpContext.Items[SecurityHeaderConstants.CspNonceItemKey] as string,
            _localizer,
            parts.Language,
            parts.Texts,
            parts.Branding);

        return Html(html);
    }

    /// <summary>
    /// The launch page of a channel whose link carries no transaction: the message with the code of the
    /// transaction and the button that copies it and opens the chat, with no automatic start. It is built
    /// over the same context as the launch page of the redirect style — the language of the transaction,
    /// the branding and the texts of its levels, the subject of a confirmation — and sends the same client
    /// signals.
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="transaction">The transaction the presented token names.</param>
    /// <param name="adapter">Adapter of the channel the button leads into.</param>
    /// <param name="target">The link the adapter derived for this presentation; it opens the chat.</param>
    /// <param name="message">The message with the code of the transaction, in its two forms.</param>
    /// <param name="observationSettings">
    /// The observation settings of this presentation; null — they could not be read, and the page goes
    /// without client signals.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    public async Task<IResult> CodeStartPageAsync(
        HttpContext httpContext,
        Transaction transaction,
        IChannelAdapter adapter,
        string target,
        ChannelStartMessageText message,
        HopObservationSettings? observationSettings,
        CancellationToken cancellationToken)
    {
        var parts = await LaunchPagePartsAsync(
            httpContext, transaction, adapter, target, spareStartMessage: null, cancellationToken);

        var html = HopCodeStartPage.Build(
            parts.Content,
            message,
            SignalScriptOf(observationSettings),
            httpContext.Items[SecurityHeaderConstants.CspNonceItemKey] as string,
            _localizer,
            parts.Language,
            parts.Texts,
            parts.Branding);

        return Html(html);
    }

    /// <summary>
    /// What both launch pages of a transaction are built over: its language, the channel as the sign-in
    /// window names and draws it, the name of its chat, the subject of a confirmation, and the branding and
    /// texts of its levels.
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="transaction">The transaction the presented token names.</param>
    /// <param name="adapter">Adapter of the channel the page leads into.</param>
    /// <param name="target">The target the adapter derived for this presentation.</param>
    /// <param name="spareStartMessage">The spare message with the code of the transaction; null — none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parts of the page.</returns>
    private async Task<LaunchPageParts> LaunchPagePartsAsync(
        HttpContext httpContext,
        Transaction transaction,
        IChannelAdapter adapter,
        string target,
        ChannelStartMessageText? spareStartMessage,
        CancellationToken cancellationToken)
    {
        var resolution = TransactionResolutionContext.For(transaction);

        // The page speaks the language of the request that created the transaction, as every page of a
        // transaction does; a transaction that stated none falls back to this request.
        var language = AuthPageLocalization.ResolveTransactionLanguage(
            transaction,
            RequestLanguage(httpContext),
            _veriqaOptions.Value.Localization.DefaultLanguage,
            _languageRegistry.SupportedLanguages);

        var subjectText = await SubjectTextAsync(transaction, resolution, language, cancellationToken);
        var branding = await _brandingResolver.ResolveAsync(resolution, [adapter.ChannelType], cancellationToken);

        // The texts the levels of the transaction override, resolved once for the page: the page itself is
        // built synchronously and only applies them.
        var texts = await _textResolver.ResolveAsync(resolution, cancellationToken);

        // The page names and draws the channel as the sign-in window does — a third-party label and
        // glyph pass the same reading there and here.
        var (displayName, iconSvgPath) = ChannelDisplayService.ReadDisplayMetadata(adapter, _logger);

        // The name of the chat is the bot of the tenant of the transaction, whoever opened the link —
        // asked under the tenant of its resolution context, as the target is.
        var botName = await ChannelBotNames.ReadAsync(
            adapter, _configurationResolver, resolution, _logger, cancellationToken);

        return new LaunchPageParts(
            language,
            new HopInterstitialContent(
                target,
                adapter.ChannelType,
                displayName,
                subjectText,
                transaction.Type,
                iconSvgPath,
                botName,
                spareStartMessage),
            texts,
            branding);
    }

    /// <summary>
    /// The scanner page of the unified hop QR code (SPEC-025 HOP-146): the page of the channel buttons the
    /// phone that scanned the code chooses its channel on — always rendered by the built-in renderer of the
    /// sign-in window, never by a renderer of the host's own, over the context of its transaction, without a redirect and without an automatic start (HOP-144).
    /// A tap on a channel button sends the signal of the chosen channel when client signals are effectively
    /// accepted, by the same one reading of the settings the observation steps made (HOP-156). The token of
    /// a channel answered with this page gets the same page, with that channel as its whole list, and that
    /// page alone may open its channel by itself. The page computes the browser fingerprint by the
    /// predicate the launch page puts it in by (HOP-090).
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="transaction">The transaction the presented token names.</param>
    /// <param name="pageSettings">Effective page settings, resolved over the transaction.</param>
    /// <param name="channels">The display list of the channels, each with its target.</param>
    /// <param name="observationSettings">
    /// The observation settings of this presentation — the one reading of them per request; null — they
    /// could not be read, and the page sends no signal.
    /// </param>
    /// <param name="launchTarget">
    /// The absolute <c>mailto:</c> of the one channel of the page, which the page opens by itself
    /// (<see cref="AuthPageRenderContext.HopLaunchTarget"/>); null — the page does not open the channel by
    /// itself, as the page of the unified token never does.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page.</returns>
    public async Task<IResult> ScannerPageAsync(
        HttpContext httpContext,
        Transaction transaction,
        AuthPageSettings pageSettings,
        IReadOnlyList<ChannelDisplayInfo> channels,
        HopObservationSettings? observationSettings,
        string? launchTarget,
        CancellationToken cancellationToken)
    {
        var renderContext = await _pageContextBuilder.BuildScannerPageAsync(
            httpContext,
            transaction,
            pageSettings,
            channels,
            sendsChannelLaunchedSignal: observationSettings is { AcceptClientSignals: true },
            collectsFingerprint: observationSettings is { CollectsFingerprintOnPage: true },
            launchTarget,
            cancellationToken);

        return Html(_authPageRenderer.RenderAuthPage(renderContext));
    }

    /// <summary>
    /// What the launch page sends back to the core (SPEC-025 HOP-066c): the signals when they are
    /// effectively accepted, and the fingerprint by the very predicate the receiver keeps it by (HOP-090).
    /// </summary>
    /// <param name="observationSettings">The observation settings of the presentation; null — not read.</param>
    /// <returns>The kind of the signal script.</returns>
    private static HopSignalScript SignalScriptOf(HopObservationSettings? observationSettings)
    {
        if (observationSettings is not { AcceptClientSignals: true })
        {
            return HopSignalScript.None;
        }

        return observationSettings.CollectsFingerprintOnPage
            ? HopSignalScript.SignalsWithFingerprint
            : HopSignalScript.Signals;
    }

    /// <summary>
    /// The subject the launch page shows: only for a confirmation, and only when the effective set of
    /// subject-display surfaces of the transaction contains this page (SPEC-025 HOP-069a, SPEC-039 R38).
    /// </summary>
    /// <param name="transaction">The transaction of the page.</param>
    /// <param name="resolution">Ownership context of the transaction.</param>
    /// <param name="language">Language of the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subject as plain text, or null when the page shows none.</returns>
    private async Task<string?> SubjectTextAsync(
        Transaction transaction,
        ResolutionContext resolution,
        string language,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(transaction.Type, TransactionTypes.Confirmation, StringComparison.Ordinal)
            || !await _configurationResolver.ShowsSubjectOnAsync(
                resolution, ConfirmationSubjectSurface.HopInterstitial, cancellationToken))
        {
            return null;
        }

        return await ConfirmationSubjectText.ResolveAsync(
            _promptContextFactory, transaction, language, cancellationToken);
    }

    /// <summary>
    /// The language of the request itself — the language of the pages built without the transaction.
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <returns>Language code.</returns>
    private string RequestLanguage(HttpContext httpContext) =>
        AuthPageStrings.DetectLanguage(
            httpContext.Request.Headers.AcceptLanguage.ToString(),
            _veriqaOptions.Value.Localization.DefaultLanguage,
            _languageRegistry.SupportedLanguages);

    /// <summary>
    /// A <c>200</c> HTML answer.
    /// </summary>
    /// <param name="html">The page.</param>
    /// <returns>The answer.</returns>
    private static IResult Html(string html) =>
        Results.Content(html, AuthPageConstants.HtmlContentType, Encoding.UTF8);

    /// <summary>
    /// What a launch page of a transaction is built over.
    /// </summary>
    /// <param name="Language">Language of the transaction's page.</param>
    /// <param name="Content">Where the page leads, which channel that is and the subject of a confirmation.</param>
    /// <param name="Texts">Text overrides stated by the transaction's levels.</param>
    /// <param name="Branding">Effective branding of the transaction's levels.</param>
    private sealed record LaunchPageParts(
        string Language,
        HopInterstitialContent Content,
        CorePageTexts Texts,
        CorePageBranding Branding);
}
