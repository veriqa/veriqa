// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veriqa.Core.AuditTrail.DependencyInjection;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts.Audit;
using Veriqa.Core.TransactionEngine.Events;

namespace Veriqa.Core.AuditTrail.Receiver;

/// <summary>
/// Audit receiver: the single place that decides whether an event becomes an audit record
/// (SPEC-011 R28). It is an ordinary subscriber of the core event bus — publication itself is never
/// filtered, because the same bus drives the real-time sign-in UI.
/// </summary>
/// <remarks>
/// The decision is taken per event, at handling time: no per-transaction snapshot of the mode is
/// kept, and records missed in another mode are never reconstructed. A transaction that outlives a
/// mode switch therefore leaves a partial trail — that is the expected behaviour, not a defect.
/// Audit is off the critical path of a transaction: a failing sink or resolver is logged and
/// swallowed, never propagated back into the sign-in flow. Handling is also kept short with the
/// audit off, because the same dispatcher runs the real-time sign-in handlers after this one.
/// </remarks>
internal sealed class AuditTransactionEventHandler : ITransactionEventHandler
{
    /// <summary>
    /// Canonical resolver of the effective setting value.
    /// </summary>
    private readonly IConfigurationResolver _configurationResolver;

    /// <summary>
    /// Sink the journal is written to.
    /// </summary>
    private readonly IAuditSink _auditSink;

    /// <summary>
    /// What a record carries beyond the safe attributes every record carries.
    /// </summary>
    private readonly AuditRecordOptions _recordOptions;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<AuditTransactionEventHandler> _logger;

    /// <summary>
    /// Creates the audit receiver.
    /// </summary>
    /// <param name="configurationResolver">Configuration resolver.</param>
    /// <param name="auditSink">Audit sink.</param>
    /// <param name="recordOptions">Record content parameters.</param>
    /// <param name="logger">Logger.</param>
    public AuditTransactionEventHandler(
        IConfigurationResolver configurationResolver,
        IAuditSink auditSink,
        IOptions<AuditRecordOptions> recordOptions,
        ILogger<AuditTransactionEventHandler> logger)
    {
        _configurationResolver = configurationResolver;
        _auditSink = auditSink;
        _recordOptions = recordOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task HandleAsync(
        TransactionEvent transactionEvent,
        CancellationToken cancellationToken = default)
    {
        // Events outside the audited set leave no trace and cost nothing: they are filtered before
        // the mode is resolved.
        var action = AuditRecordMapper.MapAction(transactionEvent);
        if (action is null)
        {
            return;
        }

        try
        {
            // The mode is resolved FOR THE OWNER OF THE EVENT: the key is owned by the tenant, and
            // the tenant is the one the attribution carries — the receiver runs in a background loop
            // and has no ambient tenant of its own. An event that carries no attribution yields the
            // shared core context and costs nothing extra: today that is the self-hosted deployment,
            // where no level above the core answers at all, AND a channel security event whose
            // tenant is not known — such an event is judged by the core (SPEC-011 R44), whichever
            // levels the deployment serves. A refusal of the interaction surface (the channel side,
            // the confirmation page, the sign-in callback, hop, the Email sender check) of a known
            // tenant carries that tenant alone; an admissibility refusal of the transaction service
            // and a completion-behaviour clamp carry the whole attribution. Either way the mode is the
            // tenant's. With the audit off (the core default) this is the whole cost of an event.
            //
            // "Not Audit" carries two readings, and only one of them is a deployment asking for no
            // audit. WHERE THE AXIS IS DECLARED — its catalog travels with the configuration
            // composition of the auth server, and never with the audit trail — a mode the deployment
            // did state and nothing could read never turns the journal off quietly: the setting admits
            // only the modes the product ships and refuses to be served another value in place of one
            // it does not admit. What that refusal costs is the answer of the policy of the setting —
            // ConfigValueRejectionPolicy.FailStart (CFG-246), which states it once and turns it on the
            // level, on the reach of the walk of the snapshot and on the phase of the host — and is
            // not restated here, where it could only drift from it. Where the axis is NOT
            // declared, which is a host wiring the journal onto the transaction engine alone, no level
            // defines the mode at all: the resolution answers with the default of the type, this
            // branch returns, and the journal stays empty for as long as the host runs.
            var mode = await _configurationResolver.ResolveAsync(
                LoggingConfigKeys.Mode,
                ResolutionContext.ForTenant(transactionEvent.Context?.TenantId),
                ConfigDimensionValues.None,
                cancellationToken);

            if (mode.Value is not LoggingMode.Audit)
            {
                return;
            }

            var record = AuditRecordMapper.Map(
                transactionEvent,
                action,
                _recordOptions.IncludeConfirmationParameters,
                await ResolveChannelInboundVerificationAsync(transactionEvent, cancellationToken));

            await _auditSink.AppendAsync(record, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown of the event processor — not an audit failure.
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to write the audit record {Action} for transaction {TransactionId}",
                action,
                transactionEvent.TransactionId.ToString());
        }
    }

    /// <summary>
    /// Resolves the verification level the configuration DECLARES for the channel of this event
    /// (SPEC-011 C42): the fact is asked of the resolver by the channel type of the record, and the
    /// channel is the very one the record is attributed to
    /// (<see cref="AuditRecordMapper.ResolveChannelType"/>) rather than a second reading of the event.
    /// </summary>
    /// <remarks>
    /// It is resolved WHEN THE RECORD IS BUILT and not carried on the bus event: the attribution of an
    /// event is a closed safe set, and this fact is a statement of the configuration rather than a
    /// property of the transaction. It is resolved FOR THE TENANT OF THE EVENT all the same — the key
    /// declares that level and C42 names the configuration of the core or the tenant as the source, so
    /// the question "how does THIS owner verify the channel" is the only one with an answer. The
    /// consequence is accepted deliberately — a record written after the configuration was edited
    /// carries the new value.
    /// <para>
    /// A failed resolution leaves the attribute unset instead of taking the record down with it: the
    /// guard of this handler swallows a failure by losing the whole record, and a record that reaches
    /// the journal without one attribute is worth more than no record at all.
    /// </para>
    /// </remarks>
    /// <param name="transactionEvent">Bus event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Token of the declared level, or null when there is no channel or no declared value.</returns>
    private async Task<string?> ResolveChannelInboundVerificationAsync(
        TransactionEvent transactionEvent,
        CancellationToken cancellationToken)
    {
        // An event outside a channel (creation, activation, expiry) declares nothing to resolve, and
        // the resolution is not attempted at all.
        if (AuditRecordMapper.ResolveChannelType(transactionEvent) is not { } channelType)
        {
            return null;
        }

        try
        {
            var declared = await _configurationResolver.ResolveAsync(
                ChannelInboundVerificationConfigKeys.InboundVerification,
                ResolutionContext.ForTenant(transactionEvent.Context?.TenantId),
                ConfigDimensionValues.Of((ConfigDimensionNames.Channel, channelType)),
                cancellationToken);

            return declared.Value?.ToToken();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown of the event processor — handled by the caller as such.
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to resolve the declared inbound verification of channel {ChannelType}; "
                + "the audit record is written without the attribute",
                channelType);

            return null;
        }
    }
}
