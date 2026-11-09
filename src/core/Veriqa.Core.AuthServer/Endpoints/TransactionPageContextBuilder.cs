// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.AuthServer.InitiatorContext;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.AuthServer.UI.Services;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// Builds the render context of a page of a live transaction that never leaves itself: the entry page of
/// a confirmation (GET /auth/transaction, SPEC-039 C15) and the scanner page of the unified hop QR code
/// (SPEC-025 HOP-146). For a confirmation the scanner page IS that entry page — the one context below,
/// with the flags of the scanner added; for a sign-in it is the sign-in window without the behaviour of
/// the initiator: no callback, no way to start over and no destination of its own, because the sign-in
/// finishes in the browser that started it (HOP-008, HOP-186).
/// <para>
/// Every level-owned value is resolved over the ownership context of the transaction, the page speaks
/// the language of the transaction, and the external resources of its design are stated for the CSP of
/// the request before the response starts (SPEC-007 UI-054, UI-040).
/// </para>
/// </summary>
internal sealed class TransactionPageContextBuilder
{
    /// <summary>
    /// Locale-file localizer — the page's own neutral key and the lines of the sign-in scanner.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// Factory the subject of a confirmation is worded by.
    /// </summary>
    private readonly IConfirmationPromptContextFactory _promptContextFactory;

    /// <summary>
    /// The one port that words terminal outcomes.
    /// </summary>
    private readonly IOutcomeReceiptText _receiptText;

    /// <summary>
    /// Canonical layer resolver — the reader of the subject-display surfaces.
    /// </summary>
    private readonly IConfigurationResolver _configurationResolver;

    /// <summary>
    /// Resolver of the text overrides the levels of the transaction state for the page.
    /// </summary>
    private readonly CorePageTextResolver _textResolver;

    /// <summary>
    /// Resolver of the lines the page shows for its channels.
    /// </summary>
    private readonly CorePageChannelLineResolver _channelLineResolver;

    /// <summary>
    /// Resolver of the colors the levels of the transaction state for the buttons of its channels.
    /// </summary>
    private readonly CorePageBrandingResolver _brandingResolver;

    /// <summary>
    /// Options holding the default language.
    /// </summary>
    private readonly IOptions<VeriqaOptions> _veriqaOptions;

    /// <summary>
    /// Registry of the languages the deployment carries.
    /// </summary>
    private readonly IAuthPageLanguageRegistry _languageRegistry;

    /// <summary>
    /// Where the external resources of the page are stated for the CSP of the request.
    /// </summary>
    private readonly CorePageResourceScope _resourceScope;

    /// <summary>
    /// Clock the remainder of the transaction is measured by.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Normalizer that tells the kind of the device the page is opened on — the caption of the action under
    /// the outcome is chosen by it.
    /// </summary>
    private readonly IUserAgentNormalizer _userAgentNormalizer;

