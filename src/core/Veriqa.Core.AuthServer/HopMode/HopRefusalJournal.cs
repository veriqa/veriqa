// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// Where a refused presentation of a hop URL is written down — the one record of a refusal for every
/// method of the resolve route (SPEC-025 HOP-074, HOP-114): a log entry for every refusal and, for the
/// refusals that name a transaction which can be recovered and are significant for security, an audit
/// event of that transaction.
/// <para>
/// Neither the token nor the hop URL reaches the record: the token is a capability. The audit event is
/// written only where the transaction was loaded — it is the target of the record; a token that does not
/// decode or names no transaction leaves the log entry alone.
/// </para>
/// </summary>
internal sealed class HopRefusalJournal
{
    /// <summary>
    /// Template of the log entry of a refusal.
    /// </summary>
    private const string RefusalLogTemplate =
        "The hop resolve endpoint refused the presentation. ResultCode: {ResultCode}, Reason: {Reason}, "
        + "TransactionId: {TransactionId}, Request: {Request}, LoadErrorCode: {LoadErrorCode}";

    /// <summary>
    /// Logger of the refusals.
    /// </summary>
    private readonly ILogger<HopRefusalJournal> _logger;

    /// <summary>
    /// Publisher the audit event goes out through.
    /// </summary>
    private readonly ITransactionEventPublisher _eventPublisher;

    /// <summary>
    /// Clock the moment of the audit event is taken from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the journal.
    /// </summary>
    /// <param name="logger">Logger of the refusals.</param>
    /// <param name="eventPublisher">
    /// Publisher of transaction events. It is registered by the transaction engine, so it resolves on every
    /// host that has transactions, while the audit sink exists only where the audit component was added: a
    /// host without it has no subscriber for the event.
    /// </param>
    /// <param name="timeProvider">Clock.</param>
    public HopRefusalJournal(
        ILogger<HopRefusalJournal> logger,
        ITransactionEventPublisher eventPublisher,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(eventPublisher);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _logger = logger;
        _eventPublisher = eventPublisher;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Writes down a refused presentation: the log entry of its row of the answer matrix and, where the
    /// row says so, the audit event of the transaction.
    /// </summary>
    /// <param name="outcome">Outcome of a refused presentation.</param>
    /// <param name="requestMarker">
    /// Who presented the URL (<see cref="HopRequestMarkers"/>); it never suppresses the record.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token of the request. The audit event is published regardless of it: the record must
    /// reach the journal even if the browser goes away, otherwise abandoning the request would suppress
    /// its own trace.
    /// </param>
    /// <returns>The completed operation.</returns>
    /// <exception cref="ArgumentException">The outcome is not a refusal.</exception>
    public async ValueTask RecordAsync(
        HopPresentationOutcome outcome,
        string requestMarker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentException.ThrowIfNullOrEmpty(requestMarker);

        if (outcome.Refusal is not { } refusal)
        {
            throw new ArgumentException("Only a refused presentation is written down.", nameof(outcome));
        }

        var entry = EntryOf(refusal);

        // transaction.Id.ToString() is Base62 — no log injection. The identifier is that of the loaded
        // transaction only: nothing read out of the token reaches the log.
        _logger.Log(
            entry.LogLevel,
            RefusalLogTemplate,
            entry.ResultCode,
            refusal,
            outcome.Transaction?.Id.ToString(),
            requestMarker,
            outcome.LoadErrorCode);

        if (entry.IsAudited && outcome is { Transaction: { } transaction, Token: { } token })
        {
            await PublishAuditAsync(transaction, token, entry.ResultCode, requestMarker);
        }
    }

    /// <summary>
    /// The row of the answer matrix a refusal belongs to (SPEC-025 HOP-074): its result code, the level
    /// of its log entry and whether it is audited. Only the refusals whose transaction was loaded and
    /// whose occurrence is significant for security are audited; an expiry and a condition of the
    /// configuration are not.
    /// </summary>
    /// <param name="refusal">The refusal.</param>
    /// <returns>The row.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The refusal is not a member of the enumeration.</exception>
    private static RefusalEntry EntryOf(HopRefusal refusal) => refusal switch
    {
        HopRefusal.TokenUndecodable => new(HopModeResultCodes.TokenInvalid, LogLevel.Warning, IsAudited: false),
        HopRefusal.TransactionNotFound => new(HopModeResultCodes.TokenInvalid, LogLevel.Warning, IsAudited: false),
        HopRefusal.ChannelTypeMismatch => new(HopModeResultCodes.TokenInvalid, LogLevel.Warning, IsAudited: true),
        HopRefusal.TokenExpired => new(HopModeResultCodes.TokenExpired, LogLevel.Information, IsAudited: false),
        HopRefusal.TransactionExpired => new(HopModeResultCodes.TokenExpired, LogLevel.Information, IsAudited: false),
        HopRefusal.NotInTransitionState => new(HopModeResultCodes.ChannelUnavailable, LogLevel.Warning, IsAudited: true),
        HopRefusal.AdapterMissing => new(HopModeResultCodes.ChannelUnavailable, LogLevel.Information, IsAudited: false),
        HopRefusal.HopDisabled => new(HopModeResultCodes.ChannelUnavailable, LogLevel.Information, IsAudited: false),
        HopRefusal.UnifiedSetEmpty => new(HopModeResultCodes.ChannelUnavailable, LogLevel.Information, IsAudited: false),
        HopRefusal.UnifiedModeInactive => new(HopModeResultCodes.ChannelUnavailable, LogLevel.Information, IsAudited: false),
        HopRefusal.TargetNotObtained => new(HopModeResultCodes.ChannelUnavailable, LogLevel.Warning, IsAudited: false),
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "Unknown refusal of a hop presentation.")
    };

    /// <summary>
    /// Publishes the audit event of a refusal. A failure of the publication does not change the answer:
    /// it is logged, and the visitor gets the same page either way.
    /// </summary>
    /// <param name="transaction">The transaction the refusal is recorded against — the target of the record.</param>
    /// <param name="token">The token; only its channel type is recorded.</param>
    /// <param name="resultCode">Result code of the refusal.</param>
    /// <param name="requestMarker">Who presented the URL.</param>
    /// <returns>The completed operation.</returns>
    private async ValueTask PublishAuditAsync(
        Transaction transaction,
        HopToken token,
        string resultCode,
        string requestMarker)
    {
        try
        {
            // CancellationToken.None — see RecordAsync. No attribution is attached: the visitor is
            // unauthenticated by definition, and the identity the transaction carries belongs to its
            // owner, who did not act.
            await _eventPublisher.PublishAsync(
                new TransactionChannelAuditEvent
                {
                    TransactionId = transaction.Id,
                    OccurredAt = _timeProvider.GetUtcNow(),
                    ChannelType = token.ChannelType,
                    EventCode = resultCode,
                    Details = requestMarker
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish audit event {EventCode}. TransactionId: {TransactionId}",
                resultCode,
                transaction.Id.ToString());
        }
    }

    /// <summary>
    /// One row of the answer matrix as the journal reads it.
    /// </summary>
    /// <param name="ResultCode">Result code of the refusal.</param>
    /// <param name="LogLevel">Level of the log entry.</param>
    /// <param name="IsAudited">Whether an audit event is published.</param>
    private readonly record struct RefusalEntry(string ResultCode, LogLevel LogLevel, bool IsAudited);
}
