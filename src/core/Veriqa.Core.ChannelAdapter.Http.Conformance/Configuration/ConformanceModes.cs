// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;

/// <summary>
/// Modes of a run. A run has exactly one mode; a scenario states the set of modes it belongs to.
/// </summary>
[Flags]
internal enum ConformanceModes
{
    /// <summary>
    /// No mode.
    /// </summary>
    None = 0,

    /// <summary>
    /// The calls alone — the CI gate of a third-party author.
    /// </summary>
    Calls = 1,

    /// <summary>
    /// The calls and the receiver of events; the author plays the user on the platform.
    /// </summary>
    Interactive = 2,

    /// <summary>
    /// The calls, the receiver and the scenarios of the reference adapters; the stub of the reference
    /// adapter plays the user and the platform.
    /// </summary>
    Automatic = 4,

    /// <summary>
    /// Every mode.
    /// </summary>
    Any = Calls | Interactive | Automatic
}