    /// <summary>
    /// Creates the builder.
    /// </summary>
    /// <param name="promptLocalizer">Locale-file localizer.</param>
    /// <param name="promptContextFactory">Factory of the confirmation context.</param>
    /// <param name="receiptText">Port the terminal wording comes from.</param>
    /// <param name="configurationResolver">Canonical layer resolver.</param>
    /// <param name="textResolver">Resolver of the text overrides of the page.</param>
    /// <param name="channelLineResolver">Resolver of the lines of the channels of the page.</param>
    /// <param name="brandingResolver">Resolver of the colors of the channel buttons of the page.</param>
    /// <param name="veriqaOptions">Veriqa options.</param>
    /// <param name="languageRegistry">Registry of supported languages.</param>
    /// <param name="resourceScope">Resource scope of the request.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="userAgentNormalizer">Normalizer of the User-Agent of the request.</param>
    public TransactionPageContextBuilder(
        IConfirmationPromptLocalizer promptLocalizer,
        IConfirmationPromptContextFactory promptContextFactory,
        IOutcomeReceiptText receiptText,
        IConfigurationResolver configurationResolver,
        CorePageTextResolver textResolver,
        CorePageChannelLineResolver channelLineResolver,
        CorePageBrandingResolver brandingResolver,
        IOptions<VeriqaOptions> veriqaOptions,
        IAuthPageLanguageRegistry languageRegistry,
        CorePageResourceScope resourceScope,
        TimeProvider timeProvider,
        IUserAgentNormalizer userAgentNormalizer)
    {
        ArgumentNullException.ThrowIfNull(promptLocalizer);
        ArgumentNullException.ThrowIfNull(promptContextFactory);
        ArgumentNullException.ThrowIfNull(receiptText);
        ArgumentNullException.ThrowIfNull(configurationResolver);
        ArgumentNullException.ThrowIfNull(textResolver);
        ArgumentNullException.ThrowIfNull(channelLineResolver);
        ArgumentNullException.ThrowIfNull(brandingResolver);
        ArgumentNullException.ThrowIfNull(veriqaOptions);
        ArgumentNullException.ThrowIfNull(languageRegistry);
        ArgumentNullException.ThrowIfNull(resourceScope);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(userAgentNormalizer);

        _promptLocalizer = promptLocalizer;
        _promptContextFactory = promptContextFactory;
        _receiptText = receiptText;
        _configurationResolver = configurationResolver;
        _textResolver = textResolver;
        _channelLineResolver = channelLineResolver;
        _brandingResolver = brandingResolver;
        _veriqaOptions = veriqaOptions;
        _languageRegistry = languageRegistry;
        _resourceScope = resourceScope;
        _timeProvider = timeProvider;
        _userAgentNormalizer = userAgentNormalizer;
    }

    /// <summary>
    /// The context of the entry page of a confirmation (SPEC-039 C15), with the "Cancel confirmation" button
    /// where the levels of the transaction leave it on (SPEC-007 UI-122, SPEC-012 CFG-267).
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="transaction">The live confirmation transaction the page addresses.</param>
    /// <param name="pageSettings">Effective page settings, resolved over the transaction.</param>
    /// <param name="channels">The display list of the channels of the transaction.</param>
    /// <param name="unifiedQr">
    /// The unified hop QR code of the page (SPEC-025 HOP-183); null — the per-channel QR codes.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The render context.</returns>
    /// <exception cref="ArgumentException">The transaction is not a confirmation.</exception>
    public Task<AuthPageRenderContext> BuildEntryPageAsync(
        HttpContext httpContext,
        Transaction transaction,
        AuthPageSettings pageSettings,
        IReadOnlyList<ChannelDisplayInfo> channels,
        HopUnifiedQrImage? unifiedQr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (!IsConfirmation(transaction))
        {
            throw new ArgumentException("The entry page addresses a confirmation transaction.", nameof(transaction));
        }

        // The holder of the address of this page cancels: the identifier of the transaction is what the page
        // is opened by (SPEC-039 C15), and the cancel only records the refusal.
        return BuildAsync(
            httpContext,
            transaction,
            pageSettings,
            channels,
            unifiedQr,
            IssueTransactionCancelForm(httpContext, pageSettings),
            ScannerFlags.None,
            cancellationToken);
    }

    /// <summary>
    /// The context of the scanner page of the unified hop QR code (SPEC-025 HOP-146): the entry page of a
    /// confirmation, or the sign-in window without the behaviour of the initiator, with the flags of the
    /// scanner. Both types carry the cancel button where the levels of the transaction leave it on — "Cancel
    /// sign-in" or "Cancel confirmation" (SPEC-007 UI-122, SPEC-012 CFG-267).
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="transaction">The live transaction the unified token names.</param>
    /// <param name="pageSettings">Effective page settings, resolved over the transaction.</param>
    /// <param name="channels">The display list of the channels of the transaction.</param>
    /// <param name="sendsChannelLaunchedSignal">
    /// Whether a tap on a channel button sends the signal of the chosen channel (SPEC-025 HOP-156).
    /// </param>
    /// <param name="collectsFingerprint">
    /// Whether the page computes the browser fingerprint and sends it back (SPEC-025 HOP-090).
    /// </param>
    /// <param name="launchTarget">
    /// The absolute <c>mailto:</c> the page of one channel opens by itself (SPEC-025 HOP-195); null — the
    /// page does not open the channel by itself.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The render context.</returns>
    public Task<AuthPageRenderContext> BuildScannerPageAsync(
        HttpContext httpContext,
        Transaction transaction,
        AuthPageSettings pageSettings,
        IReadOnlyList<ChannelDisplayInfo> channels,
        bool sendsChannelLaunchedSignal,
        bool collectsFingerprint,
        string? launchTarget,
        CancellationToken cancellationToken)
    {
        return BuildAsync(
            httpContext,
            transaction,
            pageSettings,
            channels,
            // The phone that scanned the unified QR code is shown no QR code (SPEC-025 HOP-146).
            unifiedQr: null,
            // The phone holds the session identifier the unified token led it to, and a cancel only records
            // the refusal — for a sign-in as for a confirmation. It posts to the cancel of the entry page, not
            // to the one of the sign-in window: that one checks the browser nonce of the window that started
            // the sign-in and answers with a redirect to the client, and the phone has neither.
            IssueTransactionCancelForm(httpContext, pageSettings),
            new ScannerFlags(
                IsScannerPage: true,
                SendsChannelLaunchedSignal: sendsChannelLaunchedSignal,
                CollectsFingerprint: collectsFingerprint,
                LaunchTarget: launchTarget),
            cancellationToken);
    }

