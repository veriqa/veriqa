// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// What the launch page — or the page of the channel buttons of one channel that launches the channel by
/// itself — sends back to the core (SPEC-025 HOP-066c), as the server decides it when the page is built —
/// from the observation settings of the same presentation.
/// </summary>
internal enum HopSignalScript
{
    /// <summary>
    /// No client signals: they are not accepted, or the settings could not be read. The page only launches
    /// the channel.
    /// </summary>
    None,

    /// <summary>
    /// The client signals, without a fingerprint.
    /// </summary>
    Signals,

    /// <summary>
    /// The client signals, and a browser fingerprint with the signal of the opened page: collection is
    /// effectively on for the channel (SPEC-025 HOP-090).
    /// </summary>
    SignalsWithFingerprint,

    /// <summary>
    /// The signal of the opened page with a browser fingerprint, and no <c>channel_launched</c>: the page of
    /// the channel buttons of one channel that launches the channel by itself (SPEC-025 HOP-195, HOP-090)
    /// while collection is effectively on. The transition into the channel is reported by the script of that
    /// page, the same way as a tap on its button, so the launch reports nothing of it.
    /// </summary>
    FingerprintOnly
}
