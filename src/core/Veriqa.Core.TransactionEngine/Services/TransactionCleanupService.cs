// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Constants;
using Veriqa.Core.TransactionEngine.Diagnostics;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.TransactionEngine.Services;

/// <summary>
/// Background transaction cleanup service.
/// Performs:
/// 1. Expiration: moves expired transactions to Expired.
/// 2. Finalization timeout: moves stalled Confirmed transactions to Failed.
/// 3. Cleanup: removes old terminal transactions.
/// </summary>
internal sealed class TransactionCleanupService : BackgroundService
{
    /// <summary>
    /// Transaction store.
    /// </summary>
    private readonly ITransactionStore _store;

    /// <summary>
    /// Event publisher.
    /// </summary>
    private readonly ITransactionEventPublisher _eventPublisher;

    /// <summary>
    /// Transaction Engine configuration.
    /// </summary>
    private readonly IOptionsMonitor<TransactionEngineOptions> _options;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<TransactionCleanupService> _logger;

    /// <summary>
    /// Clock behind the expiry, stall and retention boundaries of the cleanup cycle.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Lifecycle metrics of the finalization timeout, the terminal transition this service drives
    /// itself.
    /// </summary>
    private readonly TransactionMetrics _metrics;

    /// <summary>
    /// The expiry step this pass shares with the transaction service, which takes it when a decision
    /// arrives after the deadline.
    /// </summary>
    private readonly TransactionExpiry _expiry;