    /// <summary>
    /// The lines a page that follows a transaction without taking part in it ends on — the scanner page
    /// of the unified hop QR code and the launch page of the hop once a transition into the channel began
    /// (SPEC-025 HOP-186): the receipts of a confirmation, the lines of the scanner of a sign-in.
    /// </summary>
    /// <param name="transaction">The transaction of the page.</param>
    /// <param name="texts">Text overrides stated by the levels of the transaction.</param>
    /// <param name="language">Language of the page.</param>
    /// <param name="showsSubject">
    /// Whether the page shows the subject of a confirmation: its receipts may name a caller value only
    /// then. It has no meaning for a sign-in.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lines.</returns>
    public async Task<TransactionOutcomeLines> OutcomeLinesAsync(
        Transaction transaction,
        CorePageTexts texts,
        string language,
        bool showsSubject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(texts);

        return IsConfirmation(transaction)
            ? await ConfirmationOutcomeLinesAsync(transaction, language, showsSubject, cancellationToken)
            : SignInScannerOutcomeLines(texts, language);
    }

    /// <summary>
    /// The page of the core that asks the confirming question of a transaction (SPEC-039 E29), for a page
    /// that follows the transaction without taking part in it: the entry page of a confirmation, its
    /// scanner page and the launch page of the hop once a transition into the channel began (SPEC-025 HOP-186).
    /// </summary>
    /// <param name="transaction">The transaction of the page.</param>
    /// <returns>
    /// The root-relative address of the question; null — the transaction is not a confirmation, and no
    /// page of the core asks about it.
    /// </returns>
    public static string? ConfirmationQuestionUrl(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return IsConfirmation(transaction)
            ? ConfirmationQuestionUrl(SessionIdMapper.ToSessionId(transaction.Id))
            : null;
    }

    /// <summary>
    /// The address of the question of a confirmation by its session identifier. It is stated from the
    /// constants of the routes rather than taken out of a status message — so both delivery paths of that
    /// status lead to the same page. Root-relative: the path base is applied by the page.
    /// </summary>
    /// <param name="sessionId">External session identifier of the confirmation transaction.</param>
    /// <returns>The root-relative address of the question.</returns>
    private static string ConfirmationQuestionUrl(string sessionId) =>
        $"{OidcEndpoints.TransactionConfirmPage}"
        + $"?{OidcConstants.SessionIdParameterName}={Uri.EscapeDataString(sessionId)}";

    /// <summary>
    /// The form behind the cancel button of a page whose holder cancels by the session identifier — the
    /// entry page of a confirmation and the scanner page of either type — posting to
    /// <see cref="OidcEndpoints.TransactionCancel"/> (SPEC-007 UI-122).
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="pageSettings">Effective page settings, resolved over the transaction.</param>
    /// <returns>
    /// The form; null — the levels of the transaction turned the button off (SPEC-012 CFG-267), or no
    /// request token could be issued.
    /// </returns>
    private static AuthPageCancelForm? IssueTransactionCancelForm(HttpContext httpContext, AuthPageSettings pageSettings)
    {
        ArgumentNullException.ThrowIfNull(pageSettings);

        return pageSettings.CancelEnabled
            ? AuthPageCancelForm.Issue(httpContext, OidcEndpoints.TransactionCancel)
            : null;
    }

