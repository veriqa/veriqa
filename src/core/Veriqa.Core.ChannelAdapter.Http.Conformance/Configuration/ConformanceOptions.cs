// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;

/// <summary>
/// The keys <c>Conformance:*</c> of a run, read and checked by <see cref="ConformanceOptionsReader"/>.
/// </summary>
internal sealed record ConformanceOptions
{
    /// <summary>
    /// The registration <c>Veriqa:Channels:{type}</c> under test.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Mode of the run — exactly one flag.
    /// </summary>
    public required ConformanceModes Mode { get; init; }

    /// <summary>
    /// The user of the channel the calls outside events are addressed to.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// Tenants of the run, in the order of the configuration; <c>null</c> — the implicit tenant. Never
    /// empty: without the key the run has the implicit tenant alone.
    /// </summary>
    public required IReadOnlyList<string?> Tenants { get; init; }

    /// <summary>
    /// Path of the JSON report; <c>null</c> — no report.
    /// </summary>
    public string? ReportPath { get; init; }

    /// <summary>
    /// How long the receiver waits for an event.
    /// </summary>
    public required TimeSpan EventTimeout { get; init; }

    /// <summary>
    /// Identifier of the platform credential of each tenant at the stand (never the credential itself);
    /// keys are tenants of <see cref="Tenants"/>, the implicit one spelled
    /// <see cref="ConformanceConfigKeys.ImplicitTenant"/>.
    /// </summary>
    public required IReadOnlyDictionary<string, string> TenantCredentials { get; init; }

    /// <summary>
    /// Base address of the platform stub, ending with <c>/</c>; <c>null</c> outside the automatic mode.
    /// </summary>
    public Uri? PlatformStubUrl { get; init; }

    /// <summary>
    /// Whether the platform of the stand has a case in which it cannot carry a confirmation out;
    /// always <c>false</c> outside the automatic mode.
    /// </summary>
    public bool PlatformCannotConfirm { get; init; }

    /// <summary>
    /// The tenant the calls of the run are made in: the first of <see cref="Tenants"/>.
    /// </summary>
    public string? PrimaryTenant => Tenants[0];
}
