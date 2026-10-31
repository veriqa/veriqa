// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Endpoints;
using Veriqa.Core.AuthServer.HopMode;
using Veriqa.Core.AuthServer.Infrastructure;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.MessageTemplates;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.AuthServer.ClaimCompletion;

/// <summary>
/// Starts the web step of the claims completion (SPEC-041 L53) — the one place its surface is chosen: by the
/// setting <see cref="AuthServerConfigKeys.ClaimCompletionFormSurface"/>, by whether the sign-in was confirmed
/// on the confirmation page of the mail, by whether the channel account has an identifier to be shown by
/// (<see cref="ClaimCompletionAccountIdentifier"/>) and by whether a link button can be sent into the chat.
/// The surface is written with the transaction before the pages are told the step began.
/// </summary>
/// <remarks>
/// <para>
/// The surface of the device that confirmed the sign-in is the confirmation page of the mail, which issues the
/// form token itself, or the page opened by the link button of the channel (SPEC-041 C76). The mail page
/// confirmed the sign-in only where the effective confirmation surface is <c>None</c>: on any other surface
/// the letter only links the mailbox, the "Yes" of the sign-in window confirms, and the mail page has already
/// answered and shows no form. For the link button the starter issues the token, writes its fingerprint and
/// the surface, and delivers the link in the background.
/// An undelivered link moves the surface to the sign-in page and tells the pages the step again, unless the
/// setting is the strict <see cref="ClaimFormSurface.CrossDevicePageOnly"/>.
/// </para>
/// <para>
/// A web step the strict setting leaves with nothing to collect by — the link cannot be sent or was not
/// delivered — ends only outside <see cref="StartAsync"/>, in the background of the delivery seam: failing or
/// skipping inside the call would finish the transaction under the engine that is starting the step.
/// </para>
/// <para>
/// Scoped: it reads the channel adapters, whose lifetime a host may narrow; the engine resolves it from a
/// scope of its own. What runs in the background uses only the singletons the starter holds, and takes the
/// transaction service from a scope opened there. The transaction service is never taken in the
/// constructor — the service owns the call that starts the step.
/// </para>
/// </remarks>
internal sealed class WebStepClaimCompletionStarter : IClaimCompletionStepStarter
{
    /// <summary>
    /// Store the surface and the fingerprint of the form token are written into.
    /// </summary>
    private readonly ITransactionStore _store;

    /// <summary>
    /// Registered channel adapters — whether the channel of the transaction can show a link button, and the
    /// facts the confirmation surface of its channel is resolved by.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Resolver of the effective confirmation surface — whether the mail page is where the sign-in was confirmed.
    /// </summary>
    private readonly IEffectiveConfirmationSurfaceResolver _surfaceResolver;

    /// <summary>
    /// Resolver of the surface setting and of the issuer the link is built on.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Channel message localizer of the link button message: the text above the button and its label.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _promptLocalizer;

    /// <summary>
    /// Accessor of the message the link button is sent with (<see cref="MessageKinds.ClaimFormLink"/>).
    /// </summary>
    private readonly IMessageTemplateAccessor _messageTemplates;

    /// <summary>
    /// The seam the link is delivered into the chat through, and the background the strict outcome runs in.
    /// </summary>
    private readonly IClaimCompletionChannelDelivery _delivery;

    /// <summary>
    /// Publisher of the channel status and of the repeated step signal.
    /// </summary>
    private readonly ITransactionEventPublisher _eventPublisher;

    /// <summary>
    /// Clock the moment of the published events is taken from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Factory of the scope the strict outcome takes the transaction service from.
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<WebStepClaimCompletionStarter> _logger;

