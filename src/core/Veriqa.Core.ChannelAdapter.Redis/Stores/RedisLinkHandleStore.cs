// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using StackExchange.Redis;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Redis.Stores;

/// <summary>
/// Redis implementation of <see cref="ILinkHandleStore"/>: a link handle is exchanged whichever replica
/// the request of the integrator lands on.
/// </summary>
/// <remarks>
/// <para>
/// A record is one Redis hash whose TTL is the record's own <c>ExpiresAt</c>. The key is the prefix
/// followed by the fingerprint of the handle, so the handle itself never appears in a key or a value.
/// </para>
/// <para>
/// Handing a record out is one Lua script: the comparison with the client and the removal run as one
/// indivisible step on the server, so of several concurrent calls exactly one receives the record and
/// a call of another client leaves it in place.
/// </para>
/// </remarks>
internal sealed class RedisLinkHandleStore : ILinkHandleStore
{
    /// <summary>
    /// Hash field holding the client the sign-in is attributed to.
    /// </summary>
    private const string ApplicationIdField = "ApplicationId";

    /// <summary>
    /// Hash field holding the transaction identifier.
    /// </summary>
    private const string TransactionIdField = "TransactionId";

    /// <summary>
    /// Hash field holding the deadline of the record.
    /// </summary>
    private const string ExpiresAtField = "ExpiresAt";

    /// <summary>
    /// Entity description used in lifetime errors.
    /// </summary>
    private const string EntityDescription = "Link handle";

    /// <summary>
    /// Number of values the script returns for a record it hands out.
    /// </summary>
    private const int TakenValueCount = 3;

    /// <summary>
    /// Lua script handing a record out, once, to the client it names.
    /// KEYS[1] — record hash key.
    /// ARGV[1] — name of the field holding the client (<see cref="ApplicationIdField"/>).
    /// ARGV[2] — name of the field holding the transaction (<see cref="TransactionIdField"/>).
    /// ARGV[3] — name of the field holding the deadline (<see cref="ExpiresAtField"/>).
    /// ARGV[4] — client the record must name.
    /// Returns: the client, the transaction and the deadline — removed by this call; nil — absent, or
    /// naming another client (the record stays).
    /// </summary>
    private const string TakeIfApplicationLuaScript = @"
local record = redis.call('HMGET', KEYS[1], ARGV[1], ARGV[2], ARGV[3])
if record[1] ~= ARGV[4] then
    return false
end
redis.call('DEL', KEYS[1])
return record";

    /// <summary>
    /// Store options (key prefixes).
    /// </summary>
    private readonly RedisChannelStoreOptions _options;

    /// <summary>
    /// Logical Redis database handle.
    /// </summary>
    private readonly IDatabase _db;

    /// <summary>
    /// Clock a record is checked to be alive against before it is written, and its deadline is read against.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the Redis link handle store.
    /// </summary>
    /// <param name="connectionMultiplexer">Redis connection multiplexer.</param>
    /// <param name="options">Channel store options.</param>
    /// <param name="timeProvider">Time provider.</param>
    public RedisLinkHandleStore(
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
    public async Task SaveAsync(LinkHandleRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(record.ApplicationId, nameof(record));
        cancellationToken.ThrowIfCancellationRequested();

        var key = GetKey(record.HandleHash);
        var deadline = RedisRecordLifetime.DeadlineOf(record.ExpiresAt, _timeProvider.GetUtcNow(), EntityDescription);

        HashEntry[] entries =
        [
            new(ApplicationIdField, record.ApplicationId),
            new(TransactionIdField, record.TransactionId.ToString()),
            new(ExpiresAtField, RedisChannelStoreTimestamps.Format(record.ExpiresAt))
        ];

        // DEL + HSET + PEXPIREAT in one MULTI/EXEC: the record is written whole and never without its deadline.
        var transaction = _db.CreateTransaction();
        var deleteTask = transaction.KeyDeleteAsync(key);
        var setTask = transaction.HashSetAsync(key, entries);
        var expireTask = transaction.KeyExpireAsync(key, deadline.UtcDateTime);

        await transaction.ExecuteAsync();
        await Task.WhenAll(deleteTask, setTask, expireTask);
    }

    /// <inheritdoc />
    public async Task<LinkHandleRecord?> TakeAsync(
        string handleHash,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationId);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _db.ScriptEvaluateAsync(
            TakeIfApplicationLuaScript,
            new RedisKey[] { GetKey(handleHash) },
            new RedisValue[] { ApplicationIdField, TransactionIdField, ExpiresAtField, applicationId });

        if (result.IsNull)
        {
            return null;
        }

        // A record that does not read back whole is treated as absent, the way an expired one is; the
        // script has removed it either way.
        var values = (RedisValue[]?)result;
        if (values is not { Length: TakenValueCount }
            || !TransactionId.TryParse(values[1].ToString(), out var transactionId)
            || RedisChannelStoreTimestamps.Parse(values[2]) is not { } expiresAt)
        {
            return null;
        }

        // The key lives until the deadline, but the deadline is what the contract answers by: a key
        // Redis has not evicted yet in the same second is not a record that still answers.
        if (_timeProvider.GetUtcNow() >= expiresAt)
        {
            return null;
        }

        return new LinkHandleRecord
        {
            HandleHash = handleHash,
            TransactionId = transactionId,
            ApplicationId = applicationId,
            ExpiresAt = expiresAt
        };
    }

    /// <summary>
    /// Builds the Redis key of a record: the prefix followed by the fingerprint of the handle.
    /// </summary>
    /// <param name="handleHash">Fingerprint of the handle.</param>
    /// <returns>Redis key.</returns>
    private string GetKey(string handleHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(handleHash);

        return string.Concat(_options.LinkHandleKeyPrefix, handleHash);
    }
}