    /// <summary>
    /// The flags the scanner page adds to the context.
    /// </summary>
    /// <param name="IsScannerPage">The page is the scanner page of the unified hop QR code.</param>
    /// <param name="SendsChannelLaunchedSignal">A tap on a channel button sends the signal of the chosen channel.</param>
    /// <param name="CollectsFingerprint">The page computes the browser fingerprint and sends it back.</param>
    /// <param name="LaunchTarget">The target the page opens by itself; null — none.</param>
    private readonly record struct ScannerFlags(
        bool IsScannerPage,
        bool SendsChannelLaunchedSignal,
        bool CollectsFingerprint,
        string? LaunchTarget)
    {
        /// <summary>
        /// A page that is not the scanner page.
        /// </summary>
        public static ScannerFlags None => default;
    }

    /// <summary>
    /// The one construction of the context of a page that never leaves itself. What a confirmation adds —
    /// the question of the core as the destination, the subject and the receipts of the outcome — is taken
    /// by the type of the transaction; the rest is the same for both.
    /// </summary>
    /// <param name="httpContext">Context of the request.</param>
    /// <param name="transaction">The live transaction of the page.</param>
    /// <param name="pageSettings">Effective page settings, resolved over the transaction.</param>
    /// <param name="channels">The display list of the channels.</param>
    /// <param name="unifiedQr">The unified hop QR code of the page; null — none.</param>
    /// <param name="cancelForm">The form behind the cancel button; null — the page carries none.</param>
    /// <param name="scanner">The flags of the scanner page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The render context.</returns>
    private async Task<AuthPageRenderContext> BuildAsync(
        HttpContext httpContext,
        Transaction transaction,
        AuthPageSettings pageSettings,
        IReadOnlyList<ChannelDisplayInfo> channels,
        HopUnifiedQrImage? unifiedQr,
        AuthPageCancelForm? cancelForm,
        ScannerFlags scanner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(pageSettings);
        ArgumentNullException.ThrowIfNull(channels);

        // The effective external stylesheet and script of this page, stated for its CSP before the
        // response starts (SPEC-007 UI-054, UI-040).
        _resourceScope.Declare(pageSettings.Design.CustomCssPath, pageSettings.Design.CustomJsPath);

        // The page speaks the language of the transaction, which is the language the relying party
        // stated when it created it; a transaction that stated none falls back to the request.
        var defaultLanguage = _veriqaOptions.Value.Localization.DefaultLanguage;
        var supportedLanguages = _languageRegistry.SupportedLanguages;
        var language = AuthPageLocalization.ResolveTransactionLanguage(
            transaction,
            AuthPageStrings.DetectLanguage(
                httpContext.Request.Headers.AcceptLanguage.ToString(), defaultLanguage, supportedLanguages),
            defaultLanguage,
            supportedLanguages);

        // The texts the levels of the transaction override, resolved once for the page and handed to the
        // renderer with the rest of the context — the render is synchronous and only applies them.
        var resolution = TransactionResolutionContext.For(transaction);
        var texts = await _textResolver.ResolveAsync(resolution, cancellationToken);

        // The lines of the channels of the page, resolved over the levels of the transaction like its texts.
        var channelLines = await _channelLineResolver.ForWindowAsync(
            transaction, channels, texts, language, cancellationToken);

        // The colors the levels of the transaction state for the buttons of the channels of the page.
        var channelButtonColors = await _brandingResolver.ResolveChannelButtonColorsAsync(
            resolution,
            [.. channels.Select(channel => channel.ChannelType)],
            cancellationToken);

        var sessionId = SessionIdMapper.ToSessionId(transaction.Id);
        var outcome = IsConfirmation(transaction)
            ? await ConfirmationOutcomeAsync(transaction, sessionId, language, cancellationToken)
            : SignInScannerOutcome(SignInScannerOutcomeLines(texts, language));

        return new AuthPageRenderContext
        {
            SessionId = sessionId,
            ExpiresAt = transaction.ExpiresAt,
            // The countdown runs on the server's remainder here for the same reason it does on the
            // sign-in path: the browser's clock is not a shared one, and a transaction must not end
            // on this page because the device it is opened on runs fast.
            RemainingSeconds = Math.Max(0, (transaction.ExpiresAt - _timeProvider.GetUtcNow()).TotalSeconds),
            // No way out to offer: a confirmation transaction is created server-to-server and has no
            // request of its own to repeat, so the expired page states the fact and nothing more —
            // starting over is the calling application's to arrange (SPEC-039 C15). The scanner of a
            // sign-in is not the initiator either: a new sign-in starts on the device that started this
            // one (SPEC-025 HOP-146).
            RestartUrl = null,
            CancelForm = cancelForm,
            Channels = channels,
            ChannelLines = channelLines.Lines,
            FallbackChannelLaunchedLine = channelLines.FallbackLaunched,
            // The answer of the bot by the predicate the hub replays it under: a page opened again while the
            // answer is awaited in the channel — reloaded, or reopened from the same address — is rendered
            // in the state the replay would put it in, rather than with the channel choice it then takes away.
            AnsweredChannelType = transaction.IsAwaitingAnswerInChannel()
                ? transaction.SubjectBinding?.ChannelType
                : null,
            Language = language,
            CspNonce = httpContext.Items[SecurityHeaderConstants.CspNonceItemKey] as string,
            Design = pageSettings.Design,
            Title = pageSettings.Title,
            Instruction = pageSettings.Instruction,
            InstructionWithoutQr = pageSettings.InstructionWithoutQr,
            InstructionWithoutButton = pageSettings.InstructionWithoutButton,
            ChannelButtonColors = channelButtonColors,
            Texts = texts,
            PathBase = httpContext.Request.PathBase.Value ?? string.Empty,
            QrVisibility = pageSettings.QrVisibility,
            // The visibility of the channel buttons, reconciled with the QR visibility. The scanner page
            // is handed the same value and shows the buttons regardless: that is the renderer's rule.
            ChannelButtonVisibility = pageSettings.EffectiveChannelButtonVisibility,
            // The layout of the channels, as on the sign-in window: the channel choice of a confirmation
            // is the same page. The scanner page is laid out as buttons and has nothing to arrange.
            ChannelLayout = pageSettings.ChannelLayout,
            // Remembering the channel, as on the sign-in window: the entry page of a confirmation and the
            // scanner page behave as it does. Whether this page has a choice to put the channel forward
            // in, or a unified QR code to offer the checkbox under, is the renderer's to tell.
            RememberChannelHint = pageSettings.RememberChannelHint,
            RememberChannelCheckbox = pageSettings.RememberChannelCheckbox,
            // No callback URL and no navigation: a confirmation is finalized on the server, and the
            // callback of the sign-in path would end it as a stale session instead; a sign-in is
            // finished by the callback of the browser that started it, not by this one (HOP-008).
            CallbackUrl = null,
            NavigatesAway = false,
            ConfirmationQuestionUrl = outcome.ConfirmationQuestionUrl,
            SubjectText = outcome.SubjectText,
            TerminalConfirmedText = outcome.Confirmed,
            TerminalExpiredText = outcome.Expired,
            TerminalDeclinedText = outcome.Declined,
            TerminalErrorText = outcome.Error,
            UnifiedQrCodeBase64 = unifiedQr?.QrCodeBase64,
            UnifiedQrExpiresAt = unifiedQr?.ExpiresAt,
            UnifiedQrWithChannelQrCodes = unifiedQr is { WithChannelQrCodes: true },
            AnyChannelTabShowsChannelButtons = unifiedQr?.AnyChannelTabShowsChannelButtons ?? true,
            IsScannerPage = scanner.IsScannerPage,
            // The scanner page reports an outcome the browser of the sign-in window goes on from, and ends
            // on the button that closes its tab; the entry page of a confirmation is the page the calling
            // application opened, and the way on from it is that application's to arrange (SPEC-039 C15).
            OffersOutcomeClose = scanner.IsScannerPage,
            OutcomeCloseApplicationName = scanner.IsScannerPage
                ? OutcomeCloseAction.ApplicationName(
                    transaction, OutcomeCloseAction.OpenedOn(httpContext, _userAgentNormalizer))
                : null,
            SendsChannelLaunchedSignal = scanner.SendsChannelLaunchedSignal,
            CollectsHopFingerprint = scanner.CollectsFingerprint,
            HopLaunchTarget = scanner.LaunchTarget,
            TransactionType = transaction.Type
        };
    }

