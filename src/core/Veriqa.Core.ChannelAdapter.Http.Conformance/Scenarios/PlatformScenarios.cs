// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Platform;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Running;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Schema;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Scenarios;

/// <summary>
/// The scenarios of the reference adapters (SPEC-042 R25), in the automatic mode alone: what the adapter
/// did on the platform, read from the platform log of the stub. Each starts with a reset of the stub. A
/// platform action is tied to a call by the coordinates of the message the call returned, so an adapter
/// that returns none cannot pass them.
/// </summary>
internal static class PlatformScenarios
{
    /// <summary>
    /// Two <c>send-prompt</c> under one key send one message on the platform (SPEC-042 R15).
    /// </summary>
    public const string SingleMessageOnRepeat = "platform.single-message-on-repeat";

    /// <summary>
    /// A platform that cannot carry the confirmation out makes the adapter refuse the prompt with
    /// <c>channel_cannot_continue</c> and send nothing (SPEC-042 R16).
    /// </summary>
    public const string CannotContinue = "platform.cannot-continue";

    /// <summary>
    /// The prompt of each tenant goes out with the credential of that tenant (SPEC-042 R13).
    /// </summary>
    public const string CredentialsByTenant = "platform.credentials-by-tenant";

    /// <summary>
    /// Fewest tenants with a credential <see cref="CredentialsByTenant"/> can tell apart.
    /// </summary>
    private const int MinTenantsWithCredential = 2;

    /// <summary>
    /// Reason of a call that returned no coordinates of its message.
    /// </summary>
    private const string NoMessageReason =
        "the adapter returned no message reference, so no action of the platform log can be tied to the call";

    /// <summary>
    /// The scenarios, in the order they run.
    /// </summary>
    public static IReadOnlyList<ConformanceScenario> All { get; } =
    [
        new(SingleMessageOnRepeat, ConformanceModes.Automatic, SingleMessageOnRepeatAsync),
        new(CannotContinue, ConformanceModes.Automatic, CannotContinueAsync),
        new(CredentialsByTenant, ConformanceModes.Automatic, CredentialsByTenantAsync)
    ];

    /// <summary>
    /// <see cref="SingleMessageOnRepeat"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> SingleMessageOnRepeatAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        var stub = context.RequiredPlatformStub;
        await stub.ResetAsync(cancellationToken);

