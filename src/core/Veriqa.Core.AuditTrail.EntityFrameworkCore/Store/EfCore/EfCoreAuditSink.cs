// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Veriqa.Core.AuditTrail.Retention;
using Veriqa.Core.Contracts.Audit;

namespace Veriqa.Core.AuditTrail.Store.EfCore;

/// <summary>
/// EF Core audit sink (provider-agnostic). Works through IDbContextFactory, which makes it safe to
/// use from a singleton.
/// </summary>
/// <remarks>
/// A failing append is not swallowed and not retried here: the caller logs it and moves on, and a
/// retry would risk a duplicate record in a journal that cannot be corrected afterwards.
/// </remarks>
internal sealed class EfCoreAuditSink : IAuditSink, IAuditRetentionStore
{
    /// <summary>
    /// DbContext factory for creating a context per operation.
    /// </summary>
    private readonly IDbContextFactory<AuditDbContext> _contextFactory;

    /// <summary>
    /// Creates the EF Core audit sink.
    /// </summary>
    /// <param name="contextFactory">DbContext factory.</param>
    public EfCoreAuditSink(IDbContextFactory<AuditDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        context.AuditRecords.Add(ToEntity(record));

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> DeleteOlderThanAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // The batch is selected first and deleted by key: a row limit inside a bulk delete is not
        // expressible on every relational provider, while a delete restricted to a known set of
        // keys is. The oldest records go first, so a backlog is cleared from the far end.
        var expiredIds = await context.AuditRecords
            .Where(entity => entity.Timestamp < cutoff)
            .OrderBy(entity => entity.Timestamp)
            .Select(entity => entity.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (expiredIds.Count == 0)
        {
            return 0;
        }

        return await context.AuditRecords
            .Where(entity => expiredIds.Contains(entity.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Flattens a record into its storage entity.
    /// </summary>
    /// <remarks>
    /// Every member of the record has a column here: a field the mapping drops is lost in silence,
    /// with the row that reaches the database indistinguishable from the row of a deployment that
    /// never asked for the field at all.
    /// </remarks>
    /// <param name="record">Record to store.</param>
    /// <returns>Storage entity.</returns>
    private static AuditRecordEntity ToEntity(AuditRecord record) =>
        new()
        {
            Timestamp = record.Timestamp,
            Actor = record.Actor,
            Action = record.Action,
            Target = record.Target,
            Outcome = record.Result.Outcome.ToString(),
            ReasonCode = record.Result.ReasonCode,
            TransactionType = record.Metadata.TransactionType,
            ChannelType = record.Metadata.ChannelType,
            CorrelationId = record.Metadata.CorrelationId,
            TenantId = record.Metadata.TenantId,
            ClientId = record.Metadata.ClientId,
            UiLocale = record.Metadata.UiLocale,
            UiTimeZone = record.Metadata.UiTimeZone,
            ChannelDetails = record.Metadata.ChannelDetails,
            ChannelInboundVerification = record.Metadata.ChannelInboundVerification,
            DeletionBasis = record.Metadata.DeletionBasis,
            ConfirmationActionType = record.Metadata.ConfirmationParameters?.ActionType,
            ConfirmationSlotValuesJson = SerializeSlotValues(record.Metadata.ConfirmationParameters),
            Sequence = record.Sequence,
            PreviousRecordHash = record.PreviousRecordHash
        };

    /// <summary>
    /// Serializes the slot values of the confirmed operation into the JSON of their column.
    /// </summary>
    /// <param name="parameters">Parameters of the confirmed operation, if the record carries them.</param>
    /// <returns>JSON of the slot values, or null when there are none to store.</returns>
    private static string? SerializeSlotValues(AuditConfirmationParameters? parameters)
    {
        if (parameters?.SlotValues is not { Count: > 0 } slotValues)
        {
            return null;
        }

        return JsonSerializer.Serialize(slotValues);
    }
}
