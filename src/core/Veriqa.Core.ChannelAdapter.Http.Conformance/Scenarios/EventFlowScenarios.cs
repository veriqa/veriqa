// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Receiving;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Running;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Schema;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Scenarios;

/// <summary>
/// The scenarios of the receiver of events (SPEC-042 R23): a confirmation and a refusal played from the
/// deep link to the outcome, the start by a code of a registration that declares it, the press of an item
/// of the menu of a registration that declares buttons (in the interactive mode alone), the check of the
/// phone number a channel that obtains it automatically reports with the start of a flow, and the check
/// that every event that came was valid. In the interactive mode the author acts on the platform; in the
/// automatic mode the platform stub does.
/// </summary>
internal static partial class EventFlowScenarios
{
    /// <summary>
    /// For every tenant of the run: deep link, <c>auth_start</c>, the counter <c>send-prompt</c>,
    /// <c>auth_confirm</c>, <c>report-outcome</c>.
    /// </summary>
    public const string FlowConfirm = "flow.confirm";

    /// <summary>
    /// For the first tenant of the run: deep link, <c>auth_start</c>, the counter <c>send-prompt</c>,
    /// <c>auth_decline</c>, <c>report-outcome</c>.
    /// </summary>
    public const string FlowDecline = "flow.decline";

    /// <summary>
    /// Every <c>auth_start</c> of the flows carries the phone number of the user in E.164 — for a
    /// registration that declares the number obtained automatically.
    /// </summary>
    public const string IdentityPhoneAutomatic = "identity.phone-automatic";

    /// <summary>
    /// Every event that came passed the signature, the reading, the schema, the rule of the declared
    /// proof of a phone number and the rule of the route.
    /// </summary>
    public const string EventsAllValid = "events.all-valid";

    /// <summary>
    /// Modes that run the receiver.
    /// </summary>
    private const ConformanceModes ReceiverModes = ConformanceModes.Interactive | ConformanceModes.Automatic;

    /// <summary>
    /// Prefix of a line of the output that asks the author to act on the platform.
    /// </summary>
    private const string ActionLinePrefix = "ACTION";

    /// <summary>
    /// What the reason of a timeout adds to the name of the event of a transaction a step waits for.
    /// </summary>
    private const string OfTheTransaction = " of the transaction";

    /// <summary>
    /// The flows, in the order they run. The two that start by the deep link of a transaction are left
    /// out of the run of a registration whose link carries none; the start by a code joins only the run
    /// of a registration that declares it; the press of an item of the menu joins only the interactive
    /// run of a registration that declares buttons — the platform stub knows no press of a button.
    /// </summary>
    public static IReadOnlyList<ConformanceScenario> Flows { get; } =
    [
        new(FlowConfirm, ReceiverModes, ConfirmFlowAsync) { FollowsTransactionLink = true },
        new(FlowDecline, ReceiverModes, DeclineFlowAsync) { FollowsTransactionLink = true },
        new(FlowCodeStart, ReceiverModes, CodeStartFlowAsync) { RequiresCodeStart = true },
        new(FlowMenuItemPress, ConformanceModes.Interactive, MenuItemPressFlowAsync)
        {
            RequiredMessageKind = ChannelMessageKind.ActionButtons
        }
    ];

    /// <summary>
    /// The check of the phone number of the starts of the flows — after the flows, and only in a run of
    /// a registration that declares the number obtained automatically and whose flows follow the link of
    /// a transaction: the starts it checks are theirs.
    /// </summary>
    public static ConformanceScenario PhoneAutomatic { get; } =
        new(IdentityPhoneAutomatic, ReceiverModes, StartsCarryPhoneNumberAsync)
        {
            PhoneNumberAcquisition = PhoneNumberAcquisition.Automatic,
            FollowsTransactionLink = true
        };

    /// <summary>
    /// The check of all events — the last scenario of a run, when every event has come.
    /// </summary>
    public static ConformanceScenario AllValid { get; } = new(EventsAllValid, ReceiverModes, AllEventsAreValidAsync);

    /// <summary>
    /// <see cref="FlowConfirm"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> ConfirmFlowAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        foreach (var tenant in context.Options.Tenants)
        {
            if (await RunFlowAsync(context, FlowConfirm, tenant, FlowAnswer.Confirm, cancellationToken) is { } reason)
            {
                return ScenarioOutcome.Failed(TenantNames.Describe(tenant) + ": " + reason);
            }
        }

