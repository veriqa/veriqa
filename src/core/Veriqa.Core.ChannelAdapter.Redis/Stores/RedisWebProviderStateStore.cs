// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using StackExchange.Redis;

using Veriqa.Core.ChannelAdapter.WebProviders.State;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Redis.Stores;

/// <summary>
/// Redis implementation of the state store of the pages of the web provider channels. Behaves like the
/// in-process default in everything but distribution: a browser that came back from the provider on one
/// replica sees its question, its feed and its outcome on another, and a message of the core finds the
/// live session of an account whichever replica wrote it.
/// </summary>
/// <remarks>
/// <para>
/// A session is a string under the hash of its mark — the mark a browser holds is never a key. An
/// account is a key under the hash of its channel and identifier, holding no value of its own: its TTL
/// is the end of the latest session of the account, and "the key exists" means "the account has a live
/// session". The feed of the account is a list under the same hash; it is written only while the account
/// key exists, and it carries the TTL of that key, so the feed ends with the latest session, as in the
/// process. A question is a hash under its channel and transaction, an outcome a string; both end at the
/// moment the caller states.
/// </para>
/// <para>
/// Each step that reads before it writes — keeping the latest end of an account, adding to a feed only
/// while the account is live, marking a question answered only while it exists (the shared
/// <see cref="RedisChannelStoreScripts.MarkFieldOnce"/>) — is one Lua script, so
/// replicas racing on one account or one transaction resolve the way one lock resolves them in the
/// process.
/// </para>
/// </remarks>
internal sealed class RedisWebProviderStateStore : IWebProviderStateStore
{
    /// <summary>
    /// Key segment of the sessions of the pages.
    /// </summary>
    private const string SessionSegment = "session:";

    /// <summary>
    /// Key segment of the accounts with a live session.
    /// </summary>
    private const string AccountSegment = "account:";

    /// <summary>
    /// Key segment of the feeds of the accounts.
    /// </summary>
    private const string FeedSegment = "feed:";

    /// <summary>
    /// Key segment of the questions.
    /// </summary>
    private const string PromptSegment = "prompt:";

    /// <summary>
    /// Key segment of the outcomes.
    /// </summary>
    private const string OutcomeSegment = "outcome:";

    /// <summary>
    /// Separator of the parts of a key, and of the parts hashed into an account.
    /// </summary>
    private const char KeyPartSeparator = ':';

    /// <summary>
    /// Hash field holding the account a question is put to.
    /// </summary>
    private const string ChannelUserIdField = "ChannelUserId";

    /// <summary>
    /// Hash field holding the text of a question.
    /// </summary>
    private const string TextField = "Text";

    /// <summary>
    /// Hash field marking a question answered; absent — not answered.
    /// </summary>
    private const string AnsweredField = "Answered";

    /// <summary>
    /// Stored value of <see cref="AnsweredField"/>, and the value of an account key.
    /// </summary>
    private const string FlagSetValue = "1";

    /// <summary>
    /// Entity description used in lifetime errors.
    /// </summary>
    private const string EntityDescription = "State of a web provider page";

    /// <summary>
    /// Lua script storing a session and keeping the latest end of its account.
    /// KEYS[1] — session key. KEYS[2] — account key. KEYS[3] — feed key of the account.
    /// ARGV[1] — the session (JSON). ARGV[2] — its deadline in Unix milliseconds (alive when checked in C#).
    /// ARGV[3] — the value of an account key.
    /// The deadline is absolute, so a script delayed on its way does not keep the session past it; a
    /// session that died on the way is not stored. An account whose latest session ends later keeps its
    /// end; an account that gets a later end carries its feed along to it.
    /// </summary>
    private const string SaveSessionLuaScript = @"
local deadline = tonumber(ARGV[2])
local time = redis.call('TIME')
local ttl = deadline - (tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000))
if ttl <= 0 then
    return 0
end
redis.call('SET', KEYS[1], ARGV[1], 'PXAT', deadline)
if redis.call('PTTL', KEYS[2]) < ttl then
    redis.call('SET', KEYS[2], ARGV[3], 'PXAT', deadline)
    if redis.call('EXISTS', KEYS[3]) == 1 then
        redis.call('PEXPIREAT', KEYS[3], deadline)
    end
end
return 1";

    /// <summary>
    /// Lua script adding a message to the feed of an account while the account has a live session.
    /// KEYS[1] — account key. KEYS[2] — feed key. ARGV[1] — the message (JSON).
    /// Returns: 1 — added; 0 — the account has no live session.
    /// </summary>
    private const string AppendMessageLuaScript = @"
