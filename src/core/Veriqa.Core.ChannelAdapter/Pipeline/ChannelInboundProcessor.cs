// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Identity;
using Veriqa.Core.TransactionEngine.Services;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Acts on the result of an inbound channel event (SPEC-003 §13): starts, confirms or declines the
/// transaction it names, takes the answers of the phone step of claim completion (SPEC-041 L44, L73),
/// answers an event that names none. Every path an event reaches the core by
/// goes through it — the webhook route, the polling loops, the inbound routes of the Email channel and
/// the replay of the sandbox — so each of them drives the very same processing.
/// </summary>
/// <remarks>
/// A scoped service built by the factory <c>AddVeriqaChannelAdapters</c> registers — it cannot be
/// constructed outside the component. The scope is the one of the event: the request of the webhook or
/// of the Email route, or the scope a polling loop opens for each update, so a collaborator the host
/// registered as scoped is taken per event and never captured by a long-lived owner. The call itself
/// carries only the data of the event.
/// </remarks>
public sealed class ChannelInboundProcessor
{
    /// <summary>
    /// Core-level declarations of the registered channels.
    /// </summary>
    private readonly IEnumerable<CoreChannelDeclaration> _channelDeclarations;

    /// <summary>
    /// Creates the processor over the services of the scope of one event.
    /// </summary>
    /// <param name="transactionService">Transaction service.</param>
    /// <param name="transactionStore">
    /// Transaction store. A decision reported by a channel is read through it rather than through the
    /// TTL gate of the service: an expiry is an outcome the processing answers with a receipt of its
    /// own (SPEC-003 CA-192), and the gate hides the very transaction that receipt is worded from.
    /// </param>
    /// <param name="timeProvider">
    /// Clock the deadline of a transaction is read against — the one the host registered, so the answer
    /// to a decision made after the deadline is the same before and after the cleanup pass.
    /// </param>
    /// <param name="identityResolutionService">Channel identity resolution service.</param>
    /// <param name="logger">Logger of the processing.</param>
    /// <param name="metrics">Component metrics a failed user notification is counted into.</param>
    /// <param name="userAuthRateLimiter">Per-user rate limiter; null — the host registered none (it
    /// belongs to the auth server), and nothing is limited.</param>
    /// <param name="confirmationPromptOrchestrator">Confirmation prompt orchestrator.</param>
    /// <param name="promptLocalizer">Channel message localizer.</param>
    /// <param name="promptMessageStore">Store of sent-prompt coordinates.</param>
    /// <param name="receiptText">Terminal text of the transaction (SPEC-036 TPL-123).</param>
    /// <param name="displayIntentSource">Port the desired display intent of the receipt comes from.</param>
    /// <param name="eventPublisher">Bus the refusals of the interaction surface are written to
    /// (SPEC-039 E48); null — the composition has no transaction engine and therefore no journal, and
    /// the refusals reach the log alone.</param>
    /// <param name="channelDeclarations">Core-level declarations of the registered channels — the owner
    /// of a registration (SPEC-042 R60).</param>
    /// <param name="phoneRequestCorrelationStore">Record of the last phone number request sent into each
    /// chat — how an answer to the request, which names no transaction, finds the one it belongs to
    /// (SPEC-041 L44).</param>
    /// <param name="phoneNumberRequestBuilder">Builder of the phone number request, with which a
    /// contact that is not the sender's own is answered by asking again (SPEC-041 L46).</param>
    /// <param name="claimCompletionDelivery">The seam claim completion delivers its messages into the
    /// user's chat through.</param>
    /// <param name="completionBehaviorDispatcher">Performer of the post-completion action of a sign-in
    /// an event of the channel completes (SPEC-012 §4.9).</param>
    /// <param name="webConfirmationNoticeSender">Sender of the message a sign-in sends into the chat
    /// when its question is handed over to the core page (SPEC-012 §4.4.2).</param>
    /// <param name="menuItemPressHandlerSource">Where the handler of the host comes from when a callback
    /// item of the post-completion menu is pressed (SPEC-012 CFG-094): asked at the press and never
    /// before, so the handler is neither built for an event that is not a press nor able to fail one by
    /// failing to be built. It answers null when the host registered none. Null — the composition
    /// names no source, and a press is left without processing.</param>
    /// <param name="startCodeStore">Store of the short codes of transactions — how a sign-in start
    /// that names a code instead of a transaction finds the transaction it stands for. Null — the
    /// composition names no store, and such a start is answered as a code nobody issued.</param>
    /// <param name="letterBudgetChargeMark">Marks of the letters on the budget of the confirmation operation —
    /// how a letter reaching the gate again costs its sender no second attempt. Null — no channel of the
    /// composition sends letters that mark it, and every pass is charged.</param>
    /// <param name="inboundEventDeduplicator">The dropping of a repeated delivery of an event within the
    /// short window of the installation. Null — the composition names none, and every delivery is
    /// processed.</param>
    /// <param name="unmatchedStartCodeBudget">The budget of start codes that took no record, per pair
    /// "tenant of the event + channel type" — how guessing codes is bounded where the sender of an event
    /// is whatever its source names. Null — the composition names none, and nothing bounds it beyond the
    /// limit per channel user.</param>
    internal ChannelInboundProcessor(
        ITransactionService transactionService,
        ITransactionStore transactionStore,
        TimeProvider timeProvider,
        IIdentityResolutionService identityResolutionService,
        ILogger<ChannelInboundProcessor> logger,
        ChannelAdapterMetrics metrics,
        IUserAuthRateLimiter? userAuthRateLimiter,
        IConfirmationPromptOrchestrator confirmationPromptOrchestrator,
        IConfirmationPromptLocalizer promptLocalizer,
        IChannelPromptMessageStore promptMessageStore,
        IOutcomeReceiptText receiptText,
        IOutcomeNoticeDisplayIntentSource displayIntentSource,
        ITransactionEventPublisher? eventPublisher,
        IEnumerable<CoreChannelDeclaration> channelDeclarations,
        IPhoneRequestCorrelationStore phoneRequestCorrelationStore,
        PhoneNumberRequestBuilder phoneNumberRequestBuilder,
        IClaimCompletionChannelDelivery claimCompletionDelivery,
        ICompletionBehaviorDispatcher completionBehaviorDispatcher,
        WebConfirmationNoticeSender webConfirmationNoticeSender,
        Func<IMenuItemPressHandler?>? menuItemPressHandlerSource = null,
        IChannelStartCodeStore? startCodeStore = null,
        ILetterBudgetChargeMark? letterBudgetChargeMark = null,
        InboundEventDeduplicator? inboundEventDeduplicator = null,
        UnmatchedStartCodeBudget? unmatchedStartCodeBudget = null)
    {
        TransactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
        TransactionStore = transactionStore ?? throw new ArgumentNullException(nameof(transactionStore));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        IdentityResolutionService = identityResolutionService
            ?? throw new ArgumentNullException(nameof(identityResolutionService));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        UserAuthRateLimiter = userAuthRateLimiter;

        // The two collaborators that decide whether a confirmation question gets ASKED are not
        // optional: a processor without them would answer every confirmation transaction by dropping
        // the decision, and it would do so silently (SPEC-039 E41). A path that asks no question in
        // the chat states that per call instead.
        ConfirmationPromptOrchestrator = confirmationPromptOrchestrator
            ?? throw new ArgumentNullException(nameof(confirmationPromptOrchestrator));
        PromptLocalizer = promptLocalizer ?? throw new ArgumentNullException(nameof(promptLocalizer));
        PromptMessageStore = promptMessageStore ?? throw new ArgumentNullException(nameof(promptMessageStore));

        ReceiptText = receiptText ?? throw new ArgumentNullException(nameof(receiptText));
        DisplayIntentSource = displayIntentSource ?? throw new ArgumentNullException(nameof(displayIntentSource));
        EventPublisher = eventPublisher;
        _channelDeclarations = channelDeclarations ?? throw new ArgumentNullException(nameof(channelDeclarations));
        PhoneRequestCorrelationStore = phoneRequestCorrelationStore
            ?? throw new ArgumentNullException(nameof(phoneRequestCorrelationStore));
        PhoneNumberRequestBuilder = phoneNumberRequestBuilder
            ?? throw new ArgumentNullException(nameof(phoneNumberRequestBuilder));
        ClaimCompletionDelivery = claimCompletionDelivery
            ?? throw new ArgumentNullException(nameof(claimCompletionDelivery));
        CompletionBehaviorDispatcher = completionBehaviorDispatcher
            ?? throw new ArgumentNullException(nameof(completionBehaviorDispatcher));
        WebConfirmationNoticeSender = webConfirmationNoticeSender
            ?? throw new ArgumentNullException(nameof(webConfirmationNoticeSender));
        MenuItemPressHandlerSource = menuItemPressHandlerSource;
        StartCodeStore = startCodeStore;
        LetterBudgetChargeMark = letterBudgetChargeMark;
        InboundEventDeduplicator = inboundEventDeduplicator;
        UnmatchedStartCodeBudget = unmatchedStartCodeBudget;
    }

