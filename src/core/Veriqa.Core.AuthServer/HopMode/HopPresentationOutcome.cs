// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// What the check of a presented hop token found (steps 2–3 of the resolve endpoint, SPEC-025 HOP-074):
/// either the refusal, with whatever was established before it, or everything the transition into the
/// channel needs.
/// </summary>
/// <param name="Refusal">Why the presentation does not lead into the channel; null — the transition is admitted.</param>
/// <param name="Token">The decoded token; null only for <see cref="HopRefusal.TokenUndecodable"/>.</param>
/// <param name="Transaction">
/// The transaction the token names; null for <see cref="HopRefusal.TokenUndecodable"/>,
/// <see cref="HopRefusal.TransactionNotFound"/> and a <see cref="HopRefusal.TransactionExpired"/> reported by
/// the loading of the transaction.
/// </param>
/// <param name="Adapter">
/// The adapter of the channel of the token; not null when the transition is admitted with the token of one
/// channel, null with the unified token.
/// </param>
/// <param name="ChannelMode">
/// The hop mode of the channel in the context of the transaction; not null when the transition is
/// admitted with the token of one channel, and its redirect style is the form of the answer; null with the
/// unified token, to which no redirect style applies (SPEC-025 HOP-144).
/// </param>
internal sealed record HopPresentationOutcome(
    HopRefusal? Refusal,
    HopToken? Token,
    Transaction? Transaction,
    IChannelAdapter? Adapter,
    HopChannelMode? ChannelMode)
{
    /// <summary>
    /// Error code the loading of the transaction failed with — set for the refusals the loading decides,
    /// so the log names the cause the transaction service reported; null otherwise.
    /// </summary>
    public string? LoadErrorCode { get; init; }

    /// <summary>
    /// The channel set of the transaction, in display order, as the check of the unified token judged it
    /// (SPEC-025 HOP-143) — not null exactly when the transition is admitted with the unified token;
    /// <see cref="Adapter"/> and <see cref="ChannelMode"/> are null then.
    /// </summary>
    public IReadOnlyList<string>? UnifiedChannelSet { get; init; }
}
