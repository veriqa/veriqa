// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

using Veriqa.Core.ChannelAdapter.Http.Conformance.Platform;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Running;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Scenarios;

/// <summary>
/// The effect of a scenario on the platform, read from the platform log of the stub in the automatic
/// mode: the log is reset before the scenario and read after it. Outside the automatic mode there is no
/// platform to look at, and the scenario is judged by the wire alone.
/// </summary>
internal static class PlatformEffects
{
    /// <summary>
    /// Runs a scenario that must have no effect on the platform: after it the platform log is empty.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="scenario">The scenario.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome of the scenario, or the effect it had.</returns>
    public static async Task<ScenarioOutcome> WithoutEffectAsync(
        ConformanceContext context,
        Func<Task<ScenarioOutcome>> scenario,
        CancellationToken cancellationToken)
    {
        if (context.PlatformStub is not { } stub)
        {
            return await scenario();
        }

        await stub.ResetAsync(cancellationToken);
        var outcome = await scenario();
        if (outcome.Status is not ScenarioStatus.Passed)
        {
            return outcome;
        }

        var actions = await stub.GetPlatformLogAsync(cancellationToken);
        return actions.Count is 0
            ? outcome
            : ScenarioOutcome.Failed(
                "the call had an effect on the platform: the platform log holds "
                + actions.Count.ToString(CultureInfo.InvariantCulture) + " action(s) after it");
    }

    /// <summary>
    /// Runs a scenario that must send exactly one message on the platform.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="scenario">The scenario.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome of the scenario, or how its effect differs.</returns>
    public static async Task<ScenarioOutcome> WithOneMessageSentAsync(
        ConformanceContext context,
        Func<Task<ScenarioOutcome>> scenario,
        CancellationToken cancellationToken)
    {
        if (context.PlatformStub is not { } stub)
        {
            return await scenario();
        }

        await stub.ResetAsync(cancellationToken);
        var outcome = await scenario();
        if (outcome.Status is not ScenarioStatus.Passed)
        {
            return outcome;
        }

        var sent = (await stub.GetPlatformLogAsync(cancellationToken))
            .Count(static action => string.Equals(action.Kind, PlatformAction.SentKind, StringComparison.Ordinal));

        return sent is 1
            ? outcome
            : ScenarioOutcome.Failed(
                "the platform log holds " + sent.ToString(CultureInfo.InvariantCulture)
                + " sent message(s) instead of one");
    }

    /// <summary>
    /// Runs a scenario that reports one outcome to the user, however many times it is called: the
    /// platform log holds one action — a sent message or an edited prompt — when the outcome is shown,
    /// and none when it is not, since <c>shown = false</c> means the channel showed nothing
    /// (SPEC-042 C36, R15).
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="scenario">
    /// The scenario: its outcome and whether the adapter reports the outcome shown. <c>Shown</c> is read
    /// only when the outcome is <see cref="ScenarioStatus.Passed"/>; otherwise any value may be returned.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome of the scenario, or how its effect differs.</returns>
    public static async Task<ScenarioOutcome> WithOutcomeShownOnceAsync(
        ConformanceContext context,
        Func<Task<(ScenarioOutcome Outcome, bool Shown)>> scenario,
        CancellationToken cancellationToken)
    {
        if (context.PlatformStub is not { } stub)
        {
            return (await scenario()).Outcome;
        }

        await stub.ResetAsync(cancellationToken);
        var (outcome, shown) = await scenario();
        if (outcome.Status is not ScenarioStatus.Passed)
        {
            return outcome;
        }

        var actions = (await stub.GetPlatformLogAsync(cancellationToken))
            .Count(static action => string.Equals(action.Kind, PlatformAction.SentKind, StringComparison.Ordinal)
                || string.Equals(action.Kind, PlatformAction.EditedKind, StringComparison.Ordinal));
        var expected = shown ? 1 : 0;

        return actions == expected
            ? outcome
            : ScenarioOutcome.Failed(
                "the platform log holds " + actions.ToString(CultureInfo.InvariantCulture)
                + " action(s) instead of " + expected.ToString(CultureInfo.InvariantCulture)
                + (shown ? " for one outcome shown" : " for one outcome not shown"));
    }
}