    /// <summary>
    /// Transaction service.
    /// </summary>
    internal ITransactionService TransactionService { get; }

    /// <summary>
    /// Transaction store a reported decision is read through.
    /// </summary>
    internal ITransactionStore TransactionStore { get; }

    /// <summary>
    /// Clock the deadline of a transaction is read against.
    /// </summary>
    internal TimeProvider TimeProvider { get; }

    /// <summary>
    /// Channel identity resolution service.
    /// </summary>
    internal IIdentityResolutionService IdentityResolutionService { get; }

    /// <summary>
    /// Logger of the processing.
    /// </summary>
    internal ILogger Logger { get; }

    /// <summary>
    /// Component metrics.
    /// </summary>
    internal ChannelAdapterMetrics Metrics { get; }

    /// <summary>
    /// Per-user rate limiter; null — none is registered.
    /// </summary>
    internal IUserAuthRateLimiter? UserAuthRateLimiter { get; }

    /// <summary>
    /// Confirmation prompt orchestrator.
    /// </summary>
    internal IConfirmationPromptOrchestrator ConfirmationPromptOrchestrator { get; }

    /// <summary>
    /// Channel message localizer.
    /// </summary>
    internal IConfirmationPromptLocalizer PromptLocalizer { get; }

    /// <summary>
    /// Store of sent-prompt coordinates.
    /// </summary>
    internal IChannelPromptMessageStore PromptMessageStore { get; }

