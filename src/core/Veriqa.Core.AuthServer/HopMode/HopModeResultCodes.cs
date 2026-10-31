// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Result codes of the hop resolve endpoint (SPEC-025 §14, HOP-074, HOP-130): the codes the log entries
/// of a presentation — refused or observed — or of a client signal, and the audit record carry. They reach
/// the journal of the integrator, so they are as stable as a wire-level code; the page the user sees names
/// none of them.
/// </summary>
public static class HopModeResultCodes
{
    /// <summary>
    /// The presented token cannot be accepted: it does not decode, it leads to no transaction, or it was
    /// issued for a channel the transaction does not admit.
    /// </summary>
    public const string TokenInvalid = "hop_token_invalid";

    /// <summary>
    /// The token or the transaction it leads to has run out of time.
    /// </summary>
    public const string TokenExpired = "hop_token_expired";

    /// <summary>
    /// The token is valid, but the way into its channel is not available now: the transaction is past
    /// the state of entering a channel, the channel is not registered, hop is off for it, or the target
    /// could not be obtained from the channel.
    /// </summary>
    public const string ChannelUnavailable = "hop_channel_unavailable";

    /// <summary>
    /// The context of the scanning device was not captured on this presentation (SPEC-025 §14): the
    /// presentation was made by a link-preview agent, or the first resolve of the link was already
    /// marked. The reason travels as a separate field of the log entry.
    /// </summary>
    public const string ScannerContextSkipped = "hop_scanner_context_skipped";

    /// <summary>
    /// The context of the scanning device was captured, but some of the fields its switches ask for gave
    /// no value (SPEC-025 HOP-081); the entry names the missing fields, never the values.
    /// </summary>
    public const string ScannerContextPartial = "hop_scanner_context_partial";

    /// <summary>
    /// A client signal of the launch page was refused by the receiver (SPEC-025 §14, HOP-066b, HOP-190):
    /// the body is not of the fixed schema, or a condition of acceptance does not hold. The reason travels
    /// as a separate field of the log entry; the answer is the same <c>204</c> either way. A token that
    /// cannot be accepted is recorded as <see cref="TokenInvalid"/> instead.
    /// </summary>
    public const string ClientSignalRejected = "hop_client_signal_rejected";

    /// <summary>
    /// A person presented a hop token the page has already replaced, or was due to replace, with a new one
    /// (SPEC-025 HOP-155): the token expires before its transaction and the share of its own lifetime after
    /// which the page asks for a replacement has passed. An observation only — the answer, the audit and the
    /// statuses are the same as without it; the entry names the transaction and the channel of the token,
    /// never the token.
    /// </summary>
    public const string TokenSuperseded = "hop_token_superseded";
}
