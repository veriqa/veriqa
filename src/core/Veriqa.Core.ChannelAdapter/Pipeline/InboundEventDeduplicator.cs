// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// Drops a repeated delivery of an inbound channel event: an event whose key matches the one of an event
/// accepted less than <see cref="ChannelInboundDeduplicationOptions.Window"/> ago is not processed. A
/// platform that delivers one event twice almost at once (a webhook retried before the first answer)
/// would otherwise get two processings — two questions in the chat for one start.
/// </summary>
/// <remarks>
/// <para>
/// The key is built from the fields of the result explicitly — the channel type, the intent, the tenant
/// and the user of the identity, and the fields that tell two different actions of that user apart — and
/// never from the equality of the result: the raw platform event and the identity snapshot it carries
/// compare by reference. A press of a menu item, an event unrelated to a transaction and a message that
/// names none are always accepted: the host deduplicates presses by their identifier, and an answer to a
/// message without a transaction is no processing of one. The type of the transaction is not read: a
/// sign-in and a confirmation are deduplicated alike.
/// </para>
/// <para>
/// The window runs from the accepted event; a dropped repeat does not extend it, and the same event after
/// the window is processed as a new one. The check and the write of the mark are one atomic operation on
/// the dictionary, so of two identical events arriving at once exactly one is accepted. The marks live in
/// the memory of this process: a repeat delivered to another instance is not dropped. Marks older than the
/// window, or ahead of the clock by the window or more after it went back, are reclaimed by an
/// opportunistic sweep, with no background service.
/// </para>
/// </remarks>
internal sealed class InboundEventDeduplicator
{
    /// <summary>
    /// Run the sweep of expired marks every N events subject to the window, accepted or dropped.
    /// </summary>
    private const int CleanupEveryNCalls = 50;

    /// <summary>
    /// Moment each key was last accepted at.
    /// </summary>
    private readonly ConcurrentDictionary<InboundEventKey, DateTimeOffset> _acceptedAt = new();

    /// <summary>
    /// Clock the window is read against.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger the dropped repeats are written to.
    /// </summary>
    private readonly ILogger<InboundEventDeduplicator> _logger;

    /// <summary>
    /// Window of the installation; <see cref="TimeSpan.Zero"/> — no event is dropped.
    /// </summary>
    private readonly TimeSpan _window;

    /// <summary>
    /// Counter of the events subject to the window that triggers the sweep.
    /// </summary>
    private int _callCount;

    /// <summary>
    /// Guard flag (0/1) ensuring at most one sweep runs at a time.
    /// </summary>
    private int _cleanupInProgress;

