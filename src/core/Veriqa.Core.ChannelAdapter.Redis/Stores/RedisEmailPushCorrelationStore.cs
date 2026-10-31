// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using StackExchange.Redis;

using Veriqa.Core.ChannelAdapter.Email.Services;

namespace Veriqa.Core.ChannelAdapter.Redis.Stores;

/// <summary>
/// Redis implementation of the Email Push-mode correlation store (SPEC-016 §5). An inbound email
/// answered by one replica resolves against the correlation minted by another, and the deduplication
/// of provider redelivery becomes shared instead of per-instance.
/// </summary>
/// <remarks>
/// <para>
/// A correlation is one Redis hash whose TTL is the correlation's own <c>ExpiresAt</c>, plus an
/// index key "transaction → live token" carrying the same TTL. The two are written by a single Lua
/// script and removed together, so "the index key exists" means "the transaction has a live token" —
/// the distributed equivalent of the in-process index the default store keeps.
/// </para>
/// <para>
/// Consuming a correlation deletes its hash, exactly as the in-process store removes the dictionary
/// entry; that is what makes <c>TryConsumeAsync</c> atomic without a compare-and-swap on a field.
/// Two consequences, both matching the in-process behaviour as observed by callers:
/// <see cref="GetAsync"/> returns null for an expired correlation, and
/// <see cref="EmailPushCorrelation.ConsumedAt"/> is never populated — a consumed correlation is gone
/// rather than flagged.
/// </para>
/// </remarks>
internal sealed class RedisEmailPushCorrelationStore : IEmailPushCorrelationStore
{
    /// <summary>
    /// Hash field holding the transaction identifier.
    /// </summary>
    private const string TransactionIdField = "TransactionId";

    /// <summary>
    /// Hash field holding the correlation expiry moment.
    /// </summary>
    private const string ExpiresAtField = "ExpiresAt";

    /// <summary>
    /// Hash field marking a correlation of the claims completion; absent — a correlation of the sign-in.
    /// </summary>
    private const string ForClaimCompletionField = "ForClaimCompletion";

    /// <summary>
    /// Stored value of <see cref="ForClaimCompletionField"/>.
    /// </summary>
    private const string FlagSetValue = "1";

    /// <summary>
    /// Key segment of the "transaction → live token" index.
    /// </summary>
    private const string TransactionIndexSegment = "idx:tx:";

    /// <summary>
    /// Entity description used in lifetime errors.
    /// </summary>
    private const string EntityDescription = "Email push correlation";

    /// <summary>
    /// Lua script registering a correlation token for its transaction idempotently.
    /// A script runs as one indivisible step, so the read of the index and the write that follows it
    /// form a genuine compare-and-swap: out of concurrent renders of one sign-in page exactly one
    /// token is stored and every caller is handed that same token back.
    /// KEYS[1] — "transaction → live token" index key.
    /// KEYS[2] — candidate correlation hash key.
    /// ARGV[1] — the deadline in Unix milliseconds (alive when checked in C# before the call); absolute,
    /// so a script delayed on its way does not keep the token past it.
    /// ARGV[2] — candidate token.
    /// ARGV[3..] — hash field/value pairs of the candidate record.
    /// Returns: the token the caller must use.
    /// </summary>
    /// <remarks>
    /// The reuse branch deliberately touches nothing: re-rendering the page must not push the
    /// expiry of an already issued token forward, or the token would live for as long as the page
    /// keeps being reopened. The index carries the same deadline as the record, so both fall away
    /// together and a stale index cannot outlive the token it points at.
    /// </remarks>
    private const string StoreOrReuseLuaScript = @"
local existing = redis.call('GET', KEYS[1])
if existing then
    return existing
end
local deadline = ARGV[1]
local token = ARGV[2]
redis.call('DEL', KEYS[2])
for i = 3, #ARGV, 2 do
    redis.call('HSET', KEYS[2], ARGV[i], ARGV[i + 1])
end
redis.call('PEXPIREAT', KEYS[2], deadline)
redis.call('SET', KEYS[1], token)
redis.call('PEXPIREAT', KEYS[1], deadline)
return token";

    /// <summary>
    /// Lua script consuming a correlation token exactly once.
    /// KEYS[1] — correlation hash key.
    /// KEYS[2] — "transaction → live token" index key.
    /// ARGV[1] — the token being consumed.
    /// Returns: 1 — consumed by this call; 0 — not found, expired or already consumed.
    /// </summary>
    /// <remarks>
    /// DEL decides the winner: Redis reports "the key existed" to exactly one concurrent caller. The
    /// index is dropped only when it still points at this very token, so a token issued for the same
    /// transaction by a later render is not wiped out along with the consumed one.
    /// </remarks>
    private const string ConsumeLuaScript = @"
if redis.call('DEL', KEYS[1]) == 0 then
    return 0
end
if redis.call('GET', KEYS[2]) == ARGV[1] then
    redis.call('DEL', KEYS[2])
end
return 1";