    /// <summary>
    /// Terminal text of the transaction.
    /// </summary>
    internal IOutcomeReceiptText ReceiptText { get; }

    /// <summary>
    /// Port the desired display intent of the receipt comes from.
    /// </summary>
    internal IOutcomeNoticeDisplayIntentSource DisplayIntentSource { get; }

    /// <summary>
    /// Bus the refusals of the interaction surface are written to; null — no journal.
    /// </summary>
    internal ITransactionEventPublisher? EventPublisher { get; }

    /// <summary>
    /// Record of the last phone number request sent into each chat.
    /// </summary>
    internal IPhoneRequestCorrelationStore PhoneRequestCorrelationStore { get; }

    /// <summary>
    /// Builder of the phone number request.
    /// </summary>
    internal PhoneNumberRequestBuilder PhoneNumberRequestBuilder { get; }

    /// <summary>
    /// The seam claim completion delivers its messages into the user's chat through.
    /// </summary>
    internal IClaimCompletionChannelDelivery ClaimCompletionDelivery { get; }

    /// <summary>
    /// Performer of the post-completion action of a completed sign-in (SPEC-012 §4.9).
    /// </summary>
    internal ICompletionBehaviorDispatcher CompletionBehaviorDispatcher { get; }

    /// <summary>
    /// Sender of the message a sign-in sends into the chat when its question is handed over to the core
    /// page (SPEC-012 §4.4.2).
    /// </summary>
    internal WebConfirmationNoticeSender WebConfirmationNoticeSender { get; }

