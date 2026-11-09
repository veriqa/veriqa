// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// The short code of a transaction — its second address, for a channel whose sign-in is started by a
/// code the user sends to the bot. One code per transaction, unique among the live codes of the
/// deployment; it answers until a start takes it or until <see cref="ExpiresAt"/>.
/// </summary>
/// <remarks>
/// The code is a secret of the transaction for as long as the transaction lives: it is never written
/// to a log or put into an exception message, on any level.
/// </remarks>
public sealed record ChannelStartCode
{
    /// <summary>
    /// The code in its normalized form: decimal digits only.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Transaction the code stands for.
    /// </summary>
    public required TransactionId TransactionId { get; init; }

    /// <summary>
    /// Moment the code stops answering — the deadline of the transaction.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Tenant of the transaction the code stands for; null — the default implicit tenant. A start by the
    /// code that arrives with a tenant of the event takes the code only when this is that tenant,
    /// compared ordinally (<see cref="IChannelStartCodeStore.TakeAsync"/>).
    /// </summary>
    public string? TenantId { get; init; }
}