    /// <summary>
    /// Creates the starter of the web step.
    /// </summary>
    /// <param name="store">Store the surface and the fingerprint are written into.</param>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="surfaceResolver">Resolver of the effective confirmation surface.</param>
    /// <param name="resolver">Resolver of the surface setting and of the issuer.</param>
    /// <param name="promptLocalizer">Channel message localizer.</param>
    /// <param name="messageTemplates">Accessor of the message the link button is sent with.</param>
    /// <param name="delivery">The delivery seam of the claims completion.</param>
    /// <param name="eventPublisher">Publisher of transaction events.</param>
    /// <param name="timeProvider">Clock of the published events.</param>
    /// <param name="scopeFactory">Factory of the scope of the strict outcome.</param>
    /// <param name="logger">Logger.</param>
    public WebStepClaimCompletionStarter(
        ITransactionStore store,
        IEnumerable<IChannelAdapter> adapters,
        IEffectiveConfirmationSurfaceResolver surfaceResolver,
        IConfigurationResolver resolver,
        IConfirmationPromptLocalizer promptLocalizer,
        IMessageTemplateAccessor messageTemplates,
        IClaimCompletionChannelDelivery delivery,
        ITransactionEventPublisher eventPublisher,
        TimeProvider timeProvider,
        IServiceScopeFactory scopeFactory,
        ILogger<WebStepClaimCompletionStarter> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _surfaceResolver = surfaceResolver ?? throw new ArgumentNullException(nameof(surfaceResolver));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _promptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        _messageTemplates = messageTemplates ?? throw new ArgumentNullException(nameof(messageTemplates));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ClaimCompletionStep Step => ClaimCompletionStep.WebStep;

    /// <inheritdoc />
    /// <remarks>
    /// The surface is written only where none is written yet. A refused write — the surface is already there
    /// (a repeated start), or the transaction has left the completion — ends the start: nothing more is
    /// written or sent, and a link sent by an earlier start stays in force. The setting is resolved for the
    /// ownership context the transaction carries; the delivery seam sends under the tenant of the snapshot.
    /// </remarks>
    public async Task StartAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        // SPEC-041 L53: the setting names a target; the target of the device that confirmed the sign-in is
        // the confirmation page of the mail when the sign-in was confirmed there, otherwise the link button,
        // and when the link cannot be sent the setting decides between the sign-in page and no surface at all.
        ArgumentNullException.ThrowIfNull(transaction);

        var snapshot = transaction.ChannelIdentitySnapshot;
        using var resolutionScope = new ConfigResolutionScope(TransactionResolutionContext.For(transaction));

        var setting = (await _resolver.ResolveAsync(
                AuthServerConfigKeys.ClaimCompletionFormSurface,
                resolutionScope,
                ConfigDimensionValues.None,
                cancellationToken))
            .Value;

        var confirmedOnMailPage = await IsConfirmedOnMailPageAsync(transaction, snapshot, cancellationToken);
        var hasIdentifier = snapshot is not null && ClaimCompletionAccountIdentifier.Resolve(snapshot) is not null;

        if (TargetOf(setting, confirmedOnMailPage, hasIdentifier) is WebStepSurface.InitiatingPage)
        {
            await WriteSurfaceAsync(transaction.Id, WebStepSurface.InitiatingPage, cancellationToken);
            return;
        }

        // The confirmation page of the mail issues the form token itself, when it shows the form.
        if (confirmedOnMailPage)
        {
            await WriteSurfaceAsync(transaction.Id, WebStepSurface.CrossDevicePage, cancellationToken);
            return;
        }

        if (snapshot is not null && await LinkBaseAsync(snapshot, resolutionScope, cancellationToken) is { } linkBase)
        {
            await StartLinkAsync(transaction, snapshot, linkBase, setting, cancellationToken);
            return;
        }

        if (setting is ClaimFormSurface.CrossDevicePageOnly)
        {
            // The surface states where the form lives, and the write guards against a repeated start: only the
            // start that wrote it ends the step.
            if (await WriteSurfaceAsync(transaction.Id, WebStepSurface.CrossDevicePage, cancellationToken))
            {
                var transactionId = transaction.Id;
                _delivery.RunInBackground(stopping => EndWithoutFormAsync(transactionId, stopping));
            }

            return;
        }

        _logger.LogWarning(
            "The claims completion form falls back to the sign-in page: the link button cannot be sent into the channel — the channel does not show one, or the issuer is no absolute HTTPS URL. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
            transaction.Id.ToString(),
            snapshot?.ChannelType);

        await WriteSurfaceAsync(transaction.Id, WebStepSurface.InitiatingPage, cancellationToken);
    }

