// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using StackExchange.Redis;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Redis.Stores;

/// <summary>
/// Redis implementation of <see cref="IChannelStartCodeStore"/>: a code shown by one replica starts
/// the sign-in whichever replica receives the message carrying it. The codes of the whole deployment
/// share one key space: no key carries a tenant.
/// </summary>
/// <remarks>
/// <para>
/// A code is two Redis hashes written and removed together, both with the TTL of the record's own
/// <c>ExpiresAt</c>: one found by the code and naming the transaction, one found by the transaction
/// and naming the code. Each key is the prefix, a segment and a SHA-256 hash of the code or of the
/// transaction identifier, so neither of the two addresses of a transaction appears in a key a
/// <c>SCAN</c> or a monitoring tool would print. The code itself is kept in the value of the second hash — the page
/// that shows it again has nothing else to read it from.
/// </para>
/// <para>
/// Recording a code and taking it are one Lua script each, so the two checks and the two writes of
/// the former, and the comparison and the two removals of the latter, run as one indivisible step on
/// the server.
/// </para>
/// </remarks>
internal sealed class RedisChannelStartCodeStore : IChannelStartCodeStore
{
    /// <summary>
    /// Hash field holding the transaction identifier (the record found by the code).
    /// </summary>
    private const string TransactionIdField = "TransactionId";

    /// <summary>
    /// Hash field holding the code (the record found by the transaction).
    /// </summary>
    private const string CodeField = "Code";

    /// <summary>
    /// Hash field holding the deadline of the record.
    /// </summary>
    private const string ExpiresAtField = "ExpiresAt";

    /// <summary>
    /// Key segment of the records found by the code.
    /// </summary>
    private const string CodeSegment = "code:";

    /// <summary>
    /// Key segment of the records found by the transaction.
    /// </summary>
    private const string TransactionSegment = "tx:";

    /// <summary>
    /// Entity description used in lifetime errors.
    /// </summary>
    private const string EntityDescription = "Start code";

    /// <summary>
    /// Number of values the read of a record by its transaction returns.
    /// </summary>
    private const int TransactionRecordValueCount = 2;

    /// <summary>
    /// Lua script recording a code unless a live record with the same code or of the same transaction
    /// already exists.
    /// KEYS[1] — key of the record found by the code.
    /// KEYS[2] — key of the record found by the transaction.
    /// ARGV[1] — name of the field holding the transaction (<see cref="TransactionIdField"/>).
    /// ARGV[2] — transaction identifier.
    /// ARGV[3] — name of the field holding the code (<see cref="CodeField"/>).
    /// ARGV[4] — the code.
    /// ARGV[5] — name of the field holding the deadline (<see cref="ExpiresAtField"/>).
    /// ARGV[6] — the deadline.
    /// ARGV[7] — the deadline in Unix milliseconds (alive when checked in C# before the call); absolute,
    /// so a script delayed on its way does not keep the records past it.
    /// Returns: 1 — recorded by this call; 0 — one of the two records already exists.
    /// </summary>
    /// <remarks>
    /// Both hashes get the same deadline inside the same script, so they fall away at the same moment and
    /// neither can outlive the other.
    /// </remarks>
    private const string TryAddLuaScript = @"
if redis.call('EXISTS', KEYS[1]) == 1 or redis.call('EXISTS', KEYS[2]) == 1 then
    return 0
end
redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
redis.call('HSET', KEYS[1], ARGV[5], ARGV[6])
redis.call('PEXPIREAT', KEYS[1], ARGV[7])
redis.call('HSET', KEYS[2], ARGV[3], ARGV[4])
redis.call('HSET', KEYS[2], ARGV[5], ARGV[6])
redis.call('PEXPIREAT', KEYS[2], ARGV[7])
return 1";

