// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Recording;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Running;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Schema;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Sending;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Scenarios;

/// <summary>
/// The scenarios of the calls the wrapper makes itself (SPEC-042 R23): the success path of every
/// operation, the repetition under one key, and the refusals the configuration of the wrapper produces
/// — a foreign secret and a clock outside the signature window. Every call goes through a member of
/// <see cref="IChannelAdapter"/> of a composition of the wrapper; the suite builds no envelope here.
/// Every scenario runs in every mode, in the scope of the first tenant of the run; a scenario of a kind
/// of message with content of its own runs only for a registration that declares the kind. In the
/// automatic mode a refusal must also leave the platform log empty. The bodies of
/// <see cref="HealthSuccess"/>, <see cref="SendPromptSuccess"/> and <see cref="SendMessageSuccess"/> are
/// kept for the sender.
/// </summary>
internal static class WrapperCallScenarios
{
    /// <summary>
    /// <c>health</c> succeeds.
    /// </summary>
    public const string HealthSuccess = "health.success";

    /// <summary>
    /// <c>deep-link</c> succeeds.
    /// </summary>
    public const string DeepLinkSuccess = "deep-link.success";

    /// <summary>
    /// Two <c>deep-link</c> with the same request — one transaction, one purpose — return equal URLs
    /// (SPEC-042 C37).
    /// </summary>
    public const string DeepLinkStable = "deep-link.stable";

    /// <summary>
    /// <c>send-prompt</c> succeeds.
    /// </summary>
    public const string SendPromptSuccess = "send-prompt.success";

    /// <summary>
    /// <c>send-message</c> succeeds.
    /// </summary>
    public const string SendMessageSuccess = "send-message.success";

    /// <summary>
    /// <c>send-message</c> of the request for the phone number succeeds — for a registration that
    /// declares the number obtained on request.
    /// </summary>
    public const string SendMessagePhoneNumberRequest = "send-message.phone-number-request";

    /// <summary>
    /// <c>send-message</c> of a link to the claim form succeeds — for a registration that declares the
    /// kind <c>claim_form_link</c>.
    /// </summary>
    public const string SendMessageClaimFormLink = "send-message.claim-form-link";

    /// <summary>
    /// <c>send-message</c> with a link button and a button of an item of the menu succeeds — for a
    /// registration that declares the kind <c>action_buttons</c>.
    /// </summary>
    public const string SendMessageActionButtons = "send-message.action-buttons";

    /// <summary>
    /// Caption of the button of an item of the menu in the message with buttons of the suite.
    /// </summary>
    internal const string MenuItemButtonLabel = "Press the menu item";

    /// <summary>
    /// Text of the message with buttons of the suite.
    /// </summary>
    internal const string ActionButtonsText = "Veriqa conformance suite: a test message with buttons.";

    /// <summary>
    /// <c>report-outcome</c> succeeds.
    /// </summary>
    public const string ReportOutcomeSuccess = "report-outcome.success";

    /// <summary>
    /// Two <c>send-prompt</c> under the key of one event return equal results (SPEC-042 R15).
    /// </summary>
    public const string SendPromptRepeatSameKey = "send-prompt.repeat-same-key";

    /// <summary>
    /// Two <c>report-outcome</c> under the key of one event return equal results (SPEC-042 R15).
    /// </summary>
    public const string ReportOutcomeRepeatSameKey = "report-outcome.repeat-same-key";

    /// <summary>
    /// A call signed with a foreign secret is refused with 403 and no envelope (SPEC-042 R14).
    /// </summary>
    public const string AuthWrongSecret = "auth.wrong-secret";

    /// <summary>
    /// A call signed ten minutes in the past is refused with 403 and no envelope (SPEC-042 R14, C31).
    /// </summary>
    public const string AuthTimestampPast = "auth.timestamp-past";

    /// <summary>
    /// A call signed ten minutes in the future is refused with 403 and no envelope (SPEC-042 R14, C31).
    /// </summary>
    public const string AuthTimestampFuture = "auth.timestamp-future";

    /// <summary>
    /// Name of the client application in the prompts of the suite.
    /// </summary>
    private const string ClientApplicationName = "Veriqa conformance suite";

    /// <summary>
    /// Display name of the account a prompt of the suite is put to when no event named it.
    /// </summary>
    private const string PromptAccountDisplayName = "Veriqa conformance user";