        return ScenarioOutcome.Passed;
    }

    /// <summary>
    /// <see cref="FlowDecline"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> DeclineFlowAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        var tenant = context.Options.PrimaryTenant;

        return await RunFlowAsync(context, FlowDecline, tenant, FlowAnswer.Decline, cancellationToken) is { } reason
            ? ScenarioOutcome.Failed(TenantNames.Describe(tenant) + ": " + reason)
            : ScenarioOutcome.Passed;
    }

    /// <summary>
    /// <see cref="IdentityPhoneAutomatic"/>: a channel that obtains the number automatically reports it
    /// in the identity of its events, and the start of a sign-in is the event the core takes it from.
    /// The number itself is never printed.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> StartsCarryPhoneNumberAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        var flows = context.Receiver.ObservedTransactions;
        var starts = context.Receiver.Events
            .Select(static received => (Event: received, Start: received.Result as ChannelAuthStartResult))
            .Where(pair => pair.Start is not null && flows.Contains(pair.Start.TransactionId))
            .ToList();

        if (starts.Count is 0)
        {
            return Task.FromResult(ScenarioOutcome.Failed("no auth_start of a transaction of the flows came to the receiver"));
        }

        foreach (var (received, start) in starts)
        {
            var phoneNumber = start!.Identity.PhoneNumber;

            if (phoneNumber is null)
            {
                return Task.FromResult(ScenarioOutcome.Failed(
                    received.Describe() + " carries no identity.phone_number, while the registration declares the number obtained automatically"));
            }

            if (!E164PhoneNumber.IsCanonical(phoneNumber))
            {
                return Task.FromResult(ScenarioOutcome.Failed(
                    received.Describe() + " carries an identity.phone_number that is not in the E.164 format with the leading plus"));
            }
        }

        return Task.FromResult(ScenarioOutcome.Passed);
    }

    /// <summary>
    /// <see cref="EventsAllValid"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> AllEventsAreValidAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        var events = context.Receiver.Events;
        if (events.Count is 0)
        {
            return Task.FromResult(ScenarioOutcome.NotApplicable("no event came to the receiver"));
        }

        var invalid = events.Where(static received => received.Violation is not null).ToList();
        if (invalid.Count is 0)
        {
            return Task.FromResult(ScenarioOutcome.Passed);
        }

        var first = invalid[0];
        return Task.FromResult(ScenarioOutcome.Failed(
            invalid.Count.ToString(CultureInfo.InvariantCulture) + " of " + events.Count.ToString(CultureInfo.InvariantCulture)
            + " events are not valid; the first, " + first.Describe() + ": " + first.Violation));
    }

    /// <summary>
    /// Plays one flow for one tenant: the deep link of a new transaction, the user following it, and the
    /// events the adapter posts, observed until the outcome is reported.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="scenarioId">The scenario, for the line that asks the author to act.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="answer">The answer of the user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>null</c> — the flow went as the wire requires; otherwise why not.</returns>
    private static async Task<string?> RunFlowAsync(
        ConformanceContext context,
        string scenarioId,
        string? tenant,
        FlowAnswer answer,
        CancellationToken cancellationToken)
    {
        var transactionId = TransactionId.NewId();
        var (url, refusal) = await DeepLinkAsync(context, transactionId, tenant, cancellationToken);

        if (url is null)
        {
            return refusal;
        }

        // What came to the receiver before the flow is not an event of the flow.
        var receivedBefore = context.Receiver.Events.Count;
        var flow = new ObservedFlow(context, transactionId, answer);
        using var observation = context.Receiver.Observe(transactionId, tenant, flow.OnEventAsync);

        if (context.Options.Mode is ConformanceModes.Automatic)
        {
            await context.RequiredPlatformStub.FollowLinkAsync(url, answer.StubAnswer, cancellationToken);
        }
        else
        {
            await Console.Out.WriteLineAsync(
                ActionLinePrefix + " " + scenarioId + ": as the user of " + TenantNames.Describe(tenant)
                + ", open the link and press " + answer.Button + ": " + url);
        }

        return await WaitAsync(context, flow.Started, HttpChannelWire.Events.AuthStart + OfTheTransaction, receivedBefore, cancellationToken)
            ?? await WaitAsync(context, flow.Answered, answer.EventName + OfTheTransaction, receivedBefore, cancellationToken);
    }

    /// <summary>
    /// The deep link of a new transaction, made in the scope of a tenant through the wrapper.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link; or, when the call did not succeed as the wire requires, <c>null</c> and why not.</returns>
    private static async Task<(string? Url, string? Refusal)> DeepLinkAsync(
        ConformanceContext context,
        TransactionId transactionId,
        string? tenant,
        CancellationToken cancellationToken)
    {
        using var tenantScope = ChannelTenantContext.BeginScope(tenant);

        var adapter = context.Compositions.Working.Adapter;
        var (link, capture) = await WrapperCallScenarios.CallAsync(context, () => adapter.GetDeepLinkAsync(new DeepLinkRequest { TransactionId = transactionId }, cancellationToken));

        if (WrapperCallVerdict.Succeeded(capture, WrapperCallScenarios.RefusalOf(link), WireDocument.DeepLinkResponse, context.Schema) is { } reason)
        {
            return (Url: null, Refusal: "deep-link: " + reason);
        }

        return (Url: link.Value, Refusal: null);
    }

    /// <summary>
    /// Waits for a step of a flow within the wait for an event.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="step">The step: <c>null</c> — done as the wire requires; otherwise why not.</param>
    /// <param name="awaited">What the step waits for, as the reason of a timeout names it.</param>
    /// <param name="receivedBefore">How many requests the receiver had got before the flow started.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome of the step.</returns>
    private static async Task<string?> WaitAsync(
        ConformanceContext context,
        Task<string?> step,
        string awaited,
        int receivedBefore,
        CancellationToken cancellationToken)
    {
        try
        {
            return await step.WaitAsync(context.Options.EventTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            var reason = "no " + awaited + " came within "
                + context.Options.EventTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s";

            return UntiedRequestSince(context, receivedBefore) is { } untied
                ? reason + "; " + untied
                : reason;
        }
    }

    /// <summary>
    /// The first request the receiver got during a flow and could not tie to any transaction — refused by
    /// the signature check or not read by the wrapper. The event of the flow may have been it, so a flow
    /// that waited in vain names it instead of reporting that nothing came.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="receivedBefore">How many requests the receiver had got before the flow started.</param>
    /// <returns>The request and why it was not taken; <c>null</c> — none came.</returns>
    private static string? UntiedRequestSince(ConformanceContext context, int receivedBefore)
    {
        var untied = context.Receiver.Events
            .Skip(receivedBefore)
            .FirstOrDefault(static received => received.TransactionId is null && received.Violation is not null);

        return untied is null
            ? null
            : "the receiver got a request it could not tie to a transaction, " + untied.Describe() + ": " + untied.Violation;
    }

    /// <summary>
    /// The answer of the user in a flow.
    /// </summary>
    /// <param name="StubAnswer">The answer of <c>follow-link</c>.</param>
    /// <param name="Button">The button in the line that asks the author to act.</param>
    /// <param name="EventName">The event the answer brings.</param>
    /// <param name="Outcome">The outcome reported on it.</param>
    /// <param name="StatusText">Text of the outcome.</param>
    private sealed record FlowAnswer(string StubAnswer, string Button, string EventName, TransactionOutcome Outcome, string StatusText)
    {
        /// <summary>
        /// The user confirms.
        /// </summary>
        public static FlowAnswer Confirm { get; } = new(
            "confirm",
            "Confirm",
            HttpChannelWire.Events.AuthConfirm,
            TransactionOutcome.Confirmed,
            "Veriqa conformance suite: confirmed.");

        /// <summary>
        /// The user declines.
        /// </summary>
        public static FlowAnswer Decline { get; } = new(
            "decline",
            "Decline",
            HttpChannelWire.Events.AuthDecline,
            TransactionOutcome.Declined,
            "Veriqa conformance suite: declined.");
    }

    /// <summary>
    /// The events of one transaction as the core would take them. On <c>auth_start</c> it puts the prompt
    /// to the user of the event while the POST of the adapter is still open (SPEC-042 E49); on the answer
    /// it reports the outcome with the prompt and the token of the event. Each call is made in the scope of
    /// the <c>webhook-id</c> of its event — the source of its key (SPEC-042 L43). The events are taken one
    /// at a time: an answer that comes while the prompt is on its way waits for it.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <param name="answer">The answer the flow expects.</param>
    private sealed class ObservedFlow(ConformanceContext context, TransactionId transactionId, FlowAnswer answer)
    {
        /// <summary>
        /// Opening of the reason of a counter call the adapter did not take.
        /// </summary>
        private const string CounterCallNotTakenReason =
            "the adapter did not take the counter send-prompt of the core while its auth_start was open: ";

        /// <summary>
        /// One event at a time.
        /// </summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>
        /// The step up to the prompt.
        /// </summary>
        private readonly TaskCompletionSource<string?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The step up to the outcome.
        /// </summary>
        private readonly TaskCompletionSource<string?> _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The prompt the counter call sent; <c>null</c> — none, or the adapter returned no reference.
        /// </summary>
        private ChannelMessageRef? _prompt;

        /// <summary>
        /// The step up to the prompt: <c>null</c> — done as the wire requires; otherwise why not.
        /// </summary>
        public Task<string?> Started => _started.Task;

        /// <summary>
        /// The step up to the outcome: <c>null</c> — done as the wire requires; otherwise why not.
        /// </summary>
        public Task<string?> Answered => _answered.Task;

        /// <summary>
        /// Takes an event of the transaction. An exception that prevents the run ends the step it came in.
        /// </summary>
        /// <param name="received">The event.</param>
        /// <param name="cancellationToken">Cancellation token of the request of the adapter.</param>
        /// <returns>A task that completes when the event is taken.</returns>
        public async Task OnEventAsync(ReceivedEvent received, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            var step = _started.Task.IsCompleted ? _answered : _started;

            try
            {
                if (!step.Task.IsCompleted && await TakeAsync(received, cancellationToken) is { } outcome)
                {
                    step.TrySetResult(outcome.Reason);
                }
            }
            catch (Exception exception)
            {
                step.TrySetException(exception);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Takes an event for the step it belongs to.
        /// </summary>
        /// <param name="received">The event.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The end of the current step; <c>null</c> — the event does not end it.</returns>
        private async Task<StepEnd?> TakeAsync(ReceivedEvent received, CancellationToken cancellationToken)
        {
            // The rule of the route (SPEC-042 R13) is part of the violation: the receiver checks it by the
            // tenant the transaction is observed with.
            if (received.Violation is { } violation)
            {
                return new StepEnd("the " + received.Name + " event is not valid: " + violation);
            }

            if (!_started.Task.IsCompleted)
            {
                return received.Result is ChannelAuthStartResult start
                    ? new StepEnd(await PromptAsync(start, cancellationToken))
                    : new StepEnd(received.Name + " came before auth_start");
            }

            // A repeated delivery of the start: the prompt is already out.
            if (received.Result is ChannelAuthStartResult)
            {
                return null;
            }

            if (!string.Equals(received.Name, answer.EventName, StringComparison.Ordinal))
            {
                return new StepEnd(received.Name + " came instead of " + answer.EventName);
            }

            return received.Result switch
            {
                ChannelAuthConfirmResult confirm => new StepEnd(await ReportAsync(confirm.Identity, confirm.InboundToken, cancellationToken)),
                ChannelAuthDeclineResult decline => new StepEnd(await ReportAsync(decline.Identity, decline.InboundToken, cancellationToken)),
                _ => new StepEnd(received.Name + " came instead of " + answer.EventName)
            };
        }

        /// <summary>
        /// The counter call: <c>send-prompt</c> to the user of the event.
        /// </summary>
        /// <param name="start">The event.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><c>null</c> — the prompt is out; otherwise why not.</returns>
        private async Task<string?> PromptAsync(ChannelAuthStartResult start, CancellationToken cancellationToken)
        {
            var adapter = context.Compositions.Working.Adapter;
            var (sent, capture) = await WrapperCallScenarios.CallAsync(
                context,
                () => WrapperCallScenarios.SendPromptAsync(adapter, transactionId, start.Identity, cancellationToken));

            capture.EnsureSeamPresent();
            if (capture.Last is { TransportFailure: { } failure })
            {
                return CounterCallNotTakenReason + failure;
            }

            if (WrapperCallVerdict.Succeeded(capture, WrapperCallScenarios.RefusalOf(sent), WireDocument.SendPromptResponse, context.Schema) is { } reason)
            {
                return "send-prompt: " + reason;
            }

            _prompt = sent.Value;
            return null;
        }

        /// <summary>
        /// The outcome on the answer: <c>report-outcome</c> with the prompt and the token of the event.
        /// </summary>
        /// <param name="identity">The user of the event.</param>
        /// <param name="inboundToken">The token of the event; <c>null</c> — none.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns><c>null</c> — the outcome is reported; otherwise why not.</returns>
        private async Task<string?> ReportAsync(ChannelIdentitySnapshot identity, ChannelInboundToken? inboundToken, CancellationToken cancellationToken)
        {
            var adapter = context.Compositions.Working.Adapter;
            var notice = new TransactionOutcomeNotice
            {
                TransactionId = transactionId,
                Outcome = answer.Outcome,
                ChannelUserId = identity.ChannelUserId,
                StatusText = answer.StatusText,
                InboundToken = inboundToken,
                PromptMessage = _prompt,
                DisplayIntent = _prompt is null
                    ? OutcomeNoticeDisplayIntent.NewMessage
                    : OutcomeNoticeDisplayIntent.ReplacePrompt
            };

            var (reported, capture) = await WrapperCallScenarios.CallAsync(context, () => adapter.ReportOutcomeAsync(notice, cancellationToken));

            return WrapperCallVerdict.Succeeded(capture, WrapperCallScenarios.RefusalOf(reported), WireDocument.ReportOutcomeResponse, context.Schema) is { } reason
                ? "report-outcome: " + reason
                : null;
        }

        /// <summary>
        /// The end of a step.
        /// </summary>
        /// <param name="Reason"><c>null</c> — done as the wire requires; otherwise why not.</param>
        private sealed record StepEnd(string? Reason);
    }
}
