// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using StackExchange.Redis;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Redis.Stores;

/// <summary>
/// Redis implementation of <see cref="IPhoneRequestCorrelationStore"/> (SPEC-041 L44): the answer to a
/// phone number request finds its transaction whichever replica it lands on.
/// </summary>
/// <remarks>
/// <para>
/// A record is one Redis hash whose TTL is the record's own <c>ExpiresAt</c>. The key is built from a
/// SHA-256 hash of the chat — tenant, channel type and channel user — so the channel user identifier
/// never appears in a key a <c>SCAN</c> or a monitoring tool would print. The hash keeps only what the
/// chat does not already name: the transaction, the secondary action, its label and the deadline; the
/// chat itself is restated from the arguments of the lookup.
/// </para>
/// <para>
/// The conditional removal is one Lua script: the comparison with the transaction and the removal run
/// as one indivisible step on the server, so a record a later request wrote in between is never
/// removed.
/// </para>
/// </remarks>
internal sealed class RedisPhoneRequestCorrelationStore : IPhoneRequestCorrelationStore
{
    /// <summary>
    /// Hash field holding the transaction identifier.
    /// </summary>
    private const string TransactionIdField = "TransactionId";

    /// <summary>
    /// Hash field holding the secondary action.
    /// </summary>
    private const string SecondaryActionField = "SecondaryAction";

    /// <summary>
    /// Hash field holding the label of the secondary button.
    /// </summary>
    private const string SecondaryButtonLabelField = "SecondaryButtonLabel";

    /// <summary>
    /// Hash field holding the deadline of the record.
    /// </summary>
    private const string ExpiresAtField = "ExpiresAt";

    /// <summary>
    /// Entity description used in lifetime errors.
    /// </summary>
    private const string EntityDescription = "Phone request correlation";

    /// <summary>
    /// Separator between the parts of the chat hashed into the key. A control character, so no
    /// tenant, channel type or channel user identifier can contain it and two different chats cannot
    /// run together into the same input.
    /// </summary>
    private const char KeyPartSeparator = '\u001F';

    /// <summary>
    /// Lua script removing a record only while it names the given transaction.
    /// KEYS[1] — record hash key.
    /// ARGV[1] — name of the field holding the transaction (<see cref="TransactionIdField"/>).
    /// ARGV[2] — transaction identifier the record must name.
    /// Returns: 1 — removed by this call; 0 — absent, or naming another transaction.
    /// </summary>
    private const string RemoveIfTransactionLuaScript = @"
if redis.call('HGET', KEYS[1], ARGV[1]) == ARGV[2] then
    return redis.call('DEL', KEYS[1])
end
return 0";

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
    /// Creates the Redis phone request correlation store.
    /// </summary>
    /// <param name="connectionMultiplexer">Redis connection multiplexer.</param>
    /// <param name="options">Channel store options.</param>
    /// <param name="timeProvider">Time provider.</param>
    public RedisPhoneRequestCorrelationStore(
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
    public async Task SaveAsync(PhoneRequestCorrelation correlation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        cancellationToken.ThrowIfCancellationRequested();

        var key = GetKey(correlation.TenantId, correlation.ChannelType, correlation.ChannelUserId);
        var deadline = RedisRecordLifetime.DeadlineOf(correlation.ExpiresAt, _timeProvider.GetUtcNow(), EntityDescription);

        HashEntry[] entries =
        [
            new(TransactionIdField, correlation.TransactionId.ToString()),
            new(SecondaryActionField, correlation.SecondaryAction.ToString()),
            new(SecondaryButtonLabelField, correlation.SecondaryButtonLabel),
            new(ExpiresAtField, RedisChannelStoreTimestamps.Format(correlation.ExpiresAt))
        ];

        // DEL + HSET + PEXPIREAT in one MULTI/EXEC: a later request replaces the record of the chat
        // wholesale, as the in-process store assigns over its dictionary slot.
        var transaction = _db.CreateTransaction();
        var deleteTask = transaction.KeyDeleteAsync(key);
        var setTask = transaction.HashSetAsync(key, entries);
        var expireTask = transaction.KeyExpireAsync(key, deadline.UtcDateTime);

        await transaction.ExecuteAsync();
        await Task.WhenAll(deleteTask, setTask, expireTask);
    }

    /// <inheritdoc />
    public async Task<PhoneRequestCorrelation?> FindAsync(
        string? tenantId,
        string channelType,
        string channelUserId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = await _db.HashGetAllAsync(GetKey(tenantId, channelType, channelUserId));
        if (entries.Length is 0)
        {
            return null;
        }

        var fields = entries.ToDictionary(entry => entry.Name.ToString(), entry => entry.Value, StringComparer.Ordinal);

        // A record that does not read back whole is treated as absent, the way an expired one is.
        if (!fields.TryGetValue(TransactionIdField, out var rawTransactionId)
            || !TransactionId.TryParse(rawTransactionId.ToString(), out var transactionId)
            || !fields.TryGetValue(SecondaryActionField, out var rawAction)
            || !Enum.TryParse<PhoneStepAction>(rawAction.ToString(), ignoreCase: false, out var action)
            || !fields.TryGetValue(SecondaryButtonLabelField, out var label)
            || label.IsNullOrEmpty
            || !fields.TryGetValue(ExpiresAtField, out var rawExpiresAt)
            || RedisChannelStoreTimestamps.Parse(rawExpiresAt) is not { } expiresAt)
        {
            return null;
        }

        // The key lives until the deadline, but the deadline is what the contract answers by: a key
        // Redis has not evicted yet in the same second is not a record that still answers.
        if (_timeProvider.GetUtcNow() >= expiresAt)
        {
            return null;
        }

        return new PhoneRequestCorrelation
        {
            TenantId = tenantId,
            ChannelType = channelType,
            ChannelUserId = channelUserId,
            TransactionId = transactionId,
            SecondaryAction = action,
            SecondaryButtonLabel = label.ToString(),
            ExpiresAt = expiresAt
        };
    }

    /// <inheritdoc />
    public async Task RemoveAsync(
        string? tenantId,
        string channelType,
        string channelUserId,
        TransactionId transactionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _db.ScriptEvaluateAsync(
            RemoveIfTransactionLuaScript,
            new RedisKey[] { GetKey(tenantId, channelType, channelUserId) },
            new RedisValue[] { TransactionIdField, transactionId.ToString() });
    }

    /// <summary>
    /// Builds the Redis key of a chat: the prefix followed by the hex SHA-256 of the tenant key
    /// segment, the channel type and the channel user.
    /// </summary>
    /// <param name="tenantId">Tenant; null — the default implicit tenant.</param>
    /// <param name="channelType">Channel type.</param>
    /// <param name="channelUserId">Channel user.</param>
    /// <returns>Redis key.</returns>
    private string GetKey(string? tenantId, string channelType, string channelUserId)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);
        ArgumentException.ThrowIfNullOrEmpty(channelUserId);

        var chat = string.Join(KeyPartSeparator, TenantKey.Segment(tenantId), channelType, channelUserId);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(chat));

        return string.Concat(_options.PhoneRequestCorrelationKeyPrefix, Convert.ToHexStringLower(digest));
    }
}