    /// <summary>
    /// The target the setting names for a web step (SPEC-041 L53) — before the question of whether a link can
    /// be sent, which only a target of the device that confirmed the sign-in asks.
    /// </summary>
    /// <param name="setting">Value of the surface setting.</param>
    /// <param name="confirmedOnMailPage">The sign-in was confirmed on the confirmation page of the mail.</param>
    /// <param name="hasIdentifier">The channel account has an identifier to be shown by.</param>
    /// <returns>The target surface.</returns>
    private static WebStepSurface TargetOf(ClaimFormSurface setting, bool confirmedOnMailPage, bool hasIdentifier) =>
        setting switch
        {
            ClaimFormSurface.InitiatingPage => WebStepSurface.InitiatingPage,
            ClaimFormSurface.CrossDevicePage or ClaimFormSurface.CrossDevicePageOnly => WebStepSurface.CrossDevicePage,
            _ => confirmedOnMailPage || !hasIdentifier ? WebStepSurface.CrossDevicePage : WebStepSurface.InitiatingPage
        };

    /// <summary>
    /// Whether the sign-in was confirmed on the confirmation page of the mail: the Email channel in Pull
    /// mode, stated by the mode claim of the snapshot, on the effective confirmation surface <c>None</c> for
    /// the adapter of the channel of the snapshot. On any other surface the "Yes" of the sign-in window
    /// confirmed it, and the mail page — already answered — would never show the form; an adapter that is not
    /// registered leaves nothing to resolve the surface by, and the mail page is not taken either.
    /// </summary>
    /// <param name="transaction">Transaction in the web step — the context the surface is resolved for.</param>
    /// <param name="snapshot">Channel snapshot of the transaction; null — none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true for an Email Pull confirmation made on the mail page.</returns>
    private async Task<bool> IsConfirmedOnMailPageAsync(
        Transaction transaction,
        ChannelIdentitySnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot is null
            || !string.Equals(snapshot.ChannelType, ChannelTypes.Email, StringComparison.Ordinal)
            || snapshot.AdditionalClaims is not { } claims
            || !claims.TryGetValue(VeriqaClaimTypes.VeriqaEmailMode, out var mode)
            || !string.Equals(mode, VeriqaEmailModes.Pull, StringComparison.Ordinal))
        {
            return false;
        }

        var adapter = ChannelWebhookPipeline.FindAdapter(_adapters, snapshot.ChannelType);
        if (adapter is null)
        {
            return false;
        }