    /// <summary>
    /// Name of the chat of the channel in the prompts of the suite — synthetic, like the name of the
    /// client application: the core states the name its configuration or the adapter gives, and an HTTP
    /// registration without <c>Channels.BotName</c> gives none.
    /// </summary>
    private const string PromptBotName = "@veriqa_conformance_bot";

    /// <summary>
    /// Brand name the prompts of the suite state.
    /// </summary>
    private const string PromptBrandName = "Veriqa conformance brand";

    /// <summary>
    /// Text of the message of <c>send-message</c>.
    /// </summary>
    private const string MessageText = "Veriqa conformance suite: a test message.";

    /// <summary>
    /// Text of the request for the phone number.
    /// </summary>
    private const string PhoneNumberRequestText = "Veriqa conformance suite: a test request for the phone number.";

    /// <summary>
    /// Caption of the button that shares the phone number in the request of the suite.
    /// </summary>
    private const string ShareButtonLabel = "Share phone number";

    /// <summary>
    /// Caption of the button of the secondary action in the request of the suite.
    /// </summary>
    private const string SkipButtonLabel = "Skip";

    /// <summary>
    /// Text of the message with the link to the claim form.
    /// </summary>
    private const string ClaimFormLinkText = "Veriqa conformance suite: a test link to the form of the missing claims.";

    /// <summary>
    /// Caption of the button that opens the claim form.
    /// </summary>
    private const string ClaimFormButtonLabel = "Fill in the form";

    /// <summary>
    /// Address of the claim form in the message of the suite — synthetic, on a domain reserved for
    /// examples (RFC 2606): the suite has no form to open.
    /// </summary>
    private const string ClaimFormUrl = "https://example.com/veriqa-conformance/claim-form";

    /// <summary>
    /// Caption of the link button in the message with buttons of the suite.
    /// </summary>
    private const string LinkButtonLabel = "Open the link";

    /// <summary>
    /// Address of the link button in the message with buttons of the suite — synthetic, on a domain
    /// reserved for examples (RFC 2606).
    /// </summary>
    private const string LinkButtonUrl = "https://example.com/veriqa-conformance/link";

    /// <summary>
    /// Length of the random value of an item of the menu, in hexadecimal digits.
    /// </summary>
    private const int MenuItemValueLength = 32;

    /// <summary>
    /// Outcome text of <c>report-outcome</c>.
    /// </summary>
    private const string OutcomeStatusText = "Veriqa conformance suite: confirmed.";

    /// <summary>
    /// How far the clock is moved out of the signature window: beyond its five minutes with a margin, so
    /// the boundary is never raced (SPEC-042 C31).
    /// </summary>
    internal static readonly TimeSpan SignatureWindowOverrun = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The kinds of message that travel with content of their own and have scenarios of their own.
    /// </summary>
    private static readonly ChannelMessageKind[] KindsWithContent =
    [
        ChannelMessageKind.PhoneNumberRequest,
        ChannelMessageKind.ClaimFormLink,
        ChannelMessageKind.ActionButtons
    ];

    /// <summary>
    /// The scenarios, in the order they run: <c>report-outcome.success</c> follows
    /// <c>send-prompt.success</c>, whose prompt it reports on.
    /// </summary>
    public static IReadOnlyList<ConformanceScenario> All { get; } =
    [
        new(HealthSuccess, ConformanceModes.Any, HealthSucceedsAsync),
        new(DeepLinkSuccess, ConformanceModes.Any, DeepLinkSucceedsAsync),
        new(DeepLinkStable, ConformanceModes.Any, DeepLinkIsStableAsync),
        new(SendPromptSuccess, ConformanceModes.Any, SendPromptSucceedsAsync),
        new(SendMessageSuccess, ConformanceModes.Any, SendMessageSucceedsAsync),
        new(SendMessagePhoneNumberRequest, ConformanceModes.Any, PhoneNumberRequestSucceedsAsync)
        {
            PhoneNumberAcquisition = PhoneNumberAcquisition.OnRequest
        },
        new(SendMessageClaimFormLink, ConformanceModes.Any, ClaimFormLinkSucceedsAsync)
        {
            RequiredMessageKind = ChannelMessageKind.ClaimFormLink
        },
        new(SendMessageActionButtons, ConformanceModes.Any, ActionButtonsSucceedAsync)
        {
            RequiredMessageKind = ChannelMessageKind.ActionButtons
        },
        new(ReportOutcomeSuccess, ConformanceModes.Any, ReportOutcomeSucceedsAsync),
        new(SendPromptRepeatSameKey, ConformanceModes.Any, SendPromptRepeatsUnderOneKeyAsync),
        new(ReportOutcomeRepeatSameKey, ConformanceModes.Any, ReportOutcomeRepeatsUnderOneKeyAsync),
        new(AuthWrongSecret, ConformanceModes.Any, WrongSecretIsRefusedAsync),
        new(AuthTimestampPast, ConformanceModes.Any, PastTimestampIsRefusedAsync),
        new(AuthTimestampFuture, ConformanceModes.Any, FutureTimestampIsRefusedAsync)
    ];