    /// <summary>
    /// Store options (key prefixes, deduplication retention).
    /// </summary>
    private readonly RedisChannelStoreOptions _options;

    /// <summary>
    /// Logical Redis database handle.
    /// </summary>
    private readonly IDatabase _db;

    /// <summary>
    /// Clock a record is checked to be alive against before it is written, and its stamps are taken from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the Redis push correlation store.
    /// </summary>
    /// <param name="connectionMultiplexer">Redis connection multiplexer.</param>
    /// <param name="options">Channel store options.</param>
    /// <param name="timeProvider">Time provider.</param>
    public RedisEmailPushCorrelationStore(
        IConnectionMultiplexer connectionMultiplexer,
        IOptions<RedisChannelStoreOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _db = connectionMultiplexer.GetDatabase();
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task StoreAsync(string token, EmailPushCorrelation correlation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        cancellationToken.ThrowIfCancellationRequested();

        var key = GetCorrelationKey(token);
        var deadline = RedisRecordLifetime.DeadlineOf(correlation.ExpiresAt, _timeProvider.GetUtcNow(), EntityDescription);

        // DEL + HSET + PEXPIREAT in one MULTI/EXEC: a write replaces the record wholesale, as the
        // in-process store assigns over its dictionary slot. The index is left alone on purpose —
        // the in-process StoreAsync does not touch it either; only StoreOrReuseAsync owns it.
        var transaction = _db.CreateTransaction();
        var deleteTask = transaction.KeyDeleteAsync(key);
        var setTask = transaction.HashSetAsync(key, BuildEntries(correlation));
        var expireTask = transaction.KeyExpireAsync(key, deadline.UtcDateTime);

        await transaction.ExecuteAsync();
        await Task.WhenAll(deleteTask, setTask, expireTask);
    }

    /// <inheritdoc />
    public async Task<string> StoreOrReuseAsync(string token, EmailPushCorrelation correlation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        cancellationToken.ThrowIfCancellationRequested();

        var deadline = RedisRecordLifetime.DeadlineOf(correlation.ExpiresAt, _timeProvider.GetUtcNow(), EntityDescription);

        var arguments = new List<RedisValue>(8)
        {
            deadline.ToUnixTimeMilliseconds(),
            token,
        };

        foreach (var entry in BuildEntries(correlation))
        {
            arguments.Add(entry.Name);
            arguments.Add(entry.Value);
        }

        var result = await _db.ScriptEvaluateAsync(
            StoreOrReuseLuaScript,
            new RedisKey[] { GetTransactionIndexKey(correlation.TransactionId), GetCorrelationKey(token) },
            arguments.ToArray());

        return result.ToString();
    }

    /// <inheritdoc />
    public async Task<EmailPushCorrelation?> GetAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await ReadCorrelationAsync(GetCorrelationKey(token));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The reads are issued before any of them is awaited, so the multiplexer writes them to the
    /// connection as one pipeline: a mail carrying eight token-shaped runs costs one round trip instead
    /// of eight. A correlation is a hash rather than a string, so there is no single command batching
    /// the reads the way MGET batches string reads — the pipeline is what makes this a batch.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, EmailPushCorrelation>> GetManyAsync(
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        cancellationToken.ThrowIfCancellationRequested();

        var issued = new List<(string Token, Task<HashEntry[]> Read)>(tokens.Count);
        var asked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in tokens)
        {
            if (asked.Add(token))
            {
                issued.Add((token, _db.HashGetAllAsync(GetCorrelationKey(token))));
            }
        }

        var found = new Dictionary<string, EmailPushCorrelation>(issued.Count, StringComparer.Ordinal);

        foreach (var (token, read) in issued)
        {
            if (ToCorrelation(await read) is { } correlation)
            {
                found[token] = correlation;
            }
        }

