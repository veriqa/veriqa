// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Record of one link handle — the one-time value the address of a post-completion button carries into
/// the bot of the integrator, which its server then exchanges for the identity of the signed-in user.
/// The record names the sign-in the handle stands for and the client that may exchange it. The handle
/// itself is not kept: the record is found by its fingerprint, so neither the store nor its backups can
/// hand the handle out.
/// </summary>
public sealed record LinkHandleRecord
{
    /// <summary>
    /// Fingerprint of the handle — the key of the record. Never the handle itself.
    /// </summary>
    public required string HandleHash { get; init; }

    /// <summary>
    /// Sign-in transaction the handle stands for.
    /// </summary>
    public required TransactionId TransactionId { get; init; }

    /// <summary>
    /// Client the sign-in is attributed to — the only one the record is handed to.
    /// </summary>
    public required string ApplicationId { get; init; }

    /// <summary>
    /// Moment the record stops answering.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
