// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Implementation of the post-completion action resolution (SPEC-012 §4.9). Consumes the canonical
/// level resolver (anti-fork CFG-202 — builds no precedence of its own): key
/// <see cref="CompletionBehaviorConfigKeys.Action"/> asked about the adapter's channel → the shipped
/// action where no level states one → clamp of an action the channel cannot render to the channel's
/// default, with a WARNING and an audit event.
/// </summary>
internal sealed class CompletionBehaviorResolver : ICompletionBehaviorResolver
{
    /// <summary>
    /// Post-completion behavior AS THE PRODUCT SHIPS IT — a default-constructed options object, read
    /// for one thing only: the action applied when no level states one. The shipped value is taken off
    /// the options class rather than restated here, so the two cannot drift.
    /// </summary>
    private static readonly CompletionBehaviorOptions Shipped = new();

    /// <summary>
    /// Canonical level resolver.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Clock the moment of the audit event is read off.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<CompletionBehaviorResolver> _logger;

    /// <summary>
    /// Bus the audit event goes to; null — the composition has no journal, and the WARNING is the
    /// only trace of a clamp.
    /// </summary>
    private readonly ITransactionEventPublisher? _eventPublisher;

    /// <summary>
    /// Creates the post-completion action resolver.
    /// </summary>
    /// <param name="resolver">Canonical level resolver.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="eventPublisher">Transaction event bus; optional — a host may compose none.</param>
    public CompletionBehaviorResolver(
        IConfigurationResolver resolver,
        TimeProvider timeProvider,
        ILogger<CompletionBehaviorResolver> logger,
        ITransactionEventPublisher? eventPublisher = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _eventPublisher = eventPublisher;
    }

    /// <inheritdoc />
    public async ValueTask<CompletionBehaviorAction> ResolveAsync(
        Transaction transaction,
        IChannelAdapter adapter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(adapter);

        // 1. Resolve the action along the ownership ladder (§10.3), asked about the adapter's channel:
        //    inside each level the per-channel entry is tried before the plain member (CFG-236).
        var resolved = await _resolver.ResolveAsync(
            CompletionBehaviorConfigKeys.Action,
            TransactionResolutionContext.For(transaction),
            ConfigDimensionValues.Of((ConfigDimensionNames.Channel, adapter.ChannelType)),
            cancellationToken);

        // 2. The key declares no answer for a deployment in which no level states the action, so an
        //    unanswered chain is read as the action the product ships with.
        var requested = resolved.SourceLevel is null ? Shipped.Action : resolved.Value;

        // 3. Doing nothing needs no rendering, so it is honoured on every channel as asked.
        if (requested is CompletionBehaviorAction.None)
        {
            return requested;
        }

        var supported = adapter.Capabilities.SupportedCompletionBehaviorActions;

        if (supported.Contains(requested))
        {
            return requested;
        }

        // 4. The channel cannot render what was asked for. The action is clamped straight to the
        //    channel's default — no ladder of weaker actions is walked — and a default the channel
        //    itself did not declare as supported is read as doing nothing. The log and the journal are
        //    the only way the operator learns about the replacement: the user notices none.
        var declaredDefault = adapter.Capabilities.DefaultCompletionBehaviorAction;

        var applied = supported.Contains(declaredDefault)
            ? declaredDefault
            : CompletionBehaviorAction.None;

        _logger.LogWarning(
            "completion behavior action {Requested} is not supported by channel {ChannelType}, applied {Applied}. TransactionId: {TransactionId}",
            requested,
            adapter.ChannelType,
            applied,
            transaction.Id);

        await PublishClampAsync(transaction, adapter.ChannelType, requested, applied);

        return applied;
    }

    /// <summary>
    /// Writes the clamp into the integrator's journal: one audit event on the transaction bus. The
    /// event carries the context of the transaction, so the record names the tenant and the
    /// application whose configuration asked for the action — the key is resolved on both levels. A
    /// failed publish never changes the resolved action — the WARNING above has already been written.
    /// <see cref="CancellationToken.None"/> for the publish: the occurrence must reach the journal
    /// even if the request that caused it is being cancelled.
    /// </summary>
    /// <param name="transaction">Transaction the action was resolved for.</param>
    /// <param name="channelType">Channel the action was clamped on.</param>
    /// <param name="requested">Action the deployment asked for.</param>
    /// <param name="applied">Action applied instead.</param>
    private async Task PublishClampAsync(
        Transaction transaction,
        string channelType,
        CompletionBehaviorAction requested,
        CompletionBehaviorAction applied)
    {
        if (_eventPublisher is null)
        {
            return;
        }

        try
        {
            await _eventPublisher.PublishAsync(
                new TransactionChannelAuditEvent
                {
                    TransactionId = transaction.Id,
                    OccurredAt = _timeProvider.GetUtcNow(),
                    ChannelType = channelType,
                    EventCode = ChannelAuditEventCodes.CompletionBehaviorClamped,
                    Details = $"requested={requested}; applied={applied}",
                    Context = TransactionEventContext.FromTransaction(transaction)
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish audit event {EventCode}. TransactionId: {TransactionId}",
                ChannelAuditEventCodes.CompletionBehaviorClamped,
                transaction.Id);
        }
    }
}