        return found;
    }

    /// <inheritdoc />
    public async Task<bool> TryConsumeAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var correlationKey = GetCorrelationKey(token);

        // The index key is derived from the transaction, so it is read here rather than inside the
        // script: a Lua script must declare every key it touches, and a key computed from a value
        // read mid-script would break on a clustered Redis. The token → transaction mapping never
        // changes, so reading it first races with nothing.
        var correlation = await ReadCorrelationAsync(correlationKey);

        if (correlation is null)
        {
            return false;
        }

        var result = await _db.ScriptEvaluateAsync(
            ConsumeLuaScript,
            new RedisKey[] { correlationKey, GetTransactionIndexKey(correlation.TransactionId) },
            new RedisValue[] { token });

        return (long)result == 1L;
    }

    /// <inheritdoc />
    public async Task<bool> IsMessageProcessedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Read-only check: it must not register the message-id, only report whether it was seen.
        return await _db.KeyExistsAsync(GetProcessedMessageKey(messageId));
    }

    /// <inheritdoc />
    public async Task<bool> TryRegisterProcessedMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // SET NX: the first registration of a message-id wins, a redelivery of the same email gets
        // false and is ignored. The record is kept for the configured deduplication window — it has
        // no owning entity to take a lifetime from.
        return await _db.StringSetAsync(
            GetProcessedMessageKey(messageId),
            RedisChannelStoreTimestamps.Format(_timeProvider.GetUtcNow()),
            _options.ProcessedMessageRetention,
            When.NotExists);
    }

    /// <summary>
    /// Builds the stored fields of a correlation record.
    /// </summary>
    /// <param name="correlation">Correlation data.</param>
    /// <returns>Hash entries to write.</returns>
    private static HashEntry[] BuildEntries(EmailPushCorrelation correlation)
    {
        var entries = new List<HashEntry>(4)
        {
            new(TransactionIdField, correlation.TransactionId),
            new(ExpiresAtField, RedisChannelStoreTimestamps.Format(correlation.ExpiresAt)),
        };

        // Written only when set, so a record of the sign-in keeps the shape it always had.
        if (correlation.ForClaimCompletion)
        {
            entries.Add(new HashEntry(ForClaimCompletionField, FlagSetValue));
        }

        return entries.ToArray();
    }

    /// <summary>
    /// Reads a correlation record by its Redis key.
    /// </summary>
    /// <param name="correlationKey">Redis key of the correlation hash.</param>
    /// <returns>Correlation data, or null when the record is absent or unusable.</returns>
    private async Task<EmailPushCorrelation?> ReadCorrelationAsync(string correlationKey)
    {
        return ToCorrelation(await _db.HashGetAllAsync(correlationKey));
    }

    /// <summary>
    /// Turns the stored fields of a correlation hash into a correlation record — the mapping the
    /// single read and the batched one share, so a record read in a pipeline is read by exactly the
    /// same rules as a record read on its own.
    /// </summary>
    /// <param name="entries">Fields of the hash; an empty array means the key holds nothing.</param>
    /// <returns>The correlation, or null when the key holds nothing or the record is unusable.</returns>
    private static EmailPushCorrelation? ToCorrelation(HashEntry[] entries)
    {
        if (entries.Length == 0)
        {
            return null;
        }

        var fields = entries.ToDictionary(
            entry => entry.Name.ToString(),
            entry => entry.Value,
            StringComparer.Ordinal);

        // A record missing a mandatory field is unusable and is reported as "not found": the contract
        // already makes null the outcome for a correlation that cannot be resolved.
        if (!fields.TryGetValue(TransactionIdField, out var transactionId)
            || RedisChannelStoreTimestamps.Parse(fields.GetValueOrDefault(ExpiresAtField)) is not { } expiresAt)
        {
            return null;
        }

        return new EmailPushCorrelation(
            transactionId.ToString(),
            expiresAt,
            ConsumedAt: null)
        {
            ForClaimCompletion = fields.GetValueOrDefault(ForClaimCompletionField) == FlagSetValue
        };
    }

    /// <summary>
    /// Builds the Redis key of a correlation record.
    /// </summary>
    /// <param name="token">String correlation token.</param>
    /// <returns>Redis key.</returns>
    private string GetCorrelationKey(string token)
    {
        return string.Concat(_options.EmailPushCorrelationKeyPrefix, token);
    }

    /// <summary>
    /// Builds the Redis key of the "transaction → live token" index.
    /// </summary>
    /// <param name="transactionId">String transaction identifier.</param>
    /// <returns>Redis index key.</returns>
    private string GetTransactionIndexKey(string transactionId)
    {
        return string.Concat(_options.EmailPushCorrelationKeyPrefix, TransactionIndexSegment, transactionId);
    }

    /// <summary>
    /// Builds the Redis key of a processed inbound message-id.
    /// </summary>
    /// <param name="messageId">Email identifier.</param>
    /// <returns>Redis key.</returns>
    private string GetProcessedMessageKey(string messageId)
    {
        return string.Concat(_options.ProcessedMessageKeyPrefix, messageId);
    }
}
