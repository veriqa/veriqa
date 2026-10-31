// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// What the hop mode captures about the scanning device and publishes, for one act, as the canonical
/// resolver answers the protective keys of SPEC-025 §11.1 for the caller's full resolution context.
/// </summary>
/// <param name="CollectScannerContext">Capture the context of the scanning device.</param>
/// <param name="CollectIpAddress">Capture the IP address of the scanning device.</param>
/// <param name="CollectUserAgent">Capture the User-Agent of the scanning device.</param>
/// <param name="CollectGeoLocation">Capture the country of the scanning device.</param>
/// <param name="CollectFingerprint">Collection of a browser fingerprint is switched on.</param>
/// <param name="StoreRawUserAgent">Keep the raw User-Agent.</param>
/// <param name="EmitScannedStatus">Publish the intermediate hop statuses.</param>
/// <param name="AcceptClientSignals">Accept signals from the launch page.</param>
/// <param name="WarnOnRepeatScan">Mark a reopening of the hop link from another browser.</param>
internal sealed record HopObservationSettings(
    bool CollectScannerContext,
    bool CollectIpAddress,
    bool CollectUserAgent,
    bool CollectGeoLocation,
    bool CollectFingerprint,
    bool StoreRawUserAgent,
    bool EmitScannedStatus,
    bool AcceptClientSignals,
    bool WarnOnRepeatScan)
{
    /// <summary>
    /// Whether a fingerprint is EFFECTIVELY collected on a page a presentation of a hop URL is answered
    /// with (SPEC-025 HOP-090): the switch is on, there is a receiver for the signal carrying it and a
    /// snapshot to keep it in. Any such page computes it — the launch page, the page of a channel that
    /// starts by a code and the page of the channel buttons — so the redirect style is no condition
    /// here: it only decides whether a channel has a page at all. The one predicate both sides ask — the
    /// page that puts the script in and the receiver that keeps the value.
    /// </summary>
    public bool CollectsFingerprintOnPage => CollectFingerprint && AcceptClientSignals && CollectScannerContext;
}