local ttl = redis.call('PTTL', KEYS[1])
if ttl <= 0 then
    return 0
end
redis.call('RPUSH', KEYS[2], ARGV[1])
redis.call('PEXPIRE', KEYS[2], ttl)
return 1";

    /// <summary>
    /// Store options (key prefix).
    /// </summary>
    private readonly RedisChannelStoreOptions _options;

    /// <summary>
    /// Logical Redis database handle.
    /// </summary>
    private readonly IDatabase _db;

    /// <summary>
    /// Clock a record is checked to be alive against before it is written.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the Redis state store of the web provider pages.
    /// </summary>
    /// <param name="connectionMultiplexer">Redis connection multiplexer.</param>
    /// <param name="options">Channel store options.</param>
    /// <param name="timeProvider">Time provider.</param>
    public RedisWebProviderStateStore(
        IConnectionMultiplexer connectionMultiplexer,
        IOptions<RedisChannelStoreOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _db = connectionMultiplexer.GetDatabase();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task SaveSessionAsync(string mark, WebProviderPageSession session, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mark);
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        var deadline = RedisRecordLifetime.DeadlineOf(session.ExpiresAt, _timeProvider.GetUtcNow(), EntityDescription);
        var account = AccountHash(session.ChannelType, session.ChannelUserId);

        await _db.ScriptEvaluateAsync(
            SaveSessionLuaScript,
            new RedisKey[] { SessionKey(mark), Key(AccountSegment, account), Key(FeedSegment, account) },
            new RedisValue[]
            {
                JsonSerializer.Serialize(session, RedisChannelStoreSerialization.Options),
                deadline.ToUnixTimeMilliseconds(),
                FlagSetValue
            });
    }

    /// <inheritdoc />
    public async Task<WebProviderPageSession?> FindSessionAsync(string mark, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mark);
        cancellationToken.ThrowIfCancellationRequested();

        var value = await _db.StringGetAsync(SessionKey(mark));

        return value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<WebProviderPageSession>(value.ToString(), RedisChannelStoreSerialization.Options);
    }

    /// <inheritdoc />
    public async Task SavePromptAsync(
        string channelType,
        TransactionId transactionId,
        WebProviderPrompt prompt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(prompt);
        cancellationToken.ThrowIfCancellationRequested();

        var key = PromptKey(channelType, transactionId);
        var deadline = RedisRecordLifetime.DeadlineOf(expiresAt, _timeProvider.GetUtcNow(), EntityDescription);

        var entries = new List<HashEntry>(3)
        {
            new(ChannelUserIdField, prompt.ChannelUserId),
            new(TextField, prompt.Text)
        };

        if (prompt.Answered)
        {
            entries.Add(new HashEntry(AnsweredField, FlagSetValue));
        }

        // DEL + HSET + PEXPIREAT in one MULTI/EXEC: a question replaces the earlier one wholesale, its
        // answered mark included; the deadline is absolute, so a delayed command does not extend it.
        var transaction = _db.CreateTransaction();
        var deleteTask = transaction.KeyDeleteAsync(key);
        var setTask = transaction.HashSetAsync(key, entries.ToArray());
        var expireTask = transaction.KeyExpireAsync(key, deadline.UtcDateTime);

        await transaction.ExecuteAsync();
        await Task.WhenAll(deleteTask, setTask, expireTask);
    }

    /// <inheritdoc />
    public async Task<WebProviderPrompt?> FindPromptAsync(
        string channelType,
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        cancellationToken.ThrowIfCancellationRequested();

        var entries = await _db.HashGetAllAsync(PromptKey(channelType, transactionId));
        if (entries.Length == 0)
        {
            return null;
        }

        var fields = entries.ToDictionary(
            entry => entry.Name.ToString(),
            entry => entry.Value,
            StringComparer.Ordinal);

        // A record missing a mandatory field is unusable and is reported as "no question".
        if (!fields.TryGetValue(ChannelUserIdField, out var channelUserId) || !fields.TryGetValue(TextField, out var text))
        {
            return null;
        }

        return new WebProviderPrompt(
            channelUserId.ToString(),
            text.ToString(),
            Answered: fields.GetValueOrDefault(AnsweredField) == FlagSetValue);
    }

    /// <inheritdoc />
    public async Task MarkPromptAnsweredAsync(
        string channelType,
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        cancellationToken.ThrowIfCancellationRequested();

        // Marks the question only while it exists; the question keeps its TTL.
        await _db.ScriptEvaluateAsync(
            RedisChannelStoreScripts.MarkFieldOnce,
            new RedisKey[] { PromptKey(channelType, transactionId) },
            new RedisValue[] { AnsweredField, FlagSetValue });
    }

    /// <inheritdoc />
    public async Task<bool> TryAppendMessageAsync(
        string channelType,
        string channelUserId,
        WebProviderFeedMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentException.ThrowIfNullOrEmpty(channelUserId);
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var account = AccountHash(channelType, channelUserId);

        var result = await _db.ScriptEvaluateAsync(
            AppendMessageLuaScript,
            new RedisKey[] { Key(AccountSegment, account), Key(FeedSegment, account) },
            new RedisValue[] { JsonSerializer.Serialize(message, RedisChannelStoreSerialization.Options) });

        return (long)result == 1L;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebProviderFeedMessage>> ListMessagesAsync(
        string channelType,
        string channelUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentException.ThrowIfNullOrEmpty(channelUserId);
        cancellationToken.ThrowIfCancellationRequested();

        var values = await _db.ListRangeAsync(Key(FeedSegment, AccountHash(channelType, channelUserId)));

        var messages = new List<WebProviderFeedMessage>(values.Length);
        foreach (var value in values)
        {
            if (!value.IsNullOrEmpty
                && JsonSerializer.Deserialize<WebProviderFeedMessage>(value.ToString(), RedisChannelStoreSerialization.Options) is { } message)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    /// <inheritdoc />
    public async Task SaveOutcomeAsync(
        string channelType,
        TransactionId transactionId,
        string statusText,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentNullException.ThrowIfNull(statusText);
        cancellationToken.ThrowIfCancellationRequested();

        var key = OutcomeKey(channelType, transactionId);
        var deadline = RedisRecordLifetime.DeadlineOf(expiresAt, _timeProvider.GetUtcNow(), EntityDescription);

        // SET + PEXPIREAT in one MULTI/EXEC: a later notice of the transaction replaces the earlier one,
        // and the absolute deadline is not extended by a delayed command.
        var transaction = _db.CreateTransaction();
        var setTask = transaction.StringSetAsync(key, statusText);
        var expireTask = transaction.KeyExpireAsync(key, deadline.UtcDateTime);

        await transaction.ExecuteAsync();
        await Task.WhenAll(setTask, expireTask);
    }

    /// <inheritdoc />
    public async Task<string?> FindOutcomeAsync(
        string channelType,
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        cancellationToken.ThrowIfCancellationRequested();

        var value = await _db.StringGetAsync(OutcomeKey(channelType, transactionId));

        return value.IsNull ? null : value.ToString();
    }

    /// <summary>
    /// Hash of an account — its channel and its identifier — so the identifier never appears in a key.
    /// </summary>
    /// <param name="channelType">Channel of the account.</param>
    /// <param name="channelUserId">The account.</param>
    /// <returns>Lower-case hexadecimal SHA-256.</returns>
    private static string AccountHash(string channelType, string channelUserId) =>
        Hash(string.Concat(channelType, KeyPartSeparator.ToString(), channelUserId));

    /// <summary>
    /// SHA-256 of a value, in lower-case hexadecimal.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The hash.</returns>
    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// Builds a key of this store.
    /// </summary>
    /// <param name="segment">Segment of the kind of record.</param>
    /// <param name="suffix">What identifies the record.</param>
    /// <returns>Redis key.</returns>
    private string Key(string segment, string suffix) =>
        string.Concat(_options.WebProviderStateKeyPrefix, segment, suffix);

    /// <summary>
    /// Key of a session: the hash of its mark.
    /// </summary>
    /// <param name="mark">The mark.</param>
    /// <returns>Redis key.</returns>
    private string SessionKey(string mark) => Key(SessionSegment, Hash(mark));

    /// <summary>
    /// Key of the question of a transaction.
    /// </summary>
    /// <param name="channelType">Channel of the transaction.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <returns>Redis key.</returns>
    private string PromptKey(string channelType, TransactionId transactionId) =>
        Key(PromptSegment, string.Concat(channelType, KeyPartSeparator.ToString(), transactionId.ToString()));

    /// <summary>
    /// Key of the outcome of a transaction.
    /// </summary>
    /// <param name="channelType">Channel of the transaction.</param>
    /// <param name="transactionId">The transaction.</param>
    /// <returns>Redis key.</returns>
    private string OutcomeKey(string channelType, TransactionId transactionId) =>
        Key(OutcomeSegment, string.Concat(channelType, KeyPartSeparator.ToString(), transactionId.ToString()));
}