    /// <summary>
    /// Lua script handing a code out, once: the record found by the code is removed together with the
    /// record of its transaction.
    /// KEYS[1] — key of the record found by the code.
    /// KEYS[2] — key of the record found by the transaction the code is expected to name.
    /// ARGV[1] — name of the field holding the transaction (<see cref="TransactionIdField"/>).
    /// ARGV[2] — name of the field holding the deadline (<see cref="ExpiresAtField"/>).
    /// ARGV[3] — transaction identifier the record must name.
    /// Returns: the deadline — both records removed by this call; nil — the record is absent, or
    /// names another transaction (nothing is removed).
    /// </summary>
    private const string TakeLuaScript = @"
local record = redis.call('HMGET', KEYS[1], ARGV[1], ARGV[2])
if record[1] ~= ARGV[3] then
    return false
end
redis.call('DEL', KEYS[1])
redis.call('DEL', KEYS[2])
return record[2]";

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
    /// Creates the Redis start code store.
    /// </summary>
    /// <param name="connectionMultiplexer">Redis connection multiplexer.</param>
    /// <param name="options">Channel store options.</param>
    /// <param name="timeProvider">Time provider.</param>
    public RedisChannelStartCodeStore(
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
    public async Task<bool> TryAddAsync(ChannelStartCode record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        var deadline = RedisRecordLifetime.DeadlineOf(record.ExpiresAt, _timeProvider.GetUtcNow(), EntityDescription);
        var transactionId = record.TransactionId.ToString();

        var result = await _db.ScriptEvaluateAsync(
            TryAddLuaScript,
            new RedisKey[]
            {
                GetCodeKey(record.Code),
                GetTransactionKey(transactionId)
            },
            new RedisValue[]
            {
                TransactionIdField,
                transactionId,
                CodeField,
                record.Code,
                ExpiresAtField,
                RedisChannelStoreTimestamps.Format(record.ExpiresAt),
                deadline.ToUnixTimeMilliseconds()
            });

        return (long)result == 1L;
    }

    /// <inheritdoc />
    public async Task<ChannelStartCode?> FindByTransactionAsync(
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var values = await _db.HashGetAsync(
            GetTransactionKey(transactionId.ToString()),
            new RedisValue[] { CodeField, ExpiresAtField });

        // A record that does not read back whole is treated as absent, the way an expired one is.
        if (values is not { Length: TransactionRecordValueCount }
            || values[0].IsNullOrEmpty
            || RedisChannelStoreTimestamps.Parse(values[1]) is not { } expiresAt)
        {
            return null;
        }

        // The key lives until the deadline, but the deadline is what the contract answers by: a key
        // Redis has not evicted yet in the same instant is not a record that still answers.
        if (_timeProvider.GetUtcNow() >= expiresAt)
        {
            return null;
        }

        return new ChannelStartCode
        {
            Code = values[0].ToString(),
            TransactionId = transactionId,
            ExpiresAt = expiresAt
        };
    }

    /// <inheritdoc />
    public async Task<ChannelStartCode?> TakeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var codeKey = GetCodeKey(code);

        // The key of the second record is derived from the transaction, so the transaction is read
        // here rather than inside the script: a Lua script must declare every key it touches, and a
        // key computed from a value read mid-script would break on a clustered Redis. This read
        // decides nothing — the script compares the transaction again and removes the two records
        // only while the code still names it. A code that was taken in between, even one issued anew
        // to another transaction since, is answered as absent: the caller presented the code of the
        // transaction it read, and somebody else has taken that one.
        var rawTransactionId = await _db.HashGetAsync(codeKey, TransactionIdField);
        if (rawTransactionId.IsNullOrEmpty
            || !TransactionId.TryParse(rawTransactionId.ToString(), out var transactionId))
        {
            return null;
        }

        var result = await _db.ScriptEvaluateAsync(
            TakeLuaScript,
            new RedisKey[] { codeKey, GetTransactionKey(transactionId.ToString()) },
            new RedisValue[] { TransactionIdField, ExpiresAtField, transactionId.ToString() });

        // A record that does not read back whole is treated as absent, the way an expired one is; the
        // script has removed it either way.
        if (result.IsNull
            || RedisChannelStoreTimestamps.Parse((RedisValue)result) is not { } expiresAt)
        {
            return null;
        }

        if (_timeProvider.GetUtcNow() >= expiresAt)
        {
            return null;
        }

        return new ChannelStartCode
        {
            Code = code,
            TransactionId = transactionId,
            ExpiresAt = expiresAt
        };
    }

    /// <summary>
    /// Builds the Redis key of the record found by a code.
    /// </summary>
    /// <param name="code">The code.</param>
    /// <returns>Redis key.</returns>
    private string GetCodeKey(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);

        return string.Concat(_options.StartCodeKeyPrefix, CodeSegment, Hash(code));
    }

    /// <summary>
    /// Builds the Redis key of the record found by a transaction.
    /// </summary>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <returns>Redis key.</returns>
    private string GetTransactionKey(string transactionId)
    {
        return string.Concat(_options.StartCodeKeyPrefix, TransactionSegment, Hash(transactionId));
    }

    /// <summary>
    /// Hashes the variable part of a key: hex SHA-256 of the value.
    /// </summary>
    /// <param name="value">The code or the transaction identifier.</param>
    /// <returns>Lower-case hex digest.</returns>
    private static string Hash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        return Convert.ToHexStringLower(digest);
    }
}