        using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);
        using var inboundEvent = context.EventIds.BeginNewEvent();

        var adapter = context.Compositions.Working.Adapter;
        var transactionId = TransactionId.NewId();
        var (first, firstCapture) = await WrapperCallScenarios.CallAsync(context, () => WrapperCallScenarios.SendPromptAsync(context, adapter, transactionId, cancellationToken));
        var (second, secondCapture) = await WrapperCallScenarios.CallAsync(context, () => WrapperCallScenarios.SendPromptAsync(context, adapter, transactionId, cancellationToken));

        if (WrapperCallScenarios.BothSucceeded(
                context,
                WireDocument.SendPromptResponse,
                (firstCapture, WrapperCallScenarios.RefusalOf(first)),
                (secondCapture, WrapperCallScenarios.RefusalOf(second))) is { } failed)
        {
            return failed;
        }

        if (first.Value is not { } message)
        {
            return ScenarioOutcome.Failed(NoMessageReason);
        }

        // The log was reset before the calls: every sent action in it came from them, whatever message
        // it carries — a second message of the repeat, or one of the first call under other coordinates.
        var sent = (await stub.GetPlatformLogAsync(cancellationToken))
            .Where(static action => string.Equals(action.Kind, PlatformAction.SentKind, StringComparison.Ordinal))
            .ToList();

        if (sent.Count is not 1)
        {
            return ScenarioOutcome.Failed(
                "the platform log holds " + sent.Count.ToString(CultureInfo.InvariantCulture)
                + " sent action(s) after the two calls instead of one");
        }

        return sent[0].Sent(message.ChatId, message.MessageId)
            ? ScenarioOutcome.Passed
            : ScenarioOutcome.Failed("the one sent action of the platform log does not carry the message the first call returned");
    }

    /// <summary>
    /// <see cref="CannotContinue"/>; applies when the stand declares such a case of its platform.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> CannotContinueAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        if (!context.Options.PlatformCannotConfirm)
        {
            return ScenarioOutcome.NotApplicable(
                "the stand declares no case in which its platform cannot carry a confirmation out ("
                + ConformanceConfigKeys.PathOf(ConformanceConfigKeys.PlatformCannotConfirm) + " is not true)");
        }

        var stub = context.RequiredPlatformStub;
        await stub.ResetAsync(cancellationToken);
        await stub.SetScenarioAsync(platformCannotConfirm: true, cancellationToken);

        // The switch lasts until a reset: the platform is given back as it was for whatever runs next. A
        // failed reset never hides what the scenario came to — an exception of the scenario, or a failure
        // it found — and fails the scenario only when it passed. The reset runs under the token of the
        // run: an interrupted run does not wait for a stub that does not answer, and the stub is reset
        // anyway by the next scenario that reads the platform log.
        ScenarioOutcome outcome;
        try
        {
            outcome = await PromptIsRefusedWithoutEffectAsync(context, cancellationToken);
        }
        catch
        {
            await TryResetAsync(stub, cancellationToken);
            throw;
        }

        if (outcome.Status is ScenarioStatus.Passed)
        {
            await stub.ResetAsync(cancellationToken);
        }
        else
        {
            await TryResetAsync(stub, cancellationToken);
        }

        return outcome;
    }

    /// <summary>
    /// Resets the stub after a scenario whose own result is what the run reports: a failure of the reset
    /// is left out.
    /// </summary>
    /// <param name="stub">The stub.</param>
    /// <param name="cancellationToken">Cancellation token of the run.</param>
    /// <returns>A task that completes when the reset is done, has failed or was cancelled with the run.</returns>
    private static async Task TryResetAsync(PlatformStubClient stub, CancellationToken cancellationToken)
    {
        try
        {
            await stub.ResetAsync(cancellationToken);
        }
        catch (PlatformStubException)
        {
            // The result of the scenario is reported instead; a scenario that reads the platform log
            // resets the stub before it.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run is interrupted: what the scenario came to is rethrown or reported by the caller.
        }
    }

    /// <summary>
    /// The prompt of <see cref="CannotContinue"/>: refused with <c>channel_cannot_continue</c> by the
    /// adapter and by the wrapper, with no message sent.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> PromptIsRefusedWithoutEffectAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        using var tenant = ChannelTenantContext.BeginScope(context.Options.PrimaryTenant);

        var adapter = context.Compositions.Working.Adapter;
        var transactionId = TransactionId.NewId();
        var (sent, capture) = await WrapperCallScenarios.CallAsync(context, () => WrapperCallScenarios.SendPromptAsync(context, adapter, transactionId, cancellationToken));

        if (WrapperCallVerdict.AnsweredWithError(
                capture,
                HttpChannelWire.ErrorCodes.ChannelCannotContinue,
                WireDocument.SendPromptResponse,
                context.Schema) is { } reason)
        {
            return ScenarioOutcome.Failed(reason);
        }

        if (WrapperCallScenarios.RefusalOf(sent) is not { } refusal
            || !string.Equals(refusal.Code, ChannelAdapterErrorCodes.ChannelCannotContinue, StringComparison.Ordinal))
        {
            return ScenarioOutcome.Failed("the wrapper did not refuse the prompt with " + ChannelAdapterErrorCodes.ChannelCannotContinue);
        }

        var sentActions = (await context.RequiredPlatformStub.GetPlatformLogAsync(cancellationToken))
            .Count(static action => string.Equals(action.Kind, PlatformAction.SentKind, StringComparison.Ordinal));

        return sentActions is 0
            ? ScenarioOutcome.Passed
            : ScenarioOutcome.Failed(
                "the platform log holds " + sentActions.ToString(CultureInfo.InvariantCulture)
                + " sent action(s) after the refusal");
    }

    /// <summary>
    /// <see cref="CredentialsByTenant"/>; applies when at least two tenants of the run have a credential
    /// at the stand.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> CredentialsByTenantAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        var credentials = context.Options.TenantCredentials;
        var tenants = context.Options.Tenants
            .Where(tenant => credentials.ContainsKey(TenantNames.KeyOf(tenant)))
            .ToList();

        if (tenants.Count < MinTenantsWithCredential)
        {
            return ScenarioOutcome.NotApplicable(
                "fewer than " + MinTenantsWithCredential.ToString(CultureInfo.InvariantCulture) + " tenants of the run have a credential in "
                + ConformanceConfigKeys.PathOf(ConformanceConfigKeys.TenantCredentials));
        }

        var stub = context.RequiredPlatformStub;
        await stub.ResetAsync(cancellationToken);

        var adapter = context.Compositions.Working.Adapter;
        var messages = new List<(string? Tenant, ChannelMessageRef Message)>();

        foreach (var tenant in tenants)
        {
            using var scope = ChannelTenantContext.BeginScope(tenant);

            var transactionId = TransactionId.NewId();
            var (sent, capture) = await WrapperCallScenarios.CallAsync(context, () => WrapperCallScenarios.SendPromptAsync(context, adapter, transactionId, cancellationToken));

            if (WrapperCallVerdict.Succeeded(capture, WrapperCallScenarios.RefusalOf(sent), WireDocument.SendPromptResponse, context.Schema) is { } reason)
            {
                return ScenarioOutcome.Failed(TenantNames.Describe(tenant) + ": " + reason);
            }

            if (sent.Value is not { } message)
            {
                return ScenarioOutcome.Failed(TenantNames.Describe(tenant) + ": " + NoMessageReason);
            }

            messages.Add((tenant, message));
        }

        var actions = await stub.GetPlatformLogAsync(cancellationToken);

        foreach (var (tenant, message) in messages)
        {
            var expected = credentials[TenantNames.KeyOf(tenant)];
            var action = actions.FirstOrDefault(candidate => candidate.Sent(message.ChatId, message.MessageId));

            if (action is null)
            {
                return ScenarioOutcome.Failed(TenantNames.Describe(tenant) + ": no sent action of the platform log carries the message of the call");
            }

            if (!string.Equals(action.Credential, expected, StringComparison.Ordinal))
            {
                return ScenarioOutcome.Failed(
                    TenantNames.Describe(tenant) + ": the message went out with the credential '" + action.Credential
                    + "' instead of '" + expected + "'");
            }
        }

        return ScenarioOutcome.Passed;
    }
}
