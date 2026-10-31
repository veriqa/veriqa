// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Events;

namespace Veriqa.Core.AuthServer.HopMode;

/// <summary>
/// The one point the hop mode publishes its intermediate channel statuses through
/// (<see cref="HopChannelStatusKeys"/>, SPEC-025 HOP-100, HOP-161): the existing
/// <see cref="TransactionChannelStatusChangedEvent"/>, which the existing handler relays to the group of
/// the transaction on the hub method of channel statuses. No other surface carries these codes — not the
/// polled status, not the lifecycle message, not the audit (HOP-104a).
/// </summary>
/// <remarks>
/// A status describes a fact that has already happened, so it is published regardless of the request
/// (HOP-189): an aborted request must not lose it. A failure of the publication never changes the
/// answer of the endpoint (HOP-064): it is logged and swallowed.
/// </remarks>
internal sealed class HopChannelStatusPublisher
{
    /// <summary>
    /// Clock the moment of the event is taken from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<HopChannelStatusPublisher> _logger;

    /// <summary>
    /// Publisher of transaction events; null — none is registered, and there is nothing to publish to.
    /// </summary>
    private readonly ITransactionEventPublisher? _eventPublisher;

    /// <summary>
    /// Creates the publisher.
    /// </summary>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="eventPublisher">
    /// Publisher of transaction events; a host without one publishes nothing, as the Email channel
    /// statuses do in that case.
    /// </param>
    public HopChannelStatusPublisher(
        TimeProvider timeProvider,
        ILogger<HopChannelStatusPublisher> logger,
        ITransactionEventPublisher? eventPublisher = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _timeProvider = timeProvider;
        _logger = logger;
        _eventPublisher = eventPublisher;
    }

    /// <summary>
    /// Publishes an intermediate channel status of the hop family for a transaction, when the act
    /// publishes them (<see cref="HopObservationSettings.EmitScannedStatus"/>, SPEC-025 HOP-161).
    /// </summary>
    /// <param name="transaction">The transaction the status is about.</param>
    /// <param name="channelType">
    /// Channel type the event carries, as given; an empty one is allowed — a status may name no channel.
    /// </param>
    /// <param name="channelStatus">Code of the status (<see cref="HopChannelStatusKeys"/>).</param>
    /// <param name="settings">What the hop mode publishes, for this act.</param>
    /// <returns>The completed operation.</returns>
    public async Task PublishAsync(
        Transaction transaction,
        string channelType,
        string channelStatus,
        HopObservationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(channelType);
        ArgumentException.ThrowIfNullOrEmpty(channelStatus);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.EmitScannedStatus || _eventPublisher is null)
        {
            return;
        }

        try
        {
            await _eventPublisher.PublishAsync(
                new TransactionChannelStatusChangedEvent
                {
                    TransactionId = transaction.Id,
                    OccurredAt = _timeProvider.GetUtcNow(),
                    ChannelType = channelType,
                    ChannelStatus = channelStatus,
                    Context = TransactionEventContext.FromTransaction(transaction)
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish hop channel status {ChannelStatus}. TransactionId: {TransactionId}",
                channelStatus,
                transaction.Id.ToString());
        }
    }
}
