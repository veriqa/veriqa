// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration.Enums;
using Veriqa.Core.AuthServer.HopMode;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Node-wide settings of the hop authentication mode (SPEC-025 §11.1). Configuration section:
/// <c>Veriqa:HopMode</c>.
/// <para>
/// The effective value of every setting is resolved by the canonical resolver of levels through the keys
/// of <see cref="Resolution.HopModeConfigKeys"/>, and this class is the core level of them in a
/// self-hosted deployment (SPEC-025 HOP-121). The defaults below are the values the product ships and
/// the ones the keys declare. The per-channel switch and style live in the section of each channel
/// (<see cref="HopModeConstants.ChannelHopEnabledMember"/>,
/// <see cref="HopModeConstants.ChannelHopRedirectStyleMember"/>) and are read by the keys by channel.
/// </para>
/// </summary>
public sealed class HopModeOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Veriqa:HopMode";

    /// <summary>
    /// Hop for every channel of this level, unless the channel section states its own value. Off by
    /// default.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Base path of the hop resolve endpoint.
    /// </summary>
    public string HopPath { get; set; } = HopModeConstants.DefaultHopPath;

    /// <summary>
    /// How the resolve endpoint hands the scanning device over to the channel, unless the channel
    /// section states its own style.
    /// </summary>
    public HopRedirectStyle RedirectStyle { get; set; } = HopRedirectStyle.Interstitial;

    /// <summary>
    /// One hop QR code per channel (<see cref="HopQrMode.PerChannel"/>), one for the whole transaction
    /// (<see cref="HopQrMode.Unified"/>), or both (<see cref="HopQrMode.UnifiedWithChannels"/>) (SPEC-025 HOP-182).
    /// </summary>
    public HopQrMode QrMode { get; set; } = HopQrMode.PerChannel;

    /// <summary>
    /// Show the buttons of the channels on the "Any channel" tab, beside the unified QR code (SPEC-025 HOP-187).
    /// Takes effect only with <see cref="HopQrMode.UnifiedWithChannels"/>; the tab of each channel keeps its
    /// button either way. On by default.
    /// </summary>
    public bool ShowChannelButtonsOnAnyChannelTab { get; set; } = true;

    /// <summary>
    /// Own lifetime of a hop token, in the <see cref="TimeSpan"/> format of .NET configuration
    /// (<c>00:02:00</c>); null — the token lives as long as the transaction (SPEC-025 HOP-151). A level
    /// below may only shorten it, and it is never shorter than
    /// <see cref="HopModeConstants.MinimumTokenLifetime"/>.
    /// </summary>
    public TimeSpan? TokenLifetime { get; set; }

    /// <summary>
    /// Capture the context of the scanning device on resolve.
    /// </summary>
    public bool CollectScannerContext { get; set; } = true;

    /// <summary>
    /// Capture the IP address of the scanning device (best effort).
    /// </summary>
    public bool CollectIpAddress { get; set; } = true;

    /// <summary>
    /// Capture and normalize the User-Agent of the scanning device.
    /// </summary>
    public bool CollectUserAgent { get; set; } = true;

    /// <summary>
    /// Capture the country of the scanning device by GeoIP. Off by default (data minimization).
    /// </summary>
    public bool CollectGeoLocation { get; set; }

    /// <summary>
    /// Compute a browser fingerprint on the page a hop URL is answered with — the interstitial page or the
    /// page of the channel buttons — and accept it with the page signal. Takes effect only where there is
    /// such a page, with client signals accepted and scanner context captured (SPEC-025 HOP-090). Off by
    /// default (privacy, SPEC-025 HOP-092).
    /// </summary>
    public bool CollectFingerprint { get; set; }

    /// <summary>
    /// Keep the truncated raw User-Agent of the scanning device.
    /// </summary>
    public bool StoreRawUserAgent { get; set; }

    /// <summary>
    /// Publish the intermediate channel statuses of the hop family (scanned, page opened, channel
    /// launched) to the desktop.
    /// </summary>
    public bool EmitScannedStatus { get; set; } = true;

    /// <summary>
    /// Mark a transaction whose hop link a person opened again from another browser, and hand the browser
    /// of the first person's opening a mark that tells its own reopenings apart. A risk signal only: it
    /// grants and denies nothing.
    /// </summary>
    public bool WarnOnRepeatScan { get; set; } = true;

    /// <summary>
    /// Accept signals from the launch page back to the core: the signal receiver is mapped and the
    /// signal script is put on the interstitial page.
    /// </summary>
    public bool AcceptClientSignals { get; set; } = true;
}