    /// <summary>
    /// The confirmation context of a <c>send-prompt</c> of the suite: a detailed prompt owned by the
    /// tenant of the current scope, with every member the core states filled, so the adapter meets the
    /// members it meets in production. The account and the name of the channel are the ones the core
    /// would state; the application, the chat and the brand are named synthetically.
    /// </summary>
    /// <param name="adapter">The wrapper; its declared display name names the channel, as in the core.</param>
    /// <param name="identity">Channel snapshot of the account the prompt is put to.</param>
    /// <returns>The context.</returns>
    internal static DetailedConfirmationPromptContext PromptContext(IChannelAdapter adapter, ChannelIdentitySnapshot identity)
    {
        var now = TimeProvider.System.GetUtcNow();

        return new DetailedConfirmationPromptContext
        {
            Ownership = ResolutionContext.Of(ChannelTenantContext.CurrentTenantId, applicationId: null, uiConfigSelector: null),
            ClientApplicationName = ClientApplicationName,
            InitiatedAt = now,
            RequestedAt = now,
            ChannelIdentity = identity,
            ChannelName = (adapter as IChannelDisplayMetadata)?.DisplayName ?? adapter.ChannelType,
            BotName = PromptBotName,
            BrandName = PromptBrandName
        };
    }

    /// <summary>
    /// The account of the user of the run, for a prompt no event named the account of.
    /// </summary>
    /// <param name="channelType">Channel type of the registration.</param>
    /// <param name="channelUserId">The user the prompt is put to.</param>
    /// <returns>The channel snapshot.</returns>
    private static ChannelIdentitySnapshot PromptAccount(string channelType, string channelUserId) => new()
    {
        TenantId = ChannelTenantContext.CurrentTenantId,
        ChannelType = channelType,
        ChannelUserId = channelUserId,
        IsBot = false,
        DisplayName = PromptAccountDisplayName,
        CapturedAt = TimeProvider.System.GetUtcNow(),
        AdapterVersion = WireBodies.EventAdapterVersion
    };

    /// <summary>
    /// <see cref="HealthSuccess"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> HealthSucceedsAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);

        var adapter = context.Compositions.Working.Adapter;
        var (health, capture) = await CallAsync(context, () => adapter.GetHealthAsync(cancellationToken));
        context.KeepSourceBody(HealthSuccess, capture);

        if (WrapperCallVerdict.Succeeded(capture, RefusalOf(health), WireDocument.HealthResponse, context.Schema) is { } reason)
        {
            return ScenarioOutcome.Failed(reason);
        }

