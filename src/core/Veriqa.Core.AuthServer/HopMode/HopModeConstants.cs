// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Constants of the hop authentication mode (SPEC-025).
/// </summary>
public static class HopModeConstants
{
    /// <summary>
    /// Base path of the hop resolve endpoint the product ships.
    /// </summary>
    public const string DefaultHopPath = "/r";

    /// <summary>
    /// Name of the route parameter carrying the hop token in the route of the resolve endpoint
    /// (<c>{HopPath}/{hop_token}</c>).
    /// </summary>
    internal const string HopTokenRouteParameter = "hop_token";

    /// <summary>
    /// Purpose string of the Data Protection protector of hop tokens: it isolates the token from every
    /// other use of Data Protection in the process, so a payload of another purpose never reads as a hop
    /// token (SPEC-025 HOP-034).
    /// </summary>
    internal const string HopTokenProtectorPurpose = "Veriqa.Core.HopMode.Token.v1";

    /// <summary>
    /// Version of the binary hop-token payload, written as its first byte. A payload of any other
    /// version does not decode, so a later layout can coexist with tokens already in circulation.
    /// </summary>
    internal const byte HopTokenFormatVersion = 1;

    /// <summary>
    /// Name of the cookie carrying the browser mark of a hop link — what tells a person's own reopening of
    /// the link from a reopening elsewhere (SPEC-041 L93). One name for every transaction: a browser that
    /// opens the link of another transaction replaces the mark, and its return to the first one reads as a
    /// reopening elsewhere — a false alarm the signal allows.
    /// </summary>
    internal const string BrowserMarkCookieName = "veriqa_hop_browser";

    /// <summary>
    /// Purpose string of the Data Protection protector of the browser mark: isolated from the hop token
    /// (<see cref="HopTokenProtectorPurpose"/>) and every other use of Data Protection, so a hop token or any
    /// other protected payload never reads as a mark, nor a mark as a token.
    /// </summary>
    internal const string BrowserMarkProtectorPurpose = "Veriqa.Core.HopMode.BrowserMark.v1";

    /// <summary>
    /// Longest channel type a hop token carries: the length cap of a channel type stated by
    /// <see cref="Veriqa.Core.ChannelAdapter.Constants.CustomChannelContract.ChannelTypePattern"/>
    /// (<c>[a-z][a-z0-9-]{0,63}</c>). The contract states that cap only inside the pattern, not as a
    /// number, so it is repeated here by name; the two must change together.
    /// </summary>
    internal const int MaxTokenChannelTypeLength = 64;

    /// <summary>
    /// Member of a channel section (<c>Veriqa:Channels:&lt;Type&gt;</c>) carrying the per-channel hop switch.
    /// An empty or absent member inherits the node-wide value of the same level (SPEC-025 HOP-120).
    /// </summary>
    public const string ChannelHopEnabledMember = "HopEnabled";

    /// <summary>
    /// Member of a channel section (<c>Veriqa:Channels:&lt;Type&gt;</c>) carrying the per-channel redirect
    /// style. An empty or absent member inherits the node-wide value of the same level.
    /// </summary>
    public const string ChannelHopRedirectStyleMember = "HopRedirectStyle";

    /// <summary>
    /// Version of the collector of the scanning-device context (semver) — the
    /// <c>CollectorVersion</c> every scanner context snapshot carries (SPEC-025 §8.1). It is its own
    /// version, not that of the initiator context collector: the scanner snapshot changes its set of
    /// fields on its own schedule, including the attributes its fingerprint is computed from (HOP-090b).
    /// </summary>
    public const string ScannerContextCollectorVersion = "1.1.0";

    /// <summary>
    /// The attributes of the browser the fingerprint of the launch page is computed from, in the order they
    /// are hashed (SPEC-025 HOP-090b). The page gets this list when it is built and hashes the lines
    /// <c>name=value</c> joined by a line feed (an absent value is empty) as UTF-8 by SHA-256, sent in
    /// base64url without padding. Only the hash goes over the wire, never the attributes.
    /// <para>
    /// The list is the names of the table where each attribute is stated together with the way the page
    /// reads it; the page walks that same table, so the list and the script cannot disagree.
    /// </para>
    /// <para>
    /// The set is part of the collector: a change of it changes the fingerprint of every scanner, so it
    /// goes together with a new <see cref="ScannerContextCollectorVersion"/>. It holds cheap deterministic
    /// properties of the navigator and the screen only: <c>screen</c> is the shorter side, the longer side
    /// and the color depth, so a turn of the device does not change it, and the device pixel ratio is left
    /// out because the zoom of the page changes it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> FingerprintAttributes { get; } =
        [.. CorePageBrowserFingerprint.Hashed.Select(attribute => attribute.Name)];

    /// <summary>
    /// Length of a browser fingerprint as the launch page sends it (SPEC-025 HOP-090b): a SHA-256 digest
    /// in base64url without padding (RFC 4648 §5) — 43 characters of <c>[A-Za-z0-9_-]</c>. A value of any
    /// other form refuses the signal that carries it.
    /// </summary>
    public const int FingerprintLength = 43;

    /// <summary>
    /// Largest body of a client signal the receiver reads, in bytes (SPEC-025 HOP-066b). The schema of the
    /// body is fixed and its payload is a few hundred bytes; a larger body is answered <c>413</c> before
    /// the signal is looked at.
    /// </summary>
    public const int ClientSignalMaxBodyBytes = 1024;

    /// <summary>
    /// Shortest own lifetime of a hop token a deployment may state (SPEC-025 HOP-151): long enough to
    /// scan the code and open the link, including after the page re-issues the code on a share of the
    /// lifetime.
    /// </summary>
    public static readonly TimeSpan MinimumTokenLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Share of the own lifetime of a hop token after which a page asks for its replacement (SPEC-025
    /// HOP-152): a token issued at <c>IssuedAt</c> and expiring at <c>ExpiresAt</c> is replaced at
    /// <c>IssuedAt + ReissueFraction × (ExpiresAt − IssuedAt)</c>. With the shortest lifetime of
    /// <see cref="MinimumTokenLifetime"/> the replacement comes at the 45th second and leaves 15 seconds to
    /// deliver and draw the new code. The one value the script of the page schedules by, the hub limits
    /// the frequency of issue by, and the resolve endpoint observes a superseded token by.
    /// </summary>
    public const double ReissueFraction = 0.75;

    /// <summary>
    /// Delay, in milliseconds, after which a page asks again for the replacement of a QR code whose
    /// re-issue failed — a failed entry of the answer of the hub or a failed call. No token is issued by
    /// a failure, so a retry does not go past the frequency limit of issue (SPEC-025 HOP-152).
    /// </summary>
    public const int ReissueRetryDelayMs = 5000;

    /// <summary>
    /// Precision of the own expiry of a hop token as its codec carries it. A token issued with the expiry
    /// of its transaction reads as expiring earlier than the transaction by less than this after encoding;
    /// so a token expires before its transaction only when its expiry is earlier than the expiry of the
    /// transaction minus this tolerance (SPEC-025 HOP-153, HOP-155).
    /// </summary>
    public static readonly TimeSpan ExpiryTolerance = TimeSpan.FromSeconds(1);
}
