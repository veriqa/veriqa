// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Running;

/// <summary>
/// The JSON report of a run — the contract a CI job and the self-check of the suite read:
/// <c>{ "wire_version", "mode", "channel_type", "outcome": "passed"|"failed",
/// "scenarios": [ { "id", "status": "passed"|"failed"|"not_applicable", "reason" } ] }</c>.
/// </summary>
internal sealed class ConformanceReport
{
    /// <summary>
    /// Version of the wire the suite checks.
    /// </summary>
    public required string WireVersion { get; init; }

    /// <summary>
    /// Mode of the run.
    /// </summary>
    public required string Mode { get; init; }

    /// <summary>
    /// The channel under test.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// <c>passed</c> — no scenario failed; <c>failed</c> — at least one did.
    /// </summary>
    public required string Outcome { get; init; }

    /// <summary>
    /// The scenarios of the run, in the order they ran.
    /// </summary>
    public required IReadOnlyList<ConformanceReportScenario> Scenarios { get; init; }
}

/// <summary>
/// One scenario of the report.
/// </summary>
internal sealed class ConformanceReportScenario
{
    /// <summary>
    /// Stable identifier of the scenario.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// <c>passed</c>, <c>failed</c> or <c>not_applicable</c>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Reason; present for <c>failed</c> and <c>not_applicable</c>, <c>null</c> for <c>passed</c>.
    /// </summary>
    public required string? Reason { get; init; }
}

/// <summary>
/// Serialization of the report: snake_case names, <c>null</c> written out, indented.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = true)]
[JsonSerializable(typeof(ConformanceReport))]
internal sealed partial class ConformanceReportJsonContext : JsonSerializerContext
{
}