    /// <summary>
    /// Where the handler of the host a menu item press is handed to comes from; null — no source. The
    /// handler is taken from the scope of the event at the press itself: creating it is the host's
    /// code, and it must not run — let alone fail — for an event that is not a press.
    /// </summary>
    internal Func<IMenuItemPressHandler?>? MenuItemPressHandlerSource { get; }

    /// <summary>
    /// Store of the short codes of transactions a start by code is looked up in; null — no store.
    /// </summary>
    internal IChannelStartCodeStore? StartCodeStore { get; }

    /// <summary>
    /// Marks of the letters on the budget of the confirmation operation; null — none.
    /// </summary>
    internal ILetterBudgetChargeMark? LetterBudgetChargeMark { get; }

    /// <summary>
    /// The dropping of a repeated delivery of an event within the window; null — none.
    /// </summary>
    internal InboundEventDeduplicator? InboundEventDeduplicator { get; }

    /// <summary>
    /// The budget of start codes that took no record, per pair "tenant of the event + channel type"; null — none.
    /// </summary>
    internal UnmatchedStartCodeBudget? UnmatchedStartCodeBudget { get; }

    /// <summary>
    /// Opens the action of a channel on the current flow (<see cref="ChannelAdmissionScope"/>), or joins the
    /// one already open: within it the admission of a sender to a transaction is decided once, and the gate
    /// of the channel and the write of the engine share that verdict. Opened by every boundary of an action
    /// — this processor, and the routes of a channel that gate a letter before they hand it to the engine.
    /// </summary>
    /// <returns>Scope that closes the action on Dispose; called from an <c>async</c> method.</returns>
    internal static IDisposable OpenAction() => ChannelAdmissionScope.Open();

    /// <summary>
    /// Acts on the result of an inbound event.
    /// </summary>
    /// <remarks>
    /// The tenant of the event (<see cref="EventTenantOf(string)"/>) is applied on every path (SPEC-042
    /// R60): with one stated, an event is acted on only for a transaction of that tenant. The tenant of the
    /// identity snapshot is set by the core, not taken from the adapter (SPEC-003 CA-164): the tenant of
    /// the event, otherwise the tenant of the transaction the event addresses, otherwise none. An answer takes
    /// only a prompt reference its own channel stored (SPEC-003 CA-244). The processing of one
    /// event is one action: the admission of its sender to the transaction is decided once for the whole
    /// of it, the gate of the channel and the write of the engine alike.
    /// </remarks>
    /// <param name="inboundResult">Result of processing the inbound event, typed by intent.</param>
    /// <param name="adapter">Adapter of the channel the event arrived through.</param>
    /// <param name="replyTexts">
    /// Texts the channel states for itself: the error, and the replies it declares for a sign-in link
    /// that cannot be served and for a message that names no transaction.
    /// </param>
    /// <param name="asksInChannelPrompt">
    /// Whether the path the event arrived by puts the confirmation question into the chat — the webhook
    /// route and the polling loops do. False — it does not (the Email channel, the replay of the
    /// sandbox): the orchestrator of the question, the localizer of the chat texts and the store of the
    /// sent prompts take no part, the start of an authentication is confirmed without a question
    /// (SPEC-003 §4.8), the status texts stay in the base language, and an outcome notice carries no
    /// prompt reference.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed processing.</returns>
    public Task ProcessAsync(
        ChannelInboundResult inboundResult,
        IChannelAdapter adapter,
        ChannelReplyTexts replyTexts,
        bool asksInChannelPrompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inboundResult);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(replyTexts);

