// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Why a presentation of a hop URL does not lead into the channel — one member per row of the answer
/// matrix of the resolve endpoint (SPEC-025 HOP-074), in the order the rows are checked. The result code,
/// the log level and whether an audit record is written are stated per member by
/// <see cref="HopRefusalJournal"/>.
/// </summary>
internal enum HopRefusal
{
    /// <summary>The presented string does not decode into a hop token.</summary>
    TokenUndecodable,

    /// <summary>The transaction the token names cannot be loaded.</summary>
    TransactionNotFound,

    /// <summary>
    /// The transaction does not admit the channel the token was issued for — the channel is outside its
    /// allowed set, or its registration belongs to a tenant other than that of the transaction (SPEC-042
    /// R60). Not checked for the unified token: its check of the channel is the check of the mode
    /// (<see cref="UnifiedModeInactive"/>).
    /// </summary>
    ChannelTypeMismatch,

    /// <summary>The own lifetime of the token is over (SPEC-025 HOP-149).</summary>
    TokenExpired,

    /// <summary>The transaction has run out of time.</summary>
    TransactionExpired,

    /// <summary>The transaction is not in the state of entering a channel.</summary>
    NotInTransitionState,

    /// <summary>No adapter of the channel of the token is registered.</summary>
    AdapterMissing,

    /// <summary>Hop is not on for the channel of the token in the context of the transaction.</summary>
    HopDisabled,

    /// <summary>
    /// The unified token — the one that carries no channel type — names a transaction with no channel left
    /// in its channel set; it takes the row of <see cref="AdapterMissing"/> (SPEC-025 HOP-143).
    /// </summary>
    UnifiedSetEmpty,

    /// <summary>
    /// The unified mode does not apply to the transaction of a unified token — the QR mode is per channel,
    /// or hop is on for no channel of the set; it takes the row of <see cref="HopDisabled"/>, after
    /// <see cref="UnifiedSetEmpty"/> (SPEC-025 HOP-142, HOP-143).
    /// </summary>
    UnifiedModeInactive,

    /// <summary>
    /// The channel did not give the target of the transition; for the unified token — no channel of the set
    /// gave one (SPEC-025 HOP-143).
    /// </summary>
    TargetNotObtained
}