        return await _surfaceResolver.ResolveEffectiveSurfaceAsync(transaction, adapter, cancellationToken)
            is ConfirmationSurface.None;
    }

    /// <summary>
    /// The base the link to the form is built on, when a link button can be sent: the adapter of the channel
    /// of the snapshot declares the link button kind, and the issuer is an absolute HTTPS URL — the form of the
    /// base of a hop URL (SPEC-025 HOP-123). The settings of the hop mode are not read (SPEC-041 L89).
    /// </summary>
    /// <param name="snapshot">Channel snapshot of the transaction.</param>
    /// <param name="resolutionScope">Resolution scope of the transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The base; null — no link button can be sent.</returns>
    private async Task<string?> LinkBaseAsync(
        ChannelIdentitySnapshot snapshot,
        ConfigResolutionScope resolutionScope,
        CancellationToken cancellationToken)
    {
        var adapter = ChannelWebhookPipeline.FindAdapter(_adapters, snapshot.ChannelType);
        if (adapter is null || !adapter.Capabilities.SupportedMessageKinds.Contains(ChannelMessageKind.ClaimFormLink))
        {
            return null;
        }

        var issuer = (await _resolver.ResolveAsync(
                AuthServerConfigKeys.Issuer,
                resolutionScope,
                ConfigDimensionValues.None,
                cancellationToken))
            .Value;

        return HopUrlForm.NormalizeBase(issuer);
    }

    /// <summary>
    /// Issues the form token, writes its fingerprint and the surface of the device that confirmed the sign-in,
    /// and delivers the link button into the chat in the background (SPEC-041 N78, C76). A message that
    /// cannot be resolved is not sent, and the step goes on as for a link the channel did not deliver.
    /// </summary>
    /// <param name="transaction">Transaction in the web step.</param>
    /// <param name="snapshot">Its channel snapshot.</param>
    /// <param name="linkBase">Base the link is built on.</param>
    /// <param name="setting">Value of the surface setting — what an undelivered link leads to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed start.</returns>
    private async Task StartLinkAsync(
        Transaction transaction,
        ChannelIdentitySnapshot snapshot,
        string linkBase,
        ClaimFormSurface setting,
        CancellationToken cancellationToken)
    {
        var (token, hash) = ClaimFormToken.Issue();

        // A refused write means a token was issued before (a repeated start) or the transaction left the
        // completion: the new token goes to nobody, and the link sent before stays in force.
        if (!await _store.TrySetClaimFormTokenHashAsync(transaction.Id, hash, cancellationToken))
        {
            _logger.LogDebug(
                "No form token issued for the web step: one was issued already, or the transaction left the claims completion. TransactionId: {TransactionId}",
                transaction.Id.ToString());
            return;
        }

        if (!await WriteSurfaceAsync(transaction.Id, WebStepSurface.CrossDevicePage, cancellationToken))
        {
            return;
        }

        var text = await RenderLinkTextAsync(transaction, snapshot, cancellationToken);
        if (text is null)
        {
            // In the background, as the undelivered link itself: the strict setting ends the step there, and
            // never inside the start.
            var transactionId = transaction.Id;
            _delivery.RunInBackground(stopping => AfterUndeliveredLinkAsync(transactionId, setting, stopping));
            return;
        }

        // The language of the account when the channel reports one, else the language of the sign-in — as
        // the menu and the receipts take it, so the link does not fall back to the base text alone.
        var locale = snapshot.Locale ?? transaction.GetUiLocale();
        var message = new ChannelMessage
        {
            ChannelUserId = snapshot.ChannelUserId,
            Text = text,
            Kind = ChannelMessageKind.ClaimFormLink,
            ClaimFormLink = new ClaimFormLinkContent
            {
                Url = linkBase + ClaimCompletionFormPageEndpoint.BuildPath(SessionIdMapper.ToSessionId(transaction.Id), token),
                ButtonLabel = _promptLocalizer.ResolveOrBaseText(ClaimFormLinkTexts.ButtonLabel, locale)
            }
        };

        _delivery.DeliverInBackground(
            transaction,
            message,
            (delivered, stopping) => delivered
                ? ClaimFormChannelStatus.PublishFormOnPhoneAsync(
                    _eventPublisher, _timeProvider, transaction, snapshot.ChannelType, _logger)
                : AfterUndeliveredLinkAsync(transaction.Id, setting, stopping));

        _logger.LogInformation(
            "Claims completion form link queued for delivery to the channel. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
            transaction.Id.ToString(),
            snapshot.ChannelType);
    }

    /// <summary>
    /// Resolves the message above the link button at the address of the transaction
    /// (<see cref="MessageKinds.ClaimFormLink"/>) and renders it, plain, for the user of the channel snapshot.
    /// </summary>
    /// <remarks>
    /// The render point states everything it knows: the whole point of the dimensions, the ownership of the
    /// transaction, the locale of the snapshot (else of the transaction), the zone moments are shown in and
    /// the server values of the transaction, with the initiator details passed through the one display
    /// decision (SPEC-017 ICC-081).
    /// The caller values are left out: they were accepted against the contract of another message. Which of
    /// the values reach the text is decided by the contract and the wording a level declares. A resolution
    /// that fails is a warning and no text, never an exception out of the start; a cancellation of the token
    /// is not caught, while one the token did not ask for (a timeout inside a store) is a failed resolution.
    /// </remarks>
    /// <param name="transaction">Transaction in the web step.</param>
    /// <param name="snapshot">Its channel snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The text; null — no level declares the message, no step of its ladder states a plain text,
    /// or the resolution failed.</returns>
    private async Task<string?> RenderLinkTextAsync(
        Transaction transaction,
        ChannelIdentitySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var ownership = TransactionResolutionContext.For(transaction);

            var template = await _messageTemplates.FindAsync(
                InChannelMessageAddress.Of(MessageKinds.ClaimFormLink, transaction, snapshot.ChannelType),
                ownership,
                cancellationToken);

            if (template is null)
            {
                _logger.LogWarning(
                    "No level declares a message of kind {MessageKind} — the claims completion form link is not sent. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                    MessageKinds.ClaimFormLink,
                    transaction.Id.ToString(),
                    snapshot.ChannelType);
                return null;
            }

            var source = TransactionSlotSource.From(transaction).WithoutCallerValues();
            if (source.InitiatorContext is { } initiator)
            {
                var display = await InitiatorContextDisplay.ResolveAsync(_resolver, ownership, cancellationToken);
                source = source with { InitiatorContext = display.Apply(initiator) };
            }

            var text = MessageTextRenderer.Render(
                template,
                TransactionSlotValues.Of(source, template.Contract, outcomeMoment: null, MessageSurfaces.InChannel),
                snapshot.Locale ?? transaction.GetUiLocale(),
                transaction.GetUiTimeZone(),
                _promptLocalizer,
                MessageRenderMode.PlainText,
                _logger);

            if (text is null)
            {
                _logger.LogWarning(
                    "No step of the resolved ladder of message kind {MessageKind} states a plain text — the claims completion form link is not sent. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                    template.Kind,
                    transaction.Id.ToString(),
                    snapshot.ChannelType);
            }

            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "The message of the claims completion form link could not be resolved — the link is not sent. TransactionId: {TransactionId}, ChannelType: {ChannelType}",
                transaction.Id.ToString(),
                snapshot.ChannelType);
            return null;
        }
    }

    /// <summary>
    /// Handles a link the channel did not deliver after the retries, or never got to send because its message
    /// could not be resolved (SPEC-041 L53): the strict setting ends the
    /// web step with nothing to collect by; any other setting moves the surface to the sign-in page and tells
    /// the pages the step again, so the page of the device the sign-in was started on shows the form.
    /// </summary>
    /// <param name="transactionId">Transaction whose link was not delivered.</param>
    /// <param name="setting">Value of the surface setting.</param>
    /// <param name="stopping">Stopping token of the host.</param>
    /// <returns>The completed operation.</returns>
    private async Task AfterUndeliveredLinkAsync(
        TransactionId transactionId,
        ClaimFormSurface setting,
        CancellationToken stopping)
    {
        if (setting is ClaimFormSurface.CrossDevicePageOnly)
        {
            await EndWithoutFormAsync(transactionId, stopping);
            return;
        }

        if (!await _store.TrySetWebStepSurfaceAsync(
                transactionId,
                expected: WebStepSurface.CrossDevicePage,
                WebStepSurface.InitiatingPage,
                stopping))
        {
            _logger.LogDebug(
                "The undelivered form link changes nothing: the transaction left the claims completion. TransactionId: {TransactionId}",
                transactionId.ToString());
            return;
        }

        _logger.LogWarning(
            "The claims completion form link was not delivered; the form moves to the sign-in page. TransactionId: {TransactionId}",
            transactionId.ToString());

        var current = await _store.GetByIdAsync(transactionId, stopping);
        if (current?.GetCurrentClaimCompletionStep() is not { } step)
        {
            return;
        }

        // The step signal is a fact about the store as it now stands: it is published whatever happens to the
        // host, like the channel statuses.
        await _eventPublisher.PublishAsync(
            new TransactionClaimCompletionStepEvent
            {
                TransactionId = current.Id,
                OccurredAt = _timeProvider.GetUtcNow(),
                Step = step,
                Surface = current.ClaimCompletion?.Surface,
                Context = TransactionEventContext.FromTransaction(current)
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Ends a web step the strict setting leaves with nothing to collect by (SPEC-041 L53): a missing
    /// <c>Required</c> claim fails the transaction as unobtainable; otherwise every open claim of the web step
    /// is skipped. Runs in the background only — never inside <see cref="StartAsync"/>.
    /// </summary>
    /// <remarks>
    /// The open claims are read again after each round of skips: the completion check a skip runs may take
    /// vanished stored values off (SPEC-041 E86) and reopen claims of this very step, and the start that
    /// begins the reopened step again finds the surface or the form token written and does not run this end
    /// a second time.
    /// Each reopening takes a stored value off for good, so the rounds end.
    /// </remarks>
    /// <param name="transactionId">Transaction in the web step.</param>
    /// <param name="stopping">Stopping token of the host.</param>
    /// <returns>The completed operation.</returns>
    private async Task EndWithoutFormAsync(TransactionId transactionId, CancellationToken stopping)
    {
        // The transaction is read again, from a scope of the background: it may have moved on since the start.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var transactionService = scope.ServiceProvider.GetRequiredService<ITransactionService>();

        // A host that composes no channel contour has no performer of the post-completion action.
        var completionBehavior = scope.ServiceProvider.GetService<ICompletionBehaviorDispatcher>();

        // Nor a reporter of the outcome a sign-in ended here leaves in the channel.
        var outcomeReporter = scope.ServiceProvider.GetService<IClaimCompletionOutcomeReporter>();

        while (await EndOpenWebStepItemsAsync(
                   transactionService, outcomeReporter, completionBehavior, transactionId, stopping))
        {
        }
    }

    /// <summary>
    /// One round of <see cref="EndWithoutFormAsync"/> over the claims of the web step open now.
    /// </summary>
    /// <param name="transactionService">Transaction service of the background scope.</param>
    /// <param name="outcomeReporter">Reporter of the outcome of a sign-in this end fails or completes,
    /// into the channel the user came through (SPEC-003 CA-142); null — the host composes no channel
    /// contour.</param>
    /// <param name="completionBehavior">Performer of the post-completion action of a sign-in a skip
    /// completes (SPEC-012 §4.9); null — the host composes no channel contour.</param>
    /// <param name="transactionId">Transaction in the web step.</param>
    /// <param name="stopping">Stopping token of the host.</param>
    /// <returns>true — every open claim was skipped, and the web step has to be read again; false — the end
    /// is over.</returns>
    private async Task<bool> EndOpenWebStepItemsAsync(
        ITransactionService transactionService,
        IClaimCompletionOutcomeReporter? outcomeReporter,
        ICompletionBehaviorDispatcher? completionBehavior,
        TransactionId transactionId,
        CancellationToken stopping)
    {
        var read = await transactionService.GetTransactionAsync(transactionId, stopping);
        if (read.IsFailure
            || read.Value.GetCurrentClaimCompletionStep() is not ClaimCompletionStep.WebStep)
        {
            return false;
        }

        var transaction = read.Value;
        var open = ClaimCompletionFormModelBuilder.OpenWebStepItems(transaction);

        if (open.Any(item => item.Level is ClaimRequirementLevel.Required))
        {
            _logger.LogWarning(
                "The claims completion form has no surface to be shown on, and a required claim is missing: the sign-in fails. TransactionId: {TransactionId}",
                transactionId.ToString());

            var failed = await transactionService.FailTransactionAsync(
                transactionId,
                TransactionErrorCodes.RequiredClaimUnobtainable,
                transaction.ConcurrencyToken,
                stopping);

            if (failed.IsFailure)
            {
                _logger.LogWarning(
                    "Failed to move the transaction with an unobtainable required claim to Failed. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                    transactionId.ToString(),
                    failed.Error.Code);
            }
            else if (outcomeReporter is not null)
            {
                // CancellationToken.None — the sign-in has already ended and the reporter spends the
                // stored references whatever happens next: a host that stops between the two would
                // lose the receipt for good.
                await outcomeReporter.ReportAsync(failed.Value, CancellationToken.None);
            }

            return false;
        }

        _logger.LogWarning(
            "The claims completion form has no surface to be shown on: the optional claims of the web step are skipped. TransactionId: {TransactionId}, Skipped: {SkippedCount}",
            transactionId.ToString(),
            open.Count);

        foreach (var item in open)
        {
            var skipped = await transactionService.SkipClaimAsync(transactionId, item.Claim, stopping);
            if (skipped.IsFailure)
            {
                _logger.LogWarning(
                    "Failed to skip an optional claim of the web step. TransactionId: {TransactionId}, ErrorCode: {ErrorCode}",
                    transactionId.ToString(),
                    skipped.Error.Code);
                return false;
            }

            // The skip of the last open claim ends the sign-in: the channel the user came through is
            // shown the outcome first — the reporter leaves a sign-in that has not ended alone — and
            // then the post-completion action of a completed one is performed there, on the instance
            // the write returned. CancellationToken.None on the report, for the reason given above.
            if (outcomeReporter is not null)
            {
                await outcomeReporter.ReportAsync(skipped.Value, CancellationToken.None);
            }

            if (skipped.Value.State is TransactionState.Completed && completionBehavior is not null)
            {
                await completionBehavior.DispatchAsync(skipped.Value, stopping);
            }
        }

        return true;
    }

    /// <summary>
    /// Writes the surface of the web step where none is written yet.
    /// </summary>
    /// <param name="transactionId">Transaction in the web step.</param>
    /// <param name="surface">Surface to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true — written by this call; false — a surface is already there, or the transaction left the
    /// completion.</returns>
    private async Task<bool> WriteSurfaceAsync(
        TransactionId transactionId,
        WebStepSurface surface,
        CancellationToken cancellationToken)
    {
        var written = await _store.TrySetWebStepSurfaceAsync(
            transactionId,
            expected: null,
            surface,
            cancellationToken);

        if (!written)
        {
            _logger.LogDebug(
                "The surface of the web step was not written: one is already there, or the transaction left the claims completion. TransactionId: {TransactionId}",
                transactionId.ToString());
        }

        return written;
    }
}
