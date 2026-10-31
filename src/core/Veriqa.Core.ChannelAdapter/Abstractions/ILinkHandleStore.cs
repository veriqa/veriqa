// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Store of the link handles issued into the addresses of post-completion buttons, keyed by the
/// fingerprint of the handle. A record is handed out once — to the client its sign-in is attributed
/// to — and lives until then or until its <see cref="LinkHandleRecord.ExpiresAt"/>.
/// </summary>
/// <remarks>
/// The default implementation is in-process. A handle can be exchanged on another instance than the
/// one that issued it, so a multi-instance deployment registers a shared implementation
/// (<c>UseRedisChannelStores</c>).
/// </remarks>
public interface ILinkHandleStore
{
    /// <summary>
    /// Records an issued handle.
    /// </summary>
    /// <param name="record">Record of the handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(LinkHandleRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands the record of a handle out and removes it, but only while it is there, is not past its
    /// <see cref="LinkHandleRecord.ExpiresAt"/> and names the given client (ordinal comparison). The
    /// comparison and the removal are one operation of the store: of several concurrent calls for one
    /// handle exactly one receives the record. A call of another client leaves the record in place for
    /// its own client.
    /// </summary>
    /// <param name="handleHash">Fingerprint of the presented handle.</param>
    /// <param name="applicationId">Client presenting the handle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record; null — there is none, it is past its deadline, or it names another client.</returns>
    Task<LinkHandleRecord?> TakeAsync(
        string handleHash,
        string applicationId,
        CancellationToken cancellationToken = default);
}