    /// <summary>
    /// Creates an instance of the cleanup service.
    /// </summary>
    public TransactionCleanupService(
        ITransactionStore store,
        ITransactionEventPublisher eventPublisher,
        IOptionsMonitor<TransactionEngineOptions> options,
        ILogger<TransactionCleanupService> logger,
        TimeProvider timeProvider,
        TransactionMetrics metrics)
    {
        _store = store;
        _eventPublisher = eventPublisher;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;
        _metrics = metrics;
        _expiry = new TransactionExpiry(store, eventPublisher, timeProvider, metrics);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Transaction cleanup service started");

        // Determine whether the store supports native expiry (for example, Redis TTL)
        var supportsNativeExpiry = _store is ISupportsNativeExpiry;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var engineOptions = _options.CurrentValue;

                // ExpireTransactionsAsync works for all stores.
                // Redis uses a sorted set expiry index to find expired transactions
                // and publish TransactionExpiredEvent — without it, TTL would delete keys silently.
                await ExpireTransactionsAsync(engineOptions, stoppingToken);

                // For stores with native TTL (Redis) we skip deleting terminal transactions:
                // Redis will delete the keys itself once the extended TTL (CompletedRetentionSeconds) expires.
                if (!supportsNativeExpiry)
                {
                    await CleanupTerminalTransactionsAsync(engineOptions, stoppingToken);
                }

                await FailStalledConfirmedAsync(engineOptions, stoppingToken);

                // Wait until the next cycle
                await Task.Delay(
                    TimeSpan.FromSeconds(engineOptions.CleanupIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in the transaction cleanup service");

                // Use a fallback for the retry delay: if _options.CurrentValue threw an exception,
                // take the default value from a new TransactionEngineOptions instance
                var retryDelaySeconds = GetRetryDelaySeconds();

                // Wait before retrying, handling normal shutdown
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Transaction cleanup service stopped");
    }

    /// <summary>
    /// Moves the transactions their passed deadline ends into the Expired state — the ones no decision
    /// arriving after the deadline has already moved (that path takes the same step on its own).
    /// </summary>
    private async Task ExpireTransactionsAsync(TransactionEngineOptions options, CancellationToken cancellationToken)
    {
        // Get a batch of expired transactions
        var now = _timeProvider.GetUtcNow();
        var expired = await _store.GetExpiredAsync(now, options.CleanupBatchSize, cancellationToken);

        if (expired.Count is 0)
        {
            return;
        }

        var expiredCount = 0;

        foreach (var transaction in expired)
        {
            // A record the step leaves alone is either one the store handed back although its deadline
            // does not end it — the contract of GetExpiredAsync says it must not, and the shipped stores
            // do not; the transition table inside the step is the gate that holds for a store written
            // outside these assemblies too — or one another writer got to first: a decision that met
            // the deadline, a parallel pass. Either way there is nothing of this pass to announce.
            if (await _expiry.TryExpireAsync(transaction, cancellationToken))
            {
                expiredCount++;
            }
        }

        if (expiredCount > 0)
        {
            _logger.LogInformation(
                "Cleanup: {ExpiredCount} transactions moved to Expired",
                expiredCount);
        }
    }

    /// <summary>
    /// Moves stalled Confirmed transactions to Failed with the finalization_timeout code.
    /// </summary>
    private async Task FailStalledConfirmedAsync(TransactionEngineOptions options, CancellationToken cancellationToken)
    {
        // Determine the threshold for stalled Confirmed transactions
        var threshold = _timeProvider.GetUtcNow().AddSeconds(-options.ConfirmationFinalizationTimeoutSeconds);
        var stalled = await _store.GetStalledConfirmedAsync(threshold, options.CleanupBatchSize, cancellationToken);

        if (stalled.Count is 0)
        {
            return;
        }

        var failedCount = 0;

        foreach (var transaction in stalled)
        {
            var previousToken = transaction.ConcurrencyToken;
            var transitionResult = TransactionStateMachine.TryTransition(
                transaction, TransactionState.Failed, _timeProvider.GetUtcNow(), TransactionErrorCodes.FinalizationTimeout);

            if (transitionResult.IsFailure)
            {
                continue;
            }

            var updated = await _store.UpdateAsync(transaction, previousToken, cancellationToken);
            if (!updated)
            {
                continue;
            }

            failedCount++;

            // A stalled confirmation is a system failure, not the user's refusal, so it joins the
            // "failed" outcome the transaction service reports for every other non-refusal code.
            _metrics.RecordTerminated(transaction, TransactionOutcomes.Failed, _timeProvider.GetUtcNow());

            // Publish the event
            await _eventPublisher.PublishAsync(
                new TransactionFailedEvent
                {
                    TransactionId = transaction.Id,
                    OccurredAt = _timeProvider.GetUtcNow(),
                    ReasonCode = TransactionErrorCodes.FinalizationTimeout,
                    Context = TransactionEventContext.FromTransaction(transaction)
                },
                cancellationToken);
        }

        if (failedCount > 0)
        {
            _logger.LogWarning(
                "Cleanup: {FailedCount} transactions moved to Failed (finalization_timeout)",
                failedCount);
        }
    }

    /// <summary>
    /// Removes terminal transactions whose retention period has expired.
    /// </summary>
    private async Task CleanupTerminalTransactionsAsync(TransactionEngineOptions options, CancellationToken cancellationToken)
    {
        // Determine the deletion threshold
        var threshold = _timeProvider.GetUtcNow().AddSeconds(-options.CompletedRetentionSeconds);
        var stale = await _store.GetStaleTerminalAsync(threshold, options.CleanupBatchSize, cancellationToken);

        if (stale.Count is 0)
        {
            return;
        }

        foreach (var transaction in stale)
        {
            await _store.DeleteAsync(transaction.Id, cancellationToken);
        }

        _logger.LogInformation(
            "Cleanup: {CleanedCount} terminal transactions removed from the store",
            stale.Count);
    }

    /// <summary>
    /// Safely gets the retry delay from configuration.
    /// On a configuration read error, logs a warning and returns the default value.
    /// <para>
    /// This method is called from inside the loop's own <c>catch</c>, so an exception thrown here has
    /// nowhere left to go: it leaves <see cref="ExecuteAsync"/>, and with the default
    /// <c>BackgroundServiceExceptionBehavior.StopHost</c> it takes the whole host down. The
    /// guard is therefore as wide as the read: <see cref="IOptionsMonitor{TOptions}.CurrentValue"/>
    /// re-binds the section and re-runs the validator after every reload, and both halves can fail —
    /// a rule reports an <see cref="OptionsValidationException"/>, while a value that does not convert
    /// to its declared property type fails earlier, inside the binder, as an
    /// <see cref="InvalidOperationException"/>. Cancellation is not a configuration failure and stays
    /// unhandled, so shutdown keeps propagating to the caller's own cancellation handling.
    /// </para>
    /// </summary>
    /// <returns>Delay in seconds.</returns>
    private int GetRetryDelaySeconds()
    {
        // Read the delay from the current configuration; on error, fall back to the default value
        try
        {
            return _options.CurrentValue.CleanupErrorRetryDelaySeconds;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The configuration could not be read — use the fallback and keep the loop alive
            _logger.LogWarning(
                ex,
                "TransactionEngine configuration could not be read while getting CleanupErrorRetryDelaySeconds, using the default value");

            return new TransactionEngineOptions().CleanupErrorRetryDelaySeconds;
        }
    }
}
