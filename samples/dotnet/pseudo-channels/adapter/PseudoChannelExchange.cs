// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Sample.PseudoChannels.Adapter;

/// <summary>
/// In-memory <see cref="IPseudoChannelExchange"/>: the adapters write, the host reads. State lives
/// in the process and no longer than the lifetime the writing adapter states, so the sample runs as
/// a single instance — a deployment of several would need a shared store here.
/// </summary>
public sealed class PseudoChannelExchange : IPseudoChannelExchange
{
    /// <summary>
    /// Per-transaction state.
    /// </summary>
    private readonly ConcurrentDictionary<TransactionId, Entry> _entries = new();

    /// <summary>
    /// Clock the expiry is measured by.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the exchange.
    /// </summary>
    /// <param name="timeProvider">Clock the expiry is measured by.</param>
    public PseudoChannelExchange(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public bool TryGetPrompt(
        TransactionId transactionId,
        string channelUserId,
        [NotNullWhen(true)] out ConfirmationPromptContext? promptContext)
    {
        promptContext = TryGetLive(transactionId) is { Prompt: { } prompt } entry
            && IsSameUser(entry.PromptRecipient, channelUserId)
            ? prompt
            : null;

        return promptContext is not null;
    }

    /// <inheritdoc />
    public bool TryGetOutcome(
        TransactionId transactionId,
        string channelUserId,
        [NotNullWhen(true)] out TransactionOutcomeNotice? outcome)
    {
        outcome = TryGetLive(transactionId) is { Outcome: { } notice }
            && IsSameUser(notice.ChannelUserId, channelUserId)
            ? notice
            : null;

        return outcome is not null;
    }

    /// <summary>
    /// Remembers the question context of a transaction together with the user it was asked of.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="channelUserId">User in the channel the core addressed the question to.</param>
    /// <param name="promptContext">Context the core handed over with the question.</param>
    /// <param name="lifetime">How long the state is kept from now.</param>
    internal void StorePrompt(
        TransactionId transactionId,
        string channelUserId,
        ConfirmationPromptContext promptContext,
        TimeSpan lifetime)
        => Store(
            transactionId,
            lifetime,
            existing => existing with { Prompt = promptContext, PromptRecipient = channelUserId });

    /// <summary>
    /// Remembers the outcome of a transaction; a later notice replaces an earlier one. The notice
    /// names its own recipient.
    /// </summary>
    /// <param name="notice">Notice the core reported.</param>
    /// <param name="lifetime">How long the state is kept from now.</param>
    internal void StoreOutcome(TransactionOutcomeNotice notice, TimeSpan lifetime)
        => Store(notice.TransactionId, lifetime, existing => existing with { Outcome = notice });

    /// <summary>
    /// Writes the state of a transaction and drops whatever has expired. The sweep runs on writes
    /// only: a write happens once or twice per sign-in, which bounds both its cost and the memory.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="lifetime">How long the state is kept from now.</param>
    /// <param name="change">Change applied to the current (or an empty) state.</param>
    private void Store(TransactionId transactionId, TimeSpan lifetime, Func<Entry, Entry> change)
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(pair);
            }
        }

        var expiresAt = now + lifetime;
        _entries.AddOrUpdate(
            transactionId,
            _ => change(new Entry(null, null, null, expiresAt)),
            (_, existing) => change(existing with { ExpiresAt = expiresAt }));
    }

    /// <summary>
    /// Returns the state of a transaction unless it has expired.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <returns>The live state, or null.</returns>
    private Entry? TryGetLive(TransactionId transactionId)
        => _entries.TryGetValue(transactionId, out var entry) && entry.ExpiresAt > _timeProvider.GetUtcNow()
            ? entry
            : null;

    /// <summary>
    /// Tells whether the user a piece of state is addressed to is the one asking for it. Subject
    /// identifiers are opaque and case-sensitive, so the comparison is ordinal.
    /// </summary>
    /// <param name="recipient">User the state was addressed to.</param>
    /// <param name="channelUserId">User asking for it.</param>
    /// <returns>Whether they are the same user.</returns>
    private static bool IsSameUser(string? recipient, string channelUserId)
        => !string.IsNullOrEmpty(channelUserId) && string.Equals(recipient, channelUserId, StringComparison.Ordinal);

    /// <summary>
    /// State of one transaction.
    /// </summary>
    /// <param name="Prompt">Question context, once the core asked.</param>
    /// <param name="PromptRecipient">User in the channel the question was asked of.</param>
    /// <param name="Outcome">Latest outcome notice, once the core reported one.</param>
    /// <param name="ExpiresAt">Moment the state stops being served.</param>
    private sealed record Entry(
        ConfirmationPromptContext? Prompt,
        string? PromptRecipient,
        TransactionOutcomeNotice? Outcome,
        DateTimeOffset ExpiresAt);
}
