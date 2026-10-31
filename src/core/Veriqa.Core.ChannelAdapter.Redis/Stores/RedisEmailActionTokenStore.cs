// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using StackExchange.Redis;

using Veriqa.Core.ChannelAdapter.Email.Services;

namespace Veriqa.Core.ChannelAdapter.Redis.Stores;

/// <summary>
/// Redis implementation of the Email Pull-mode action token store (SPEC-016 §4.3). A magic link
/// opened on one replica resolves against the token minted on another, which the in-process default
/// cannot do.
/// </summary>
/// <remarks>
/// <para>
/// A token is one Redis hash whose TTL is the token's own <c>ExpiresAt</c>. Consuming a token
/// deletes the hash — the same thing the in-process store does with
/// <c>ConcurrentDictionary.TryRemove</c>, and the reason <c>MarkUsedAsync</c> is atomic without a
/// script: <c>DEL</c> reports "existed" to exactly one of the concurrent callers.
/// <c>TryMarkOpenedAsync</c> is an <c>HSETNX</c> guarded by the existence of the hash, so exactly one
/// concurrent open is recorded and a used or expired token records none.
/// </para>
/// <para>
/// Two consequences of leaning on Redis TTL, both intentional:
/// <see cref="GetTokenAsync"/> returns null for an expired token, where the in-process store would
/// still hand back the expired record until its sweep runs; and
/// <see cref="EmailActionToken.UsedAt"/> is never populated, because a used token is gone rather
/// than flagged. Callers already treat "null" and "used/expired" the same way, and the in-process
/// store removes a used token too, so the observable behaviour matches.
/// </para>
/// </remarks>
internal sealed class RedisEmailActionTokenStore : IEmailActionTokenStore
{
    /// <summary>
    /// Hash field holding the transaction identifier.
    /// </summary>
    private const string TransactionIdField = "TransactionId";

    /// <summary>
    /// Hash field holding the user's normalized email address.
    /// </summary>
    private const string NormalizedEmailField = "NormalizedEmail";

    /// <summary>
    /// Hash field holding the token expiry moment.
    /// </summary>
    private const string ExpiresAtField = "ExpiresAt";

    /// <summary>
    /// Hash field holding the moment the confirm page was first opened.
    /// </summary>
    private const string OpenedAtField = "OpenedAt";

    /// <summary>
    /// Entity description used in lifetime errors.
    /// </summary>
    private const string EntityDescription = "Email action token";

    /// <summary>
    /// Store options (key prefix).
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
    /// Creates the Redis action token store.
    /// </summary>
    /// <param name="connectionMultiplexer">Redis connection multiplexer.</param>
    /// <param name="options">Channel store options.</param>
    /// <param name="timeProvider">Time provider.</param>
    public RedisEmailActionTokenStore(
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
    public async Task StoreTokenAsync(string token, EmailActionToken data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();

        var key = GetTokenKey(token);
        var deadline = RedisRecordLifetime.DeadlineOf(data.ExpiresAt, _timeProvider.GetUtcNow(), EntityDescription);

        var entries = new List<HashEntry>(4)
        {
            new(TransactionIdField, data.TransactionId),
            new(NormalizedEmailField, data.NormalizedEmail),
            new(ExpiresAtField, RedisChannelStoreTimestamps.Format(data.ExpiresAt)),
        };

        if (data.OpenedAt is { } openedAt)
        {
            entries.Add(new HashEntry(OpenedAtField, RedisChannelStoreTimestamps.Format(openedAt)));
        }

        // DEL + HSET + PEXPIREAT in one MULTI/EXEC: writing a token must replace the record wholesale
        // (the in-process store assigns over the dictionary slot), never merge with leftover fields.
        var transaction = _db.CreateTransaction();
        var deleteTask = transaction.KeyDeleteAsync(key);
        var setTask = transaction.HashSetAsync(key, entries.ToArray());
        var expireTask = transaction.KeyExpireAsync(key, deadline.UtcDateTime);

        await transaction.ExecuteAsync();
        await Task.WhenAll(deleteTask, setTask, expireTask);
    }

    /// <inheritdoc />
    public async Task<EmailActionToken?> GetTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = await _db.HashGetAllAsync(GetTokenKey(token));

        if (entries.Length == 0)
        {
            return null;
        }

        var fields = entries.ToDictionary(
            entry => entry.Name.ToString(),
            entry => entry.Value,
            StringComparer.Ordinal);

        // A record missing a mandatory field is unusable, so it is reported as "not found" rather
        // than thrown at the caller: the contract already makes null the outcome for a token that
        // cannot be resolved, and the caller handles it.
        if (!fields.TryGetValue(TransactionIdField, out var transactionId)
            || !fields.TryGetValue(NormalizedEmailField, out var normalizedEmail)
            || RedisChannelStoreTimestamps.Parse(fields.GetValueOrDefault(ExpiresAtField)) is not { } expiresAt)
        {
            return null;
        }

        return new EmailActionToken(
            transactionId.ToString(),
            normalizedEmail.ToString(),
            expiresAt,
            UsedAt: null,
            OpenedAt: RedisChannelStoreTimestamps.Parse(fields.GetValueOrDefault(OpenedAtField)));
    }

    /// <inheritdoc />
    public async Task<bool> MarkUsedAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A single DEL is the whole atomicity story: Redis reports "the key existed" to exactly one
        // of the concurrent callers, so a magic link cannot be consumed twice across replicas.
        return await _db.KeyDeleteAsync(GetTokenKey(token));
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkOpenedAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // HSETNX guarded by the existence of the token hash: exactly one concurrent open wins, and a
        // used or expired token (its hash is gone) records nothing — so the "opened" status is
        // published once, and a mail scanner prefetch does not duplicate it.
        var result = await _db.ScriptEvaluateAsync(
            RedisChannelStoreScripts.MarkFieldOnce,
            new RedisKey[] { GetTokenKey(token) },
            new RedisValue[] { OpenedAtField, RedisChannelStoreTimestamps.Format(_timeProvider.GetUtcNow()) });

        return (long)result == 1L;
    }

    /// <summary>
    /// Builds the Redis key of an action token.
    /// </summary>
    /// <param name="token">String token.</param>
    /// <returns>Redis key.</returns>
    private string GetTokenKey(string token)
    {
        return string.Concat(_options.EmailActionTokenKeyPrefix, token);
    }
}
