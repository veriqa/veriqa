// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Abstractions;

/// <summary>
/// Store of the short codes of transactions, addressed two ways: by the code (the message a user
/// sent to the bot) and by the transaction (the page that shows the code again).
/// </summary>
/// <remarks>
/// The codes of all the tenants of a deployment share one space: a code is unique among the live codes
/// of the deployment, and a start that arrives with no tenant of the event — the route without a tenant
/// segment, the bot of the installation — finds the code of a transaction of any tenant. A start that
/// arrives with a tenant takes only the code of a transaction of that tenant
/// (<see cref="ChannelStartCode.TenantId"/>), and the code of another tenant stays in the store as if
/// it had not been asked for.
/// <para>
/// The default implementation is in-process. A code is shown by one instance and sent to whichever
/// instance receives the webhook, so a multi-instance deployment registers a shared implementation.
/// </para>
/// </remarks>
public interface IChannelStartCodeStore
{
    /// <summary>
    /// Records a code, unless the store already holds a live record with the same code or a live
    /// record of the same transaction. The two checks and the write are one operation of the store.
    /// </summary>
    /// <param name="record">Record of the code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>true — recorded; false — a live record with the same code, or of the same
    /// transaction, already exists.</returns>
    Task<bool> TryAddAsync(ChannelStartCode record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the code of a transaction.
    /// </summary>
    /// <param name="transactionId">Transaction the code stands for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record; null — there is none, or it is past its <see cref="ChannelStartCode.ExpiresAt"/>.</returns>
    Task<ChannelStartCode?> FindByTransactionAsync(
        TransactionId transactionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a code and removes it, as one operation of the store: of two callers presenting the same
    /// code only one gets the record. With a tenant stated, the comparison of the tenant of the record is
    /// part of that same operation: a record of another tenant is neither returned nor removed.
    /// </summary>
    /// <param name="code">The code in its normalized form: decimal digits only.</param>
    /// <param name="ownerTenantId">Tenant of the event that presents the code; null — none, and the
    /// record is taken whatever its tenant. Otherwise the record is taken only when its
    /// <see cref="ChannelStartCode.TenantId"/> is this tenant, compared ordinally — a record of the
    /// implicit tenant included, it is not.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The record; null — there is none, it is past its <see cref="ChannelStartCode.ExpiresAt"/>,
    /// or it is of another tenant (and stays in the store).</returns>
    Task<ChannelStartCode?> TakeAsync(
        string code,
        string? ownerTenantId,
        CancellationToken cancellationToken = default);
}
