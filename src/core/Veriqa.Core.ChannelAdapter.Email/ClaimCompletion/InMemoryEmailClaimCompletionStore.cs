// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;

/// <summary>
/// In-process store of the email completion — the default for a single instance; several replicas use
/// the Redis one. Every operation runs under one lock, which is what makes each of them atomic: the
/// records are small and an operation touches a handful of entries, so a finer scheme would buy nothing.
/// Expired entries are swept every few writes rather than by a timer.
/// </summary>
internal sealed class InMemoryEmailClaimCompletionStore : IEmailClaimCompletionStore
{
    /// <summary>
    /// Sweep the expired entries every this many writes.
    /// </summary>
    private const int CleanupEveryNWrites = 50;

    /// <summary>
    /// Guard of every entry below.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Records of the transactions, with the moment each transaction expires.
    /// </summary>
    private readonly Dictionary<string, (EmailClaimCompletionRecord Record, DateTimeOffset ExpiresAt)> _records =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Index "fingerprint of a link token → transaction", with the moment the link expires.
    /// </summary>
    private readonly Dictionary<string, (string TransactionId, DateTimeOffset ExpiresAt)> _links =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Mail counters, with the moment the window of each ends.
    /// </summary>
    private readonly Dictionary<string, (int Count, DateTimeOffset WindowEndsAt)> _counters =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Clock the expiries and the windows are judged against.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Writes since the last sweep.
    /// </summary>
    private int _writesSinceCleanup;

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="timeProvider">Time provider.</param>
    public InMemoryEmailClaimCompletionStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public Task<EmailClaimCompletionRecord?> GetAsync(string transactionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(LiveRecord(transactionId, _timeProvider.GetUtcNow()));
        }
    }

    /// <inheritdoc />
    public Task<int?> TryReserveMailAsync(IReadOnlyList<EmailClaimMailCounter> counters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(counters);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();

            // Every counter is judged before any is counted: a refusal by one leaves all of them as they were.
            for (var index = 0; index < counters.Count; index++)
            {
                if (CountOf(counters[index].Key, now) >= counters[index].Limit)
                {
                    return Task.FromResult<int?>(index);
                }
            }

            foreach (var counter in counters)
            {
                // The window is fixed by the first mail the counter counts, and is not moved by the next ones.
                _counters[counter.Key] = _counters.TryGetValue(counter.Key, out var current) && now < current.WindowEndsAt
                    ? (current.Count + 1, current.WindowEndsAt)
                    : (1, now.Add(counter.Window));
            }

            CountWrite(now);

            return Task.FromResult<int?>(null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<int>> ReadMailCountsAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            return Task.FromResult<IReadOnlyList<int>>([.. keys.Select(key => CountOf(key, now))]);
        }
    }

    /// <inheritdoc />
    public Task SetCodeAsync(
        string transactionId,
        EmailClaimCode code,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);

        return UpdateAsync(transactionId, recordExpiresAt, record => record with { Code = code }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TrySpendCodeAttemptAsync(
        string transactionId,
        string codeHash,
        int attemptsSeen,
        bool matched,
        CancellationToken cancellationToken) =>
        SpendAttemptAsync(
            transactionId,
            codeHash,
            attemptsSeen,
            matched,
            record => record.Code,
            (record, attemptsLeft) => record with { Code = record.Code! with { AttemptsLeft = attemptsLeft } },
            cancellationToken);

    /// <inheritdoc />
    public Task SetSignInCodeAsync(
        string transactionId,
        EmailSignInCode code,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);

        return UpdateAsync(transactionId, recordExpiresAt, record => record with { SignInCode = code }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TrySpendSignInCodeAttemptAsync(
        string transactionId,
        string codeHash,
        int attemptsSeen,
        bool matched,
        CancellationToken cancellationToken) =>
        SpendAttemptAsync(
            transactionId,
            codeHash,
            attemptsSeen,
            matched,
            record => record.SignInCode,
            (record, attemptsLeft) => record with { SignInCode = record.SignInCode! with { AttemptsLeft = attemptsLeft } },
            cancellationToken);

    /// <inheritdoc />
    public Task SetLinkAsync(
        string transactionId,
        EmailClaimLink link,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // The index of the token before it is left to the sweep: that token no longer matches the record.
            _links[link.TokenHash] = (transactionId, link.ExpiresAt);
            Update(transactionId, recordExpiresAt, record => record with { Link = link });
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<EmailClaimLinkUse?> FindLiveLinkAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<EmailClaimLinkUse?>(
                LiveLinkOf(tokenHash, now) is { } found
                    ? new EmailClaimLinkUse(found.TransactionId, found.Link.Address)
                    : null);
        }
    }

    /// <inheritdoc />
    public Task<EmailClaimLinkUse?> TryUseLinkAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (LiveLinkOf(tokenHash, now) is not { } found)
            {
                return Task.FromResult<EmailClaimLinkUse?>(null);
            }

            var (record, expiresAt) = _records[found.TransactionId];
            _records[found.TransactionId] = (record with { Link = found.Link with { Used = true } }, expiresAt);

            return Task.FromResult<EmailClaimLinkUse?>(new EmailClaimLinkUse(found.TransactionId, found.Link.Address));
        }
    }

    /// <inheritdoc />
    public Task SetSelectedMethodAsync(
        string transactionId,
        EmailCompletionMethod method,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken) =>
        UpdateAsync(transactionId, recordExpiresAt, record => record with { SelectedMethod = method }, cancellationToken);

    /// <inheritdoc />
    public Task SetPushTokenAsync(
        string transactionId,
        EmailClaimPushToken token,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        return UpdateAsync(transactionId, recordExpiresAt, record => record with { Push = token }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetHeldAddressAsync(
        string transactionId,
        EmailClaimHeldAddress? held,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken) =>
        UpdateAsync(transactionId, recordExpiresAt, record => record with { Held = held }, cancellationToken);

    /// <summary>
    /// Replaces one part of the record of a transaction under the lock.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="recordExpiresAt">Moment the transaction expires.</param>
    /// <param name="change">The change of the record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    private Task UpdateAsync(
        string transactionId,
        DateTimeOffset recordExpiresAt,
        Func<EmailClaimCompletionRecord, EmailClaimCompletionRecord> change,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            Update(transactionId, recordExpiresAt, change);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Spends one attempt of a code of the record of a transaction by a compare-and-swap on what was read: only
    /// while the code still has the fingerprint and the attempts it was read with. A match spends every attempt left.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="codeHash">Fingerprint of the code that was read.</param>
    /// <param name="attemptsSeen">Attempts the code had when it was read.</param>
    /// <param name="matched">Whether the entry matched.</param>
    /// <param name="codeOf">The code of the record the attempt is spent on.</param>
    /// <param name="withAttemptsLeft">The record with that code left with the given attempts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the attempt was spent by this call; <c>false</c> — the code changed meanwhile.</returns>
    private Task<bool> SpendAttemptAsync(
        string transactionId,
        string codeHash,
        int attemptsSeen,
        bool matched,
        Func<EmailClaimCompletionRecord, IEmailStoredCode?> codeOf,
        Func<EmailClaimCompletionRecord, int, EmailClaimCompletionRecord> withAttemptsLeft,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (LiveRecord(transactionId, _timeProvider.GetUtcNow()) is not { } record
                || codeOf(record) is not { } code
                || !string.Equals(code.Hash, codeHash, StringComparison.Ordinal)
                || code.AttemptsLeft != attemptsSeen)
            {
                return Task.FromResult(false);
            }

            var spent = withAttemptsLeft(record, matched ? 0 : attemptsSeen - 1);
            _records[transactionId] = (spent, _records[transactionId].ExpiresAt);

            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Replaces one part of the record of a transaction; the caller holds the lock.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="recordExpiresAt">Moment the transaction expires.</param>
    /// <param name="change">The change of the record.</param>
    private void Update(
        string transactionId,
        DateTimeOffset recordExpiresAt,
        Func<EmailClaimCompletionRecord, EmailClaimCompletionRecord> change)
    {
        var now = _timeProvider.GetUtcNow();
        var record = LiveRecord(transactionId, now) ?? EmailClaimCompletionRecord.Empty;

        _records[transactionId] = (change(record), recordExpiresAt);
        CountWrite(now);
    }

    /// <summary>
    /// The record of a transaction that has not expired; the caller holds the lock.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="now">Current moment.</param>
    /// <returns>The record, or null.</returns>
    private EmailClaimCompletionRecord? LiveRecord(string transactionId, DateTimeOffset now) =>
        _records.TryGetValue(transactionId, out var entry) && now < entry.ExpiresAt ? entry.Record : null;

    /// <summary>
    /// The live link a token fingerprint belongs to: the token is the CURRENT link of its transaction,
    /// unused and unexpired. The caller holds the lock.
    /// </summary>
    /// <param name="tokenHash">Fingerprint of the token.</param>
    /// <param name="now">Current moment.</param>
    /// <returns>The transaction and the link, or null.</returns>
    private (string TransactionId, EmailClaimLink Link)? LiveLinkOf(string tokenHash, DateTimeOffset now)
    {
        if (!_links.TryGetValue(tokenHash, out var indexed)
            || LiveRecord(indexed.TransactionId, now) is not { Link: { } link }
            || !string.Equals(link.TokenHash, tokenHash, StringComparison.Ordinal)
            || !link.IsLive(now))
        {
            return null;
        }

        return (indexed.TransactionId, link);
    }

    /// <summary>
    /// Mails a counter has counted within its current window; the caller holds the lock.
    /// </summary>
    /// <param name="key">Key of the counter.</param>
    /// <param name="now">Current moment.</param>
    /// <returns>The count; zero once the window has ended.</returns>
    private int CountOf(string key, DateTimeOffset now) =>
        _counters.TryGetValue(key, out var counter) && now < counter.WindowEndsAt ? counter.Count : 0;

    /// <summary>
    /// Counts a write and sweeps the expired entries every few of them; the caller holds the lock.
    /// </summary>
    /// <param name="now">Current moment.</param>
    private void CountWrite(DateTimeOffset now)
    {
        if (++_writesSinceCleanup < CleanupEveryNWrites)
        {
            return;
        }

        _writesSinceCleanup = 0;

        foreach (var key in _records.Where(entry => now >= entry.Value.ExpiresAt).Select(entry => entry.Key).ToList())
        {
            _records.Remove(key);
        }

        foreach (var key in _links.Where(entry => now >= entry.Value.ExpiresAt).Select(entry => entry.Key).ToList())
        {
            _links.Remove(key);
        }

        foreach (var key in _counters.Where(entry => now >= entry.Value.WindowEndsAt).Select(entry => entry.Key).ToList())
        {
            _counters.Remove(key);
        }
    }
}
