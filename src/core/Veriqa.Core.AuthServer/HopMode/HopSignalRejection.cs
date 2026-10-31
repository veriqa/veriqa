// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Why the receiver refused a client signal of the launch page or of the scanner page (SPEC-025 HOP-066b,
/// HOP-190, HOP-191) — the reason
/// field of the <see cref="HopModeResultCodes.ClientSignalRejected"/> log entry. A token that cannot be
/// accepted is not a reason here: it is recorded by <see cref="HopRefusalJournal"/> as a refused
/// presentation.
/// </summary>
internal enum HopSignalRejection
{
    /// <summary>The body is declared neither as <c>application/json</c> nor as <c>text/plain</c>.</summary>
    MediaType,

    /// <summary>The body is not a JSON object of the fixed schema, or its signal is unknown.</summary>
    Malformed,

    /// <summary>
    /// The fingerprint is not of its form (SPEC-025 HOP-090b), or it comes with a signal that does not
    /// carry one.
    /// </summary>
    FingerprintFormat,

    /// <summary>
    /// The body names a channel, and the token is that of one channel (SPEC-025 HOP-191) — other than the
    /// channel of the token named by the page of the channel buttons that channel is answered with.
    /// </summary>
    ChannelNotAllowed,

    /// <summary>
    /// The token is that of the unified QR code, and the unified mode does not apply to the transaction
    /// (SPEC-025 HOP-182, HOP-191).
    /// </summary>
    UnifiedModeInactive,

    /// <summary>
    /// The token resolves to the page of the channel buttons — the unified token, or the token of a channel
    /// answered with that page — and the signal is <c>interstitial_opened</c> while the browser
    /// fingerprint is not effectively collected: that page sends the signal only as the carrier of the
    /// fingerprint, so without the collection it is not a signal of the page (SPEC-025 HOP-090, HOP-191).
    /// </summary>
    SignalNotAllowed,

    /// <summary>
    /// The token is that of the unified QR code, and the body names no channel, which it must (SPEC-025
    /// HOP-191).
    /// </summary>
    ChannelMissing,

    /// <summary>
    /// The token is that of the unified QR code, and the channel the body names is not in the channel set
    /// of the transaction at the moment of the signal — the set is empty, or it does not hold that channel
    /// (SPEC-025 HOP-143, HOP-191).
    /// </summary>
    ChannelNotInSet,

    /// <summary>The transaction has run out of time.</summary>
    TransactionExpired,

    /// <summary>The transaction is not in the state of entering a channel.</summary>
    NotInTransitionState,

    /// <summary>No adapter of the channel of the token is registered.</summary>
    AdapterMissing,

    /// <summary>Hop is not on for the channel of the token in the context of the transaction.</summary>
    HopDisabled,

    /// <summary>Client signals are not accepted in the context of the transaction.</summary>
    SignalsDisabled,

    /// <summary>
    /// The presentation of the token is answered with a redirect — the redirect style of the channel is not
    /// <c>Interstitial</c>, and the channel shows no page of its own under it: there is no page to send the
    /// signal.
    /// </summary>
    NotInterstitial,

    /// <summary>The first resolve of the link is not marked: no page was shown to a person.</summary>
    NotResolved
}