        return ProcessCoreAsync(inboundResult, adapter, replyTexts, asksInChannelPrompt, letterKey: null, cancellationToken);
    }

    /// <summary>
    /// Acts on the decision a letter of a channel that puts no question into the chat reports — the letter
    /// that completes a sign-in on the surface <c>None</c>: the processing of <see cref="ProcessAsync"/>, with
    /// the budget of the confirmation operation charged once per letter however many times it arrives.
    /// </summary>
    /// <param name="confirm">The decision the letter reports.</param>
    /// <param name="adapter">Adapter of the channel of the letter.</param>
    /// <param name="replyTexts">Texts the channel states for itself.</param>
    /// <param name="letterKey">What identifies the letter within its channel — the token of its link, the
    /// identifier of its mail; not empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed processing.</returns>
    internal Task ProcessLetterAsync(
        ChannelAuthConfirmResult confirm,
        IChannelAdapter adapter,
        ChannelReplyTexts replyTexts,
        string letterKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(replyTexts);
        ArgumentException.ThrowIfNullOrEmpty(letterKey);

        return ProcessCoreAsync(confirm, adapter, replyTexts, asksInChannelPrompt: false, letterKey, cancellationToken);
    }

    /// <summary>
    /// The one processing of an inbound event (<see cref="ProcessAsync"/>), run as one action of the channel:
    /// the admission of the sender is decided once for the whole of it.
    /// </summary>
    /// <remarks>
    /// An event that is no letter first passes the dropping of repeats: one whose key matches an event
    /// accepted within the window of the installation is not processed at all, so a platform delivering
    /// one event twice almost at once gets one processing on every path. A letter is never dropped here —
    /// its charge is counted once per letter by its own mark.
    /// </remarks>
    /// <param name="inboundResult">Result of processing the inbound event, typed by intent.</param>
    /// <param name="adapter">Adapter of the channel the event arrived through.</param>
    /// <param name="replyTexts">Texts the channel states for itself.</param>
    /// <param name="asksInChannelPrompt">Whether the path the event arrived by puts the confirmation question
    /// into the chat.</param>
    /// <param name="letterKey">What identifies the letter the event is; null — the event is no letter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed processing.</returns>
    private async Task ProcessCoreAsync(
        ChannelInboundResult inboundResult,
        IChannelAdapter adapter,
        ChannelReplyTexts replyTexts,
        bool asksInChannelPrompt,
        string? letterKey,
        CancellationToken cancellationToken)
    {
        // The tenant of the event is settled once, before anything else looks at the event, and every
        // path below compares the transaction the event names with it.
        var declaration = FindDeclaration(adapter.ChannelType);
        var eventTenantId = EventTenantOf(declaration);

        // The tenant of the identity snapshot is the core's, not the adapter's (SPEC-003 CA-164): the
        // tenant of the event, or none until a handler reads the transaction the event addresses. It is
        // set before the dropping of repeats, whose key carries it. The snapshot of a letter comes with the
        // tenant of its transaction, stated by the core of the Email channel, so it is not the adapter's.
        var snapshotTenant = new InboundSnapshotTenant(
            eventTenantId, adapter.ChannelType, Logger, adapterStatesTenant: letterKey is null);
        inboundResult = snapshotTenant.TakeOver(inboundResult);

        if (letterKey is null
            && InboundEventDeduplicator is { } deduplicator
            && !deduplicator.TryAccept(adapter.ChannelType, inboundResult))
        {
            return;
        }

        using var action = OpenAction();

        if (!asksInChannelPrompt)
        {
            await ChannelWebhookPipeline.RouteInboundResultAsync(
                this, inboundResult, adapter, replyTexts,
                confirmationPromptOrchestrator: null, promptLocalizer: null, promptMessageStore: null,
                eventTenantId, snapshotTenant, letterKey, cancellationToken);
        }
        else
        {
            await ChannelWebhookPipeline.RouteInboundResultAsync(
                this, inboundResult, adapter, replyTexts,
                ConfirmationPromptOrchestrator, PromptLocalizer, PromptMessageStoreOf(adapter),
                eventTenantId, snapshotTenant, letterKey, cancellationToken);
        }

        // A no-op once the tenant is chosen; otherwise no handler read a transaction for the event, and its
        // snapshot stays without a tenant.
        snapshotTenant.Complete();
    }

    /// <summary>
    /// The tenant of an event of a channel (SPEC-042 R60): the owner of the registration when it states
    /// one, otherwise the tenant of the scope the event is processed in — the tenant segment of the
    /// webhook route, the tenant a polling loop serves, the tenant a web path of the channel opened for
    /// the transaction it has loaded. An event with a tenant is acted on only for a transaction of that
    /// tenant.
    /// </summary>
    /// <param name="channelType">Channel type the event arrived through.</param>
    /// <returns>The tenant; null — the route without a tenant segment or the polling of the implicit
    /// tenant, whose events are acted on for a transaction of any tenant.</returns>
    internal string? EventTenantOf(string channelType) => EventTenantOf(FindDeclaration(channelType));

    /// <summary>
    /// The tenant of an event of a channel with the given core-level declaration
    /// (<see cref="EventTenantOf(string)"/>).
    /// </summary>
    /// <param name="declaration">Core-level declaration of the channel; null — none.</param>
    /// <returns>The tenant; null — none.</returns>
    private static string? EventTenantOf(CoreChannelDeclaration? declaration) =>
        declaration?.OwnerTenantId ?? ChannelTenantContext.CurrentTenantId;

    /// <summary>
    /// Skips the <c>Optional</c> phone number of a transaction whose window for an answer has run out
    /// (SPEC-041 C74, L73) — the very processing a press of "Skip" in the chat gets, answered in the
    /// chat of the channel snapshot the way that press is.
    /// </summary>
    /// <remarks>
    /// Nothing is skipped when the transaction has left the phone step meanwhile — the user answered,
    /// cancelled, or the deadline passed; the engine decides that on the transaction as stored. The
    /// caller opens the tenant scope of the channel snapshot, so the tenant of this event is that of the
    /// snapshot unless the registration states an owner.
    /// </remarks>
    /// <param name="transaction">Transaction whose phone number was requested.</param>
    /// <param name="adapter">Adapter of the channel of the transaction's channel snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The completed processing.</returns>
    internal Task SkipPhoneAfterWindowAsync(
        Transaction transaction,
        IChannelAdapter adapter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(adapter);

        var snapshot = transaction.ChannelIdentitySnapshot
            ?? throw new ArgumentException(
                "The transaction has no channel identity snapshot to answer in.",
                nameof(transaction));

        var declaration = FindDeclaration(adapter.ChannelType);

        // The sender is the snapshot of the transaction itself, whose tenant the core has already set.
        return ChannelWebhookPipeline.ApplyPhoneStepActionAsync(
            this, transaction.Id, snapshot, PhoneStepAction.Skip, adapter,
            ConfirmationPromptOrchestrator, PromptMessageStoreOf(adapter),
            EventTenantOf(declaration), snapshotTenant: null, cancellationToken);
    }

    /// <summary>
    /// The store of sent-prompt coordinates an answer on a path that asks in the chat takes its prompt
    /// reference from: every channel reads through a view narrowed to its own records (SPEC-003 CA-244),
    /// so a reference another channel stored for the transaction stays in the store.
    /// </summary>
    /// <param name="adapter">Adapter of the channel.</param>
    /// <returns>The store.</returns>
    private OwnChannelPromptMessageStore PromptMessageStoreOf(IChannelAdapter adapter) =>
        new(PromptMessageStore, adapter.ChannelType);

    /// <summary>
    /// Finds the core-level declaration of a channel type, compared ordinally.
    /// </summary>
    /// <param name="channelType">Channel type.</param>
    /// <returns>The declaration, or null when the channel declares none.</returns>
    private CoreChannelDeclaration? FindDeclaration(string channelType)
    {
        foreach (var declaration in _channelDeclarations)
        {
            if (string.Equals(declaration.ChannelType, channelType, StringComparison.Ordinal))
            {
                return declaration;
            }
        }

        return null;
    }
}
