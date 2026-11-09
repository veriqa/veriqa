// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Gives a transaction its short code — the one already issued, or a new one. The single point codes
/// are issued at: the layer that shows a channel asks it, and a page that only repeats the code reads
/// <see cref="IChannelStartCodeStore.FindByTransactionAsync"/> instead and issues nothing.
/// </summary>
public interface IChannelStartCodeIssuer
{
    /// <summary>
    /// Returns the live code of the transaction, issuing one when there is none. A repeated call for
    /// the same transaction returns the same code while that code is live.
    /// </summary>
    /// <param name="transactionId">Transaction the code stands for.</param>
    /// <param name="tenantId">Tenant of the transaction, written into the record of a new code
    /// (<see cref="ChannelStartCode.TenantId"/>); null — the default implicit tenant.</param>
    /// <param name="expiresAt">Deadline of the transaction — the moment the code stops answering.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The code record. A failure
    /// (<see cref="Constants.ChannelAdapterErrorCodes.StartCodeIssueFailed"/>) — the transaction is
    /// already past its deadline, or no free code was found in the attempts allowed; the caller treats
    /// it the way it treats a channel that failed to build its link.</returns>
    Task<Result<ChannelStartCode>> GetOrIssueAsync(
        TransactionId transactionId,
        string? tenantId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);
}
