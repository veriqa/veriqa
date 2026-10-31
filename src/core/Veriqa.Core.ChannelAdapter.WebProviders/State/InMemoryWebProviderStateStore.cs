// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.WebProviders.State;

/// <summary>
/// In-process implementation of <see cref="IWebProviderStateStore"/>: the default of a single instance.
/// A deployment of several replicas takes the Redis channel stores, which replace it. Every operation
/// runs under one lock — each touches a record or two — and a read never returns a record past its
/// end; records that ended are swept out every <see cref="SweepEveryNWrites"/> writes, so memory does
/// not grow with sign-ins nobody returns to.
/// </summary>
internal sealed class InMemoryWebProviderStateStore : IWebProviderStateStore
{
    /// <summary>
    /// Number of writes between two sweeps of the records that ended.
    /// </summary>
    private const int SweepEveryNWrites = 50;

    /// <summary>
    /// Guard of every collection below.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Sessions of the pages, by mark.
    /// </summary>
    private readonly Dictionary<string, WebProviderPageSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// End of the latest session of every account, by channel and account.
    /// </summary>
    private readonly Dictionary<(string ChannelType, string ChannelUserId), DateTimeOffset> _accounts = new();

    /// <summary>
    /// Feeds of the accounts, by channel and account; a feed ends with the latest session of its account.
    /// </summary>
    private readonly Dictionary<(string ChannelType, string ChannelUserId), List<WebProviderFeedMessage>> _feeds = new();

    /// <summary>
    /// Questions with their ends, by channel and transaction.
    /// </summary>
    private readonly Dictionary<(string ChannelType, TransactionId TransactionId), (WebProviderPrompt Prompt, DateTimeOffset ExpiresAt)> _prompts = new();

    /// <summary>
    /// Outcomes with their ends, by channel and transaction.
    /// </summary>
    private readonly Dictionary<(string ChannelType, TransactionId TransactionId), (string StatusText, DateTimeOffset ExpiresAt)> _outcomes = new();

    /// <summary>
    /// Clock the ends of the records are judged against.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Writes since the last sweep.
    /// </summary>
    private int _writesSinceSweep;

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="timeProvider">Clock of the host.</param>
    public InMemoryWebProviderStateStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public Task SaveSessionAsync(string mark, WebProviderPageSession session, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mark);
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            _sessions[mark] = session;

            // The account stays reachable until its latest session ends: an earlier session ending
            // sooner does not cut a later one short.
            var account = (session.ChannelType, session.ChannelUserId);
            if (!_accounts.TryGetValue(account, out var end) || end < session.ExpiresAt)
            {
                _accounts[account] = session.ExpiresAt;
            }

            CountWrite();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WebProviderPageSession?> FindSessionAsync(string mark, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mark);

        lock (_gate)
        {
            return Task.FromResult(
                _sessions.TryGetValue(mark, out var session) && IsLive(session.ExpiresAt) ? session : null);
        }
    }

    /// <inheritdoc />
    public Task SavePromptAsync(
        string channelType,
        TransactionId transactionId,
        WebProviderPrompt prompt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(prompt);

        lock (_gate)
        {
            _prompts[(channelType, transactionId)] = (prompt, expiresAt);
            CountWrite();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WebProviderPrompt?> FindPromptAsync(
        string channelType,
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);

        lock (_gate)
        {
            return Task.FromResult(
                _prompts.TryGetValue((channelType, transactionId), out var entry) && IsLive(entry.ExpiresAt)
                    ? entry.Prompt
                    : null);
        }
    }

    /// <inheritdoc />
    public Task MarkPromptAnsweredAsync(
        string channelType,
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);

        lock (_gate)
        {
            var key = (channelType, transactionId);
            if (_prompts.TryGetValue(key, out var entry) && IsLive(entry.ExpiresAt))
            {
                _prompts[key] = (entry.Prompt with { Answered = true }, entry.ExpiresAt);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> TryAppendMessageAsync(
        string channelType,
        string channelUserId,
        WebProviderFeedMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentException.ThrowIfNullOrEmpty(channelUserId);
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            var account = (channelType, channelUserId);
            if (!_accounts.TryGetValue(account, out var end) || !IsLive(end))
            {
                return Task.FromResult(false);
            }

            if (!_feeds.TryGetValue(account, out var feed))
            {
                feed = [];
                _feeds[account] = feed;
            }

            feed.Add(message);
            CountWrite();

            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WebProviderFeedMessage>> ListMessagesAsync(
        string channelType,
        string channelUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentException.ThrowIfNullOrEmpty(channelUserId);

        lock (_gate)
        {
            var account = (channelType, channelUserId);
            IReadOnlyList<WebProviderFeedMessage> messages =
                _accounts.TryGetValue(account, out var end) && IsLive(end) && _feeds.TryGetValue(account, out var feed)
                    ? feed.ToArray()
                    : [];

            return Task.FromResult(messages);
        }
    }

    /// <inheritdoc />
    public Task SaveOutcomeAsync(
        string channelType,
        TransactionId transactionId,
        string statusText,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(statusText);

        lock (_gate)
        {
            _outcomes[(channelType, transactionId)] = (statusText, expiresAt);
            CountWrite();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> FindOutcomeAsync(
        string channelType,
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);

        lock (_gate)
        {
            return Task.FromResult(
                _outcomes.TryGetValue((channelType, transactionId), out var entry) && IsLive(entry.ExpiresAt)
                    ? entry.StatusText
                    : null);
        }
    }

    /// <summary>
    /// Tells whether a record that ends at the given moment is still live.
    /// </summary>
    /// <param name="expiresAt">End of the record.</param>
    /// <returns><c>true</c> — the record is live.</returns>
    private bool IsLive(DateTimeOffset expiresAt) => _timeProvider.GetUtcNow() < expiresAt;

    /// <summary>
    /// Counts a write and sweeps the records that ended every <see cref="SweepEveryNWrites"/> writes.
    /// Called under the lock.
    /// </summary>
    private void CountWrite()
    {
        if (++_writesSinceSweep < SweepEveryNWrites)
        {
            return;
        }

        _writesSinceSweep = 0;

        foreach (var (mark, session) in _sessions)
        {
            if (!IsLive(session.ExpiresAt))
            {
                _sessions.Remove(mark);
            }
        }

        // A feed goes with the account whose latest session ended.
        foreach (var (account, end) in _accounts)
        {
            if (!IsLive(end))
            {
                _accounts.Remove(account);
                _feeds.Remove(account);
            }
        }

        foreach (var (key, entry) in _prompts)
        {
            if (!IsLive(entry.ExpiresAt))
            {
                _prompts.Remove(key);
            }
        }

        foreach (var (key, entry) in _outcomes)
        {
            if (!IsLive(entry.ExpiresAt))
            {
                _outcomes.Remove(key);
            }
        }
    }
}