    /// <summary>
    /// What the page states about the outcome of its transaction and the one place it may take the browser
    /// to.
    /// </summary>
    /// <param name="ConfirmationQuestionUrl">The page of the core that asks the confirming question; null — none.</param>
    /// <param name="SubjectText">The subject of a confirmation the page shows; null — none.</param>
    /// <param name="Confirmed">Line of a confirmed outcome.</param>
    /// <param name="Expired">Line of an expired transaction.</param>
    /// <param name="Declined">Line of a transaction the USER declined.</param>
    /// <param name="Error">Line of a failure that is not the user's refusal; null — the renderer's own.</param>
    private readonly record struct PageOutcome(
        string? ConfirmationQuestionUrl,
        string? SubjectText,
        string Confirmed,
        string Expired,
        string Declined,
        string? Error);

    /// <summary>
    /// The outcome part of the entry page of a confirmation (SPEC-039 C15): the question of the core as its
    /// destination, the subject where the deployment shows it, and the four lines it ends on.
    /// </summary>
    /// <param name="transaction">The confirmation transaction.</param>
    /// <param name="sessionId">External session identifier of the transaction.</param>
    /// <param name="language">Language of the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome part of the context.</returns>
    private async Task<PageOutcome> ConfirmationOutcomeAsync(
        Transaction transaction,
        string sessionId,
        string language,
        CancellationToken cancellationToken)
    {
        var resolution = TransactionResolutionContext.For(transaction);

        // What is being confirmed — shown here only when the deployment put this surface into the set
        // of subject-display surfaces (SPEC-039 R38, CFG-104). The question itself stays on the page
        // that asks it: this one shows the subject and takes no answer (R36).
        var showsSubject = await _configurationResolver.ShowsSubjectOnAsync(
            resolution, ConfirmationSubjectSurface.InteractionPage, cancellationToken);

        var subjectText = showsSubject
            ? await ConfirmationSubjectText.ResolveAsync(_promptContextFactory, transaction, language, cancellationToken)
            : null;

        var lines = await ConfirmationOutcomeLinesAsync(transaction, language, showsSubject, cancellationToken);

        return new PageOutcome(
            // The one place this page does take the browser to: the core's own question, once the
            // channel has let the user in and the core is waiting for the answer (SPEC-039 E29).
            ConfirmationQuestionUrl: ConfirmationQuestionUrl(sessionId),
            SubjectText: subjectText,
            Confirmed: lines.Confirmed,
            Expired: lines.Expired,
            Declined: lines.Declined,
            Error: lines.Error);
    }

