// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance;

/// <summary>
/// Exit codes of the suite — the answer a CI job reads.
/// </summary>
internal static class ConformanceExitCodes
{
    /// <summary>
    /// No scenario failed.
    /// </summary>
    public const int Conforms = 0;

    /// <summary>
    /// At least one scenario failed.
    /// </summary>
    public const int Violations = 1;

    /// <summary>
    /// The suite could not carry out the run: a configuration of the suite or of the composition, the
    /// address of the receiver, a missing seam of the suite.
    /// </summary>
    public const int NotRun = 2;
}