        return health.Value.IsHealthy
            ? ScenarioOutcome.Passed
            : ScenarioOutcome.Failed("the adapter reports itself unhealthy");
    }

    /// <summary>
    /// <see cref="DeepLinkSuccess"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> DeepLinkSucceedsAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);

        var adapter = context.Compositions.Working.Adapter;
        var transactionId = TransactionId.NewId();
        var (link, capture) = await CallAsync(context, () => adapter.GetDeepLinkAsync(new DeepLinkRequest { TransactionId = transactionId }, cancellationToken));
        context.KeepSourceBody(DeepLinkSuccess, capture);

        return Judge(WrapperCallVerdict.Succeeded(capture, RefusalOf(link), WireDocument.DeepLinkResponse, context.Schema));
    }

    /// <summary>
    /// <see cref="DeepLinkStable"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> DeepLinkIsStableAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);

        var adapter = context.Compositions.Working.Adapter;
        var request = new DeepLinkRequest { TransactionId = TransactionId.NewId() };
        var (first, firstCapture) = await CallAsync(context, () => adapter.GetDeepLinkAsync(request, cancellationToken));
        var (second, secondCapture) = await CallAsync(context, () => adapter.GetDeepLinkAsync(request, cancellationToken));

        if (BothSucceeded(context, WireDocument.DeepLinkResponse, (firstCapture, RefusalOf(first)), (secondCapture, RefusalOf(second))) is { } failed)
        {
            return failed;
        }

        // The links themselves are not printed: they are the adapter's, of any content.
        return string.Equals(first.Value, second.Value, StringComparison.Ordinal)
            ? ScenarioOutcome.Passed
            : ScenarioOutcome.Failed("two deep-link calls with the same request returned different URLs");
    }

    /// <summary>
    /// <see cref="SendPromptSuccess"/>. The prompt is left for <see cref="ReportOutcomeSuccess"/>, with
    /// the coordinates the adapter returned, if any.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> SendPromptSucceedsAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);

        var adapter = context.Compositions.Working.Adapter;
        var transactionId = TransactionId.NewId();
        var (sent, capture) = await CallAsync(context, () => SendPromptAsync(context, adapter, transactionId, cancellationToken));

        context.SentPrompt = new SentPrompt(transactionId, sent.IsSuccess ? sent.Value : null);
        context.KeepSourceBody(SendPromptSuccess, capture);

        // A prompt without coordinates is a success too (CA-182): the channel has no addressable reference.
        return Judge(WrapperCallVerdict.Succeeded(capture, RefusalOf(sent), WireDocument.SendPromptResponse, context.Schema));
    }

    /// <summary>
    /// <see cref="SendMessageSuccess"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> SendMessageSucceedsAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);

        var adapter = context.Compositions.Working.Adapter;
        var message = new ChannelMessage
        {
            ChannelUserId = context.Options.ChannelUserId,
            Text = MessageText,
            Kind = MessageKindOf(adapter.Capabilities)
        };

        var (sent, capture) = await CallAsync(context, () => adapter.SendMessageAsync(message, cancellationToken));
        context.KeepSourceBody(SendMessageSuccess, capture);

        return Judge(WrapperCallVerdict.Succeeded(capture, RefusalOf(sent), WireDocument.SendMessageResponse, context.Schema));
    }

    /// <summary>
    /// <see cref="SendMessagePhoneNumberRequest"/>: the request with all of its content, the secondary
    /// action being the one that continues without the number. The user of the suite is asked and
    /// nothing is waited for: the answer of the user is not part of the scenario.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> PhoneNumberRequestSucceedsAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);

        var adapter = context.Compositions.Working.Adapter;
        var message = new ChannelMessage
        {
            ChannelUserId = context.Options.ChannelUserId,
            Text = PhoneNumberRequestText,
            Kind = ChannelMessageKind.PhoneNumberRequest,
            PhoneNumberRequest = new PhoneNumberRequestContent
            {
                TransactionId = TransactionId.NewId(),
                ShareButtonLabel = ShareButtonLabel,
                SecondaryAction = PhoneStepAction.Skip,
                SecondaryButtonLabel = SkipButtonLabel
            }
        };

        return await JudgeSendMessageAsync(context, adapter, message, cancellationToken);
    }

    /// <summary>
    /// <see cref="SendMessageClaimFormLink"/>: the link with its caption. The user of the suite is sent
    /// the message and nothing is waited for: the form is synthetic.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> ClaimFormLinkSucceedsAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);

        var adapter = context.Compositions.Working.Adapter;
        var message = new ChannelMessage
        {
            ChannelUserId = context.Options.ChannelUserId,
            Text = ClaimFormLinkText,
            Kind = ChannelMessageKind.ClaimFormLink,
            ClaimFormLink = new ClaimFormLinkContent
            {
                Url = ClaimFormUrl,
                ButtonLabel = ClaimFormButtonLabel
            }
        };

        return await JudgeSendMessageAsync(context, adapter, message, cancellationToken);
    }

    /// <summary>
    /// <see cref="SendMessageActionButtons"/>: the message with buttons of the suite, the value of its
    /// item of the menu a new one. Nothing is waited for: the press is the scenario of a flow.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> ActionButtonsSucceedAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);

        var adapter = context.Compositions.Working.Adapter;
        var message = ActionButtonsMessage(context, NewMenuItemValue());

        return await JudgeSendMessageAsync(context, adapter, message, cancellationToken);
    }

    /// <summary>
    /// A <c>send-message</c> that must succeed, its answer by the schema.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="adapter">The wrapper.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> JudgeSendMessageAsync(
        ConformanceContext context,
        IChannelAdapter adapter,
        ChannelMessage message,
        CancellationToken cancellationToken)
    {
        var (sent, capture) = await CallAsync(context, () => adapter.SendMessageAsync(message, cancellationToken));

        return Judge(WrapperCallVerdict.Succeeded(capture, RefusalOf(sent), WireDocument.SendMessageResponse, context.Schema));
    }

    /// <summary>
    /// <see cref="ReportOutcomeSuccess"/>: the outcome of the transaction of
    /// <see cref="SendPromptSuccess"/>, with the coordinates of its prompt (<c>null</c> when the adapter
    /// returned none).
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> ReportOutcomeSucceedsAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);

        var adapter = context.Compositions.Working.Adapter;
        var prompt = context.SentPrompt ?? new SentPrompt(TransactionId.NewId(), Message: null);
        var notice = OutcomeNotice(context, prompt);

        var (reported, capture) = await CallAsync(context, () => adapter.ReportOutcomeAsync(notice, cancellationToken));

        return Judge(WrapperCallVerdict.Succeeded(capture, RefusalOf(reported), WireDocument.ReportOutcomeResponse, context.Schema));
    }

    /// <summary>
    /// <see cref="SendPromptRepeatSameKey"/>: both calls are caused by one played event, so the wrapper
    /// keys them alike (SPEC-042 L43).
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> SendPromptRepeatsUnderOneKeyAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);
        using var inboundEvent = context.EventIds.BeginNewEvent();

        var adapter = context.Compositions.Working.Adapter;
        var transactionId = TransactionId.NewId();
        var (first, firstCapture) = await CallAsync(context, () => SendPromptAsync(context, adapter, transactionId, cancellationToken));
        var (second, secondCapture) = await CallAsync(context, () => SendPromptAsync(context, adapter, transactionId, cancellationToken));

        if (BothSucceeded(context, WireDocument.SendPromptResponse, (firstCapture, RefusalOf(first)), (secondCapture, RefusalOf(second))) is { } failed)
        {
            return failed;
        }

        return Equals(first.Value, second.Value)
            ? ScenarioOutcome.Passed
            : ScenarioOutcome.Failed("the repeated call under the same key returned another message");
    }

    /// <summary>
    /// <see cref="ReportOutcomeRepeatSameKey"/>: both calls are caused by one played event, so the
    /// wrapper keys them alike (SPEC-042 L43). In the automatic mode the repeat must not show the
    /// outcome to the user again: the platform log is checked as well (SPEC-042 R15).
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> ReportOutcomeRepeatsUnderOneKeyAsync(ConformanceContext context, CancellationToken cancellationToken) =>
        PlatformEffects.WithOutcomeShownOnceAsync(
            context,
            async () =>
            {
                using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);
                using var inboundEvent = context.EventIds.BeginNewEvent();

                var adapter = context.Compositions.Working.Adapter;
                var notice = OutcomeNotice(context, new SentPrompt(TransactionId.NewId(), Message: null));
                var (first, firstCapture) = await CallAsync(context, () => adapter.ReportOutcomeAsync(notice, cancellationToken));
                var (second, secondCapture) = await CallAsync(context, () => adapter.ReportOutcomeAsync(notice, cancellationToken));

                if (BothSucceeded(context, WireDocument.ReportOutcomeResponse, (firstCapture, RefusalOf(first)), (secondCapture, RefusalOf(second))) is { } failed)
                {
                    return (failed, false);
                }

                return first.Value == second.Value
                    ? (ScenarioOutcome.Passed, first.Value)
                    : (ScenarioOutcome.Failed("the repeated call under the same key returned another result"), first.Value);
            },
            cancellationToken);

    /// <summary>
    /// <see cref="AuthWrongSecret"/>: <c>send-prompt</c> — an operation with an effect — through the
    /// composition that signs with a secret the adapter does not know.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> WrongSecretIsRefusedAsync(ConformanceContext context, CancellationToken cancellationToken) =>
        UnauthenticatedPromptIsRefusedAsync(context, context.Compositions.ForeignSecret.Adapter, cancellationToken);

    /// <summary>
    /// <see cref="AuthTimestampPast"/>: <c>send-prompt</c> through the composition whose clock is moved
    /// back beyond the signature window.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> PastTimestampIsRefusedAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var shift = context.Compositions.Clock.Shift(-SignatureWindowOverrun);
        return await UnauthenticatedPromptIsRefusedAsync(context, context.Compositions.ShiftedClock.Adapter, cancellationToken);
    }

    /// <summary>
    /// <see cref="AuthTimestampFuture"/>: <c>send-prompt</c> through the composition whose clock is
    /// moved forward beyond the signature window.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> FutureTimestampIsRefusedAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var shift = context.Compositions.Clock.Shift(SignatureWindowOverrun);
        return await UnauthenticatedPromptIsRefusedAsync(context, context.Compositions.ShiftedClock.Adapter, cancellationToken);
    }

    /// <summary>
    /// A <c>send-prompt</c> the adapter must refuse with 403, no envelope and no effect (SPEC-042 R14);
    /// the effect is looked for in the platform log in the automatic mode.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="adapter">The wrapper of a composition that signs wrongly.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> UnauthenticatedPromptIsRefusedAsync(
        ConformanceContext context,
        IChannelAdapter adapter,
        CancellationToken cancellationToken) =>
        PlatformEffects.WithoutEffectAsync(
            context,
            async () =>
            {
                using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);

                var transactionId = TransactionId.NewId();
                var (sent, capture) = await CallAsync(context, () => SendPromptAsync(context, adapter, transactionId, cancellationToken));

                return Judge(WrapperCallVerdict.RefusedUnauthenticated(capture, RefusalOf(sent)));
            },
            cancellationToken);

    /// <summary>
    /// The message with buttons of the suite to the user of the run: a link button, then a button of an
    /// item of the menu with the given value.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="menuItemValue">The value of the item, without the marker of an item of the menu.</param>
    /// <returns>The message.</returns>
    internal static ChannelMessage ActionButtonsMessage(ConformanceContext context, string menuItemValue) => new()
    {
        ChannelUserId = context.Options.ChannelUserId,
        Text = ActionButtonsText,
        Kind = ChannelMessageKind.ActionButtons,
        ActionButtons = new ActionButtonsContent
        {
            Buttons =
            [
                new ChannelActionButton { Label = LinkButtonLabel, Url = LinkButtonUrl },
                new ChannelActionButton { Label = MenuItemButtonLabel, CallbackData = CallbackDataPrefixes.MenuItem + menuItemValue }
            ]
        }
    };

    /// <summary>
    /// A new random value of an item of the menu: a press of a button of another run never carries it.
    /// </summary>
    /// <returns>The value.</returns>
    internal static string NewMenuItemValue() => RandomNumberGenerator.GetHexString(MenuItemValueLength, lowercase: true);

    /// <summary>
    /// A <c>send-prompt</c> of the suite to the user of the run.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="adapter">The wrapper.</param>
    /// <param name="transactionId">Transaction of the prompt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the wrapper.</returns>
    internal static Task<Result<ChannelMessageRef?>> SendPromptAsync(
        ConformanceContext context,
        IChannelAdapter adapter,
        TransactionId transactionId,
        CancellationToken cancellationToken) =>
        SendPromptAsync(
            adapter,
            transactionId,
            PromptAccount(adapter.ChannelType, context.Options.ChannelUserId),
            cancellationToken);

    /// <summary>
    /// A <c>send-prompt</c> of the suite: a detailed prompt owned by the tenant of the current scope,
    /// put to the account it names, as the core puts a prompt to the sender of an event.
    /// </summary>
    /// <param name="adapter">The wrapper.</param>
    /// <param name="transactionId">Transaction of the prompt.</param>
    /// <param name="identity">Channel snapshot of the account the prompt is put to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the wrapper.</returns>
    internal static Task<Result<ChannelMessageRef?>> SendPromptAsync(
        IChannelAdapter adapter,
        TransactionId transactionId,
        ChannelIdentitySnapshot identity,
        CancellationToken cancellationToken) =>
        adapter.SendConfirmationPromptAsync(
            transactionId,
            identity.ChannelUserId,
            PromptContext(adapter, identity),
            cancellationToken);

    /// <summary>
    /// The outcome notice of the suite: confirmed, replacing the prompt when its coordinates are known.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="prompt">The prompt the outcome is reported on.</param>
    /// <returns>The notice.</returns>
    private static TransactionOutcomeNotice OutcomeNotice(ConformanceContext context, SentPrompt prompt) => new()
    {
        TransactionId = prompt.TransactionId,
        Outcome = TransactionOutcome.Confirmed,
        ChannelUserId = context.Options.ChannelUserId,
        StatusText = OutcomeStatusText,
        PromptMessage = prompt.Message,
        DisplayIntent = prompt.Message is null
            ? OutcomeNoticeDisplayIntent.NewMessage
            : OutcomeNoticeDisplayIntent.ReplacePrompt
    };

    /// <summary>
    /// The kind of the message of <c>send-message</c> (SPEC-042 C35): plain text when the registration
    /// declares it, otherwise the first declared kind in ordinal order — the set of the registration
    /// keeps no order of declaration. A kind with content of its own — the request for the phone number,
    /// the link to the claim form, the buttons — is never the kind: it has a scenario of its own, so a
    /// registration that declares such kinds alone is sent plain text.
    /// </summary>
    /// <param name="capabilities">Facts of the registration.</param>
    /// <returns>The kind.</returns>
    private static ChannelMessageKind MessageKindOf(ChannelCapabilities capabilities)
    {
        if (capabilities.SupportedMessageKinds.Contains(ChannelMessageKind.PlainText))
        {
            return ChannelMessageKind.PlainText;
        }

        var others = capabilities.SupportedMessageKinds
            .Where(static kind => !KindsWithContent.Contains(kind))
            .OrderBy(static kind => kind.Value, StringComparer.Ordinal)
            .ToList();

        return others.Count is 0 ? ChannelMessageKind.PlainText : others[0];
    }

    /// <summary>
    /// Makes one call of the wrapper inside a capture of its own.
    /// </summary>
    /// <typeparam name="T">Result of the call.</typeparam>
    /// <param name="context">Context of the run.</param>
    /// <param name="call">The call.</param>
    /// <returns>The result of the wrapper and what the call did on the wire.</returns>
    internal static async Task<(Result<T> Result, WireCallCapture Capture)> CallAsync<T>(
        ConformanceContext context,
        Func<Task<Result<T>>> call)
    {
        using var capture = context.Recorder.Capture();
        var result = await call();
        return (result, capture);
    }

    /// <summary>
    /// Judges two calls that must both succeed.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="document">What the answers are, for the schema.</param>
    /// <param name="first">The first call.</param>
    /// <param name="second">The second call.</param>
    /// <returns><c>null</c> — both succeeded; otherwise the failure of the first that did not.</returns>
    internal static ScenarioOutcome? BothSucceeded(
        ConformanceContext context,
        WireDocument document,
        (WireCallCapture Capture, TransactionError? Refusal) first,
        (WireCallCapture Capture, TransactionError? Refusal) second)
    {
        if (WrapperCallVerdict.Succeeded(first.Capture, first.Refusal, document, context.Schema) is { } firstReason)
        {
            return ScenarioOutcome.Failed("the first call: " + firstReason);
        }

        return WrapperCallVerdict.Succeeded(second.Capture, second.Refusal, document, context.Schema) is { } secondReason
            ? ScenarioOutcome.Failed("the second call: " + secondReason)
            : null;
    }

    /// <summary>
    /// The outcome of a verdict.
    /// </summary>
    /// <param name="reason">Reason of failure; <c>null</c> — passed.</param>
    /// <returns>The outcome.</returns>
    internal static ScenarioOutcome Judge(string? reason) =>
        reason is null ? ScenarioOutcome.Passed : ScenarioOutcome.Failed(reason);

    /// <summary>
    /// The refusal of the wrapper.
    /// </summary>
    /// <typeparam name="T">Result of the call.</typeparam>
    /// <param name="result">The result.</param>
    /// <returns>The error; <c>null</c> — the call succeeded.</returns>
    internal static TransactionError? RefusalOf<T>(Result<T> result) => result.IsFailure ? result.Error : null;
}