    /// <summary>
    /// The four lines a page of a confirmation transaction ends on. Such a page never leaves itself, so
    /// its status line is the LAST screen the user sees and may not word a sign-in that did not happen
    /// (SPEC-039 R51): the wording comes from the one port that words outcomes.
    /// </summary>
    /// <param name="transaction">The confirmation transaction.</param>
    /// <param name="language">Language of the page.</param>
    /// <param name="showsSubject">Whether the page shows the subject of the confirmation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lines.</returns>
    private async Task<TransactionOutcomeLines> ConfirmationOutcomeLinesAsync(
        Transaction transaction,
        string language,
        bool showsSubject,
        CancellationToken cancellationToken)
    {
        var resolution = TransactionResolutionContext.For(transaction);

        return new TransactionOutcomeLines(
            Confirmed: await ReceiptAsync(TransactionOutcome.Confirmed),
            Expired: await ReceiptAsync(TransactionOutcome.Expired),
            Declined: await ReceiptAsync(TransactionOutcome.Declined),
            // Not a receipt at all: a failure has no receipt kind, and the page says what its own neutral
            // page says, through the localizer of its own keys.
            Error: AuthPageLocalization.Localize(
                _promptLocalizer, TransactionEntryUnavailablePage.PageTitle, language));

        // The three receipts are three addresses — one per outcome — and therefore three resolutions;
        // they are asked of the port, not worded here, so this page introduces no terminal text of its
        // own (SPEC-036 TPL-123).
        ValueTask<string> ReceiptAsync(TransactionOutcome outcome) => _receiptText.RenderAsync(
            new OutcomeReceiptAddress(
                outcome,
                OutcomeReceiptSurfaces.InteractionPage,
                transaction.Type,
                transaction.ConfirmationSnapshot?.ActionType,
                // The channel is named only once one has taken part: before the user enters one, this
                // page's receipt narrows by no channel, exactly as any unnamed axis does.
                transaction.ChannelIdentitySnapshot?.ChannelType,
                transaction.InitiatorContextSnapshot?.DeviceType),
            language,
            // The zone the transaction states, beside the language of the page: the pair by which this
            // recipient reads a moment (TPL-016).
            transaction.GetUiTimeZone(),
            // No moment: this page words all three receipts BEFORE any of them happens — the script
            // shows whichever one the outcome turns out to be — so there is no moment of an outcome to
            // state here. The clock of the request would be the moment the page was built, which is
            // not what an outcome receipt reports.
            outcomeMoment: null,
            // The receipts are worded into the page before any decision, so they may name the subject
            // only where the page already shows it: a caller value in a receipt discloses no more than
            // the subject line beside it (SPEC-036 TPL-123, SPEC-039 R38). Outside that set the page
            // carries no caller value at all — only what the core states about the transaction
            // (TPL-124).
            showsSubject
                ? TransactionSlotSource.From(transaction)
                : TransactionSlotSource.From(transaction).WithoutCallerValues(),
            // The four terminal lines of one transaction are worded over that transaction's ownership,
            // so a wording an application or a ui_config record declares reaches the page (TPL-116).
            resolution,
            cancellationToken);
    }