    /// <summary>
    /// Creates the deduplicator.
    /// </summary>
    /// <param name="timeProvider">Clock the window is read against.</param>
    /// <param name="options">Settings of the window, validated at start.</param>
    /// <param name="logger">Logger the dropped repeats are written to.</param>
    public InboundEventDeduplicator(
        TimeProvider timeProvider,
        IOptions<ChannelInboundDeduplicationOptions> options,
        ILogger<InboundEventDeduplicator> logger)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _window = options.Value.Window;
    }

    /// <summary>
    /// Decides whether an inbound event is processed or dropped as a repeat, and marks an accepted one.
    /// </summary>
    /// <param name="channelType">Type of the channel the event arrived through.</param>
    /// <param name="result">Result of processing the inbound event, typed by intent.</param>
    /// <returns>true — process the event: it is the first with its key within the window, its intent is
    /// not deduplicated, or the window is off; false — the event repeats an accepted one and is dropped.</returns>
    public bool TryAccept(string channelType, ChannelInboundResult result)
    {
        ArgumentNullException.ThrowIfNull(channelType);
        ArgumentNullException.ThrowIfNull(result);

        if (_window <= TimeSpan.Zero || KeyOf(channelType, result) is not { } key)
        {
            return true;
        }

        var now = _timeProvider.GetUtcNow();

        if (Interlocked.Increment(ref _callCount) % CleanupEveryNCalls is 0)
        {
            TryRemoveExpiredMarks(now);
        }

        if (TryMarkAccepted(key, now))
        {
            return true;
        }

        _logger.LogInformation(
            "Repeated delivery of an inbound event dropped within the deduplication window. " +
            "ChannelType: {ChannelType}, Intent: {Intent}, UserFingerprint: {UserFingerprint}",
            channelType,
            key.Intent,
            LogMasking.Fingerprint(key.ChannelUserId));
        return false;
    }

    /// <summary>
    /// The key of an event subject to the window; null — the intent is never dropped.
    /// </summary>
    /// <param name="channelType">Type of the channel the event arrived through.</param>
    /// <param name="result">Result of processing the inbound event.</param>
    /// <returns>The key, or null.</returns>
    private static InboundEventKey? KeyOf(string channelType, ChannelInboundResult result) => result switch
    {
        ChannelAuthStartResult start =>
            InboundEventKey.Of(channelType, ChannelInboundIntents.AuthStart, start.Identity) with
            {
                TransactionId = start.TransactionId,
            },
        ChannelAuthStartByCodeResult startByCode =>
            InboundEventKey.Of(channelType, ChannelInboundIntents.AuthStartByCode, startByCode.Identity) with
            {
                Code = startByCode.Code,
            },
        ChannelAuthConfirmResult confirm =>
            InboundEventKey.Of(channelType, ChannelInboundIntents.AuthConfirm, confirm.Identity) with
            {
                TransactionId = confirm.TransactionId,
                InboundToken = confirm.InboundToken,
            },
        ChannelAuthDeclineResult decline =>
            InboundEventKey.Of(channelType, ChannelInboundIntents.AuthDecline, decline.Identity) with
            {
                TransactionId = decline.TransactionId,
                InboundToken = decline.InboundToken,
            },
        ChannelPhoneSharedResult phoneShared =>
            InboundEventKey.Of(channelType, ChannelInboundIntents.PhoneShared, phoneShared.Identity) with
            {
                PhoneNumber = phoneShared.PhoneNumber,
                OwnershipRejected = phoneShared.OwnershipRejected,
            },
        ChannelPhoneStepActionResult phoneStepAction =>
            InboundEventKey.Of(channelType, ChannelInboundIntents.PhoneStepAction, phoneStepAction.Identity) with
            {
                TransactionId = phoneStepAction.TransactionId,
                Action = phoneStepAction.Action,
            },

        // A menu item press, an unrelated event, a message naming no transaction and any further intent
        // are not deduplicated.
        _ => null,
    };

    /// <summary>
    /// Marks the key as accepted now unless it was accepted within the window — as one atomic operation
    /// per attempt: an absent mark is added, a mark outside the window is replaced only while it is
    /// still the one that was compared. A mark within the window is left untouched.
    /// </summary>
    /// <param name="key">Key of the event.</param>
    /// <param name="now">Current moment.</param>
    /// <returns>true — the event is accepted; false — it repeats one accepted within the window.</returns>
    private bool TryMarkAccepted(InboundEventKey key, DateTimeOffset now)
    {
        while (true)
        {
            if (_acceptedAt.TryAdd(key, now))
            {
                return true;
            }

            if (!_acceptedAt.TryGetValue(key, out var acceptedAt))
            {
                // The sweep removed the mark meanwhile: try to add it again.
                continue;
            }

            if (IsWithinWindow(now - acceptedAt))
            {
                return false;
            }

            if (_acceptedAt.TryUpdate(key, now, acceptedAt))
            {
                return true;
            }

            // Another event replaced the expired mark first: judge against the new one.
        }
    }

    /// <summary>
    /// Whether a mark of the given age lies within the window. The moment is read before the mark is
    /// compared, so of two events arriving at once the one that read the earlier moment may meet the mark
    /// of the other and see a slightly negative age; the sweep likewise meets marks written after its own
    /// moment. Such an age is within the window, or both events would be accepted. A mark ahead by the
    /// window or more — the clock went back — is outside: the event is accepted and its mark rewritten,
    /// and the sweep reclaims such a mark, so a step back of the clock leaks no mark and drops repeats for
    /// less than the window plus the step back.
    /// </summary>
    /// <param name="age">Time elapsed since the mark was written.</param>
    /// <returns>true — the mark is less than the window away from the current moment.</returns>
    private bool IsWithinWindow(TimeSpan age) => age > -_window && age < _window;

    /// <summary>
    /// Removes the marks outside the window, but runs no more than one sweep at a time.
    /// </summary>
    /// <param name="now">Current moment.</param>
    private void TryRemoveExpiredMarks(DateTimeOffset now)
    {
        if (Interlocked.CompareExchange(ref _cleanupInProgress, 1, 0) is not 0)
        {
            return;
        }

        try
        {
            foreach (var entry in _acceptedAt)
            {
                if (!IsWithinWindow(now - entry.Value))
                {
                    // Removed only while the slot still holds this very mark.
                    _acceptedAt.TryRemove(entry);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _cleanupInProgress, 0);
        }
    }

    /// <summary>
    /// Key of an inbound event: the common part — channel type, intent, tenant and user of the identity —
    /// and the fields that tell two different actions of the user apart; a field the intent does not
    /// carry stays at its default.
    /// </summary>
    /// <param name="ChannelType">Type of the channel the event arrived through.</param>
    /// <param name="Intent">Intent of the event (<see cref="ChannelInboundIntents"/>).</param>
    /// <param name="TenantId">Tenant of the identity; null — the implicit tenant.</param>
    /// <param name="ChannelUserId">User of the identity in the channel.</param>
    private readonly record struct InboundEventKey(
        string ChannelType,
        string Intent,
        string? TenantId,
        string ChannelUserId)
    {
        /// <summary>
        /// Transaction the event names — a start, an answer to the question, an action of the phone step.
        /// </summary>
        public TransactionId? TransactionId { get; init; }

        /// <summary>
        /// Code a start by code names.
        /// </summary>
        public string? Code { get; init; }

        /// <summary>
        /// Token of the interaction of an answer: a second press of the same button carries another one.
        /// </summary>
        public ChannelInboundToken? InboundToken { get; init; }

        /// <summary>
        /// Phone number a shared contact carries.
        /// </summary>
        public string? PhoneNumber { get; init; }

        /// <summary>
        /// Whether the shared contact failed the check that the number belongs to the sender.
        /// </summary>
        public bool OwnershipRejected { get; init; }

        /// <summary>
        /// Action a button of the phone step stands for.
        /// </summary>
        public PhoneStepAction? Action { get; init; }

        /// <summary>
        /// The common part of the key of an event.
        /// </summary>
        /// <param name="channelType">Type of the channel the event arrived through.</param>
        /// <param name="intent">Intent of the event.</param>
        /// <param name="identity">Identity of the user the event came from.</param>
        /// <returns>The key with the distinguishing fields at their defaults.</returns>
        public static InboundEventKey Of(string channelType, string intent, ChannelIdentitySnapshot identity) =>
            new(channelType, intent, identity.TenantId, identity.ChannelUserId);
    }
}
