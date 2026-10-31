// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Running;

/// <summary>
/// Status of a scenario in the report.
/// </summary>
internal enum ScenarioStatus
{
    /// <summary>
    /// The adapter behaved as the wire requires.
    /// </summary>
    Passed,

    /// <summary>
    /// The adapter did not; the reason says how.
    /// </summary>
    Failed,

    /// <summary>
    /// The scenario does not apply to this run; the reason says why.
    /// </summary>
    NotApplicable
}

/// <summary>
/// The outcome of one scenario. A reason is required of a failed and of a not applicable scenario.
/// </summary>
internal sealed record ScenarioOutcome
{
    /// <summary>
    /// Creates an outcome.
    /// </summary>
    /// <param name="status">Status.</param>
    /// <param name="reason">Reason; <c>null</c> only for a passed scenario.</param>
    private ScenarioOutcome(ScenarioStatus status, string? reason)
    {
        Status = status;
        Reason = reason;
    }

    /// <summary>
    /// A passed scenario.
    /// </summary>
    public static ScenarioOutcome Passed { get; } = new(ScenarioStatus.Passed, null);

    /// <summary>
    /// Status.
    /// </summary>
    public ScenarioStatus Status { get; }

    /// <summary>
    /// Reason; <c>null</c> for a passed scenario.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// A failed scenario.
    /// </summary>
    /// <param name="reason">How the adapter departed from the wire.</param>
    /// <returns>The outcome.</returns>
    public static ScenarioOutcome Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(ScenarioStatus.Failed, reason);
    }

    /// <summary>
    /// A scenario that does not apply to this run.
    /// </summary>
    /// <param name="reason">Why it does not apply.</param>
    /// <returns>The outcome.</returns>
    public static ScenarioOutcome NotApplicable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(ScenarioStatus.NotApplicable, reason);
    }
}