    /// <summary>
    /// The outcome part of the scanner page of a sign-in: no destination and no subject, and the lines of
    /// the scanner.
    /// </summary>
    /// <param name="lines">The lines of the scanner of a sign-in.</param>
    /// <returns>The outcome part of the context.</returns>
    private static PageOutcome SignInScannerOutcome(TransactionOutcomeLines lines) =>
        new(
            ConfirmationQuestionUrl: null,
            SubjectText: null,
            Confirmed: lines.Confirmed,
            Expired: lines.Expired,
            Declined: lines.Declined,
            Error: lines.Error);

    /// <summary>
    /// The lines of the scanner page of a sign-in (SPEC-025 HOP-146, HOP-186): the success line points to
    /// the device where the sign-in started, and so does the line of a transaction that ran out of time or
    /// is no longer stored — the page cannot tell which of the two it is, so that line is worded for both.
    /// A refusal by the user reads as a refusal, as it does for a confirmation, worded for a sign-in. A
    /// failure keeps the line of the page that shows it.
    /// <para>
    /// Both lines are the core's own texts that reach the renderer READY, so the renderer cannot apply the
    /// text overrides to them — they are applied here, where the core picks its key. They are worded in
    /// the language the renderer will put the page into, the content language probed on the window's own
    /// key, so that a map of ready texts by language is matched against the language the page states.
    /// </para>
    /// </summary>
    /// <param name="texts">Text overrides stated by the levels of the transaction.</param>
    /// <param name="language">Language of the page.</param>
    /// <returns>The lines.</returns>
    private TransactionOutcomeLines SignInScannerOutcomeLines(CorePageTexts texts, string language)
    {
        var contentLanguage = AuthPageLocalization.ResolveContentLanguage(
            _promptLocalizer, AuthPageStrings.Title, language);

        return new TransactionOutcomeLines(
            Confirmed: texts.Localize(_promptLocalizer, AuthPageStrings.ScannerSignInConfirmed, contentLanguage),
            Expired: texts.Localize(_promptLocalizer, AuthPageStrings.ScannerSignInInactive, contentLanguage),
            Declined: texts.Localize(_promptLocalizer, AuthPageStrings.SignInDeclined, contentLanguage),
            Error: null);
    }

    /// <summary>
    /// Whether the transaction is a confirmation.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <returns><c>true</c> for a confirmation.</returns>
    private static bool IsConfirmation(Transaction transaction) =>
        string.Equals(transaction.Type, TransactionTypes.Confirmation, StringComparison.Ordinal);
}
