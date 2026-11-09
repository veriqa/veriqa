// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Veriqa.Core.TransactionEngine.Configuration;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.TransactionEngine.Store.Redis;

/// <summary>
/// Redis implementation of the transaction store.
/// Key removal is native (Redis TTL), so TransactionCleanupService skips its own deletion pass —
/// that is what ISupportsNativeExpiry signals. The expiry of a transaction is not native: the key of
/// a non-terminal transaction outlives <c>ExpiresAt</c> by the pass margin,
/// <c>max(CleanupIntervalSeconds, CleanupErrorRetryDelaySeconds) + CompletedRetentionSeconds</c>, which is
/// the window the cleanup pass has to read it back, move it to Expired and publish TransactionExpiredEvent.
/// A key deleted exactly at <c>ExpiresAt</c> would make that expiry pass silently. The key of a confirmed
/// transaction outlives, by the same margin, the later of <c>ExpiresAt</c> and the latest end its
/// finalization window can have (<c>UpdatedAt</c> plus the upper bound of
/// <c>ConfirmationFinalizationTimeoutSeconds</c>): past the end of the window the cleanup pass reads it back
/// through the confirmed index and moves it to Failed with <c>finalization_timeout</c>.
/// </summary>
internal sealed class RedisTransactionStore : ITransactionStore, ISupportsNativeExpiry
{
    /// <summary>
    /// Key segment for the idempotency index.
    /// </summary>
    private const string IdempotencyIndexSegment = "idx:idmp:";

    /// <summary>
    /// Key segment for the sign-in subject index: a set of transaction ids per fingerprint of the
    /// sign-in subject scope (to support GetPendingSignInsAsync).
    /// </summary>
    private const string SignInSubjectIndexSegment = "idx:signin:";

    /// <summary>
    /// Key segment for the expiry sorted set index (to support GetExpiredAsync).
    /// </summary>
    private const string ExpiryIndexSegment = "expiry:index";

    /// <summary>
    /// Key segment for the confirmed sorted set index: the ids of confirmed transactions scored by the
    /// UpdatedAt of the stored document in Unix milliseconds (to support GetStalledConfirmedAsync).
    /// </summary>
    private const string ConfirmedIndexSegment = "confirmed:index";

    /// <summary>
    /// What the expiry pass reads from its index: the records a deadline still ends.
    /// </summary>
    private static readonly IndexedSweep ExpirySweep = new("expiry index", TransactionStateMachine.ExpirableStates);

    /// <summary>
    /// What the finalization pass reads from its index: the records still waiting for finalization.
    /// </summary>
    private static readonly IndexedSweep FinalizationSweep = new("confirmed index", FrozenSet.Create(TransactionState.Confirmed));

    /// <summary>
    /// Segment separator in the idempotency key.
    /// </summary>
    private const string KeySegmentSeparator = ":";

    /// <summary>
    /// Lua script for atomically saving a transaction along with the idempotency index.
    /// Uses SET NX for both keys, rolls back the main key on an idmpKey conflict.
    /// The two keys get their own TTLs: the main key outlives <c>ExpiresAt</c> by the pass margin
    /// (see <see cref="GetPassMarginSeconds"/>), while the idempotency key ends exactly at
    /// <c>ExpiresAt</c> — a repeat of the same idempotency key after the transaction expired must start a
    /// new transaction, not hit a conflict on a dead one.
    /// KEYS[1] — main transaction key.
    /// KEYS[2] — idempotency index key (passed only when an idempotency scope/key is present).
    /// ARGV[1] — transaction JSON.
    /// ARGV[2] — TTL of the main key in seconds (always >0, validated in C# before the call).
    /// ARGV[3] — TTL of the idempotency key in seconds (always >0, validated in C# before the call).
    /// ARGV[4] — string transaction ID (value for the idempotency key).
    /// Returns: 1 = success, 0 = duplicate ID, -1 = idempotency key conflict.
    /// </summary>
    private const string AddTransactionLuaScript = @"
local mainExpiry = tonumber(ARGV[2])
local ok = redis.call('SET', KEYS[1], ARGV[1], 'NX', 'EX', mainExpiry)
if not ok then
    return 0
end
if #KEYS >= 2 then
    local idmpExpiry = tonumber(ARGV[3])
    local idmpOk = redis.call('SET', KEYS[2], ARGV[4], 'NX', 'EX', idmpExpiry)
    if not idmpOk then
        redis.call('DEL', KEYS[1])
        return -1
    end
end
return 1";

    /// <summary>
    /// Lua script for an atomic compare-and-swap on ConcurrencyToken.
    /// The conditionally written fields of the new document — the hop observations, the surface, the
    /// outcomes and the form token fingerprint of the claims completion — are taken from the stored one,
    /// not from the passed one (a field absent from the stored document stays absent): an update neither writes nor erases
    /// them, and a value written between the caller's read and this write survives it. Re-encoding the document with cjson is lossless because every top-level
    /// value of the document is a string (snapshots are nested JSON strings, moments are ISO 8601
    /// strings) — cjson would round a large number, and there is none.
    /// KEYS[1] — transaction key.
    /// ARGV[1] — expected ConcurrencyToken.
    /// ARGV[2] — new transaction JSON.
    /// ARGV[3] — expiry Unix timestamp (seconds, >0). Must always be >0 for live transactions.
    /// ARGV[4..] — document fields written only by conditional writes, carried over from the stored document.
    /// Returns: 1 — success, 0 — conflict or not found.
    /// </summary>
    private const string UpdateTransactionLuaScript = @"
local data = redis.call('GET', KEYS[1])
if data == false then
    return 0
end
local ok, obj = pcall(cjson.decode, data)
if not ok then
    return 0
end
if obj['ConcurrencyToken'] ~= ARGV[1] then
    return 0
end
local newObj = cjson.decode(ARGV[2])
for i = 4, #ARGV do
    newObj[ARGV[i]] = obj[ARGV[i]]
end
redis.call('SET', KEYS[1], cjson.encode(newObj))
redis.call('EXPIREAT', KEYS[1], tonumber(ARGV[3]))
return 1";

    /// <summary>
    /// Lua script that puts a transaction id into the set of its sign-in subject and makes the set
    /// live at least as long as the transaction key. The set is shared by every sign-in of the subject,
    /// so its deadline only ever moves forward: a transaction with a shorter life must not cut the set
    /// from under one with a longer life. A set without a deadline (just created) reports a negative
    /// TTL and gets one.
    /// KEYS[1] — sign-in subject index key.
    /// ARGV[1] — string transaction ID.
    /// ARGV[2] — seconds the transaction key has left to live (always >0, validated in C# before the call).
    /// Returns: 1.
    /// </summary>
    private const string IndexSignInSubjectLuaScript = @"
redis.call('SADD', KEYS[1], ARGV[1])
local wanted = tonumber(ARGV[2])
if redis.call('TTL', KEYS[1]) < wanted then
    redis.call('EXPIRE', KEYS[1], wanted)
end
return 1";

    /// <summary>
    /// Lua script for a one-time write of a hop observation field inside the transaction document
    /// (SPEC-025 HOP-071a, HOP-080): the emptiness check and the write run as one step, so of two
    /// concurrent calls for one field exactly one writes. Only the target field changes — the
    /// ConcurrencyToken and UpdatedAt of the document stay as they are, so a concurrent update with a
    /// valid token is not turned into a conflict. KEEPTTL keeps the deadline of the key; a missing key
    /// is not created. A field absent from the document (written before the field existed) counts as
    /// empty. Re-encoding with cjson is lossless for the reason given on the update script.
    /// KEYS[1] — transaction key.
    /// ARGV[1] — document field to write.
    /// ARGV[2] — value to write (stored as a JSON string).
    /// ARGV[3] — optional: document field that must already be filled for the write to go through.
    /// Returns: 1 — written by this call; 0 — the field is already filled, the required field is
    /// empty, or the transaction is not stored.
    /// </summary>
    private const string WriteObservationOnceLuaScript = @"
local data = redis.call('GET', KEYS[1])
if data == false then
    return 0
end
local ok, obj = pcall(cjson.decode, data)
if not ok or type(obj) ~= 'table' then
    return 0
end
local current = obj[ARGV[1]]
if current ~= nil and current ~= cjson.null then
    return 0
end
if #ARGV >= 3 then
    local required = obj[ARGV[3]]
    if required == nil or required == cjson.null then
        return 0
    end
end
obj[ARGV[1]] = ARGV[2]
redis.call('SET', KEYS[1], cjson.encode(obj), 'KEEPTTL')
return 1";

    /// <summary>
    /// Lua script for a conditional write of one claims completion field inside the transaction
    /// document (SPEC-041 C90): the state, the completion mark and the current value of the field are
    /// checked, and the field written, in one step. Only the target field changes — the
    /// ConcurrencyToken and UpdatedAt of the document stay as they are, so a concurrent update with a
    /// valid token is not turned into a conflict. KEEPTTL keeps the deadline of the key; a missing key
    /// is not created. A field absent from the document counts as empty. Re-encoding with cjson is
    /// lossless for the reason given on the update script.
    /// KEYS[1] — transaction key.
    /// ARGV[1] — document field to write.
    /// ARGV[2] — value to write (stored as a JSON string).
    /// ARGV[3] — value the field must hold now; the empty string — the field must be empty.
    /// ARGV[4] — state the document must be in.
    /// ARGV[5] — document field of the completion mark, which must be filled.
    /// ARGV[6] — optional: exact value the completion mark must hold.
    /// Returns: 1 — written by this call; 0 — a condition does not hold, or the transaction is not stored.
    /// </summary>
    private const string WriteClaimCompletionFieldLuaScript = @"
local data = redis.call('GET', KEYS[1])
if data == false then
    return 0
end
local ok, obj = pcall(cjson.decode, data)
if not ok or type(obj) ~= 'table' then
    return 0
end
if obj['State'] ~= ARGV[4] then
    return 0
end
local mark = obj[ARGV[5]]
if mark == nil or mark == cjson.null then
    return 0
end
if #ARGV >= 6 and mark ~= ARGV[6] then
    return 0
end
local current = obj[ARGV[1]]
if current == nil or current == cjson.null then
    current = ''
end
if current ~= ARGV[3] then
    return 0
end
obj[ARGV[1]] = ARGV[2]
redis.call('SET', KEYS[1], cjson.encode(obj), 'KEEPTTL')
return 1";

    /// <summary>
    /// Document fields written only by conditional writes — the hop observations, the surface, the
    /// outcomes and the form token fingerprint of the claims completion — carried over from the stored
    /// document by every update. The document keys are the entity property names: the store's serializer options set no naming
    /// policy.
    /// </summary>
    private static readonly RedisValue[] ConditionalDocumentFields =
    [
        nameof(TransactionEntity.ScannerContextSnapshotJson),
        nameof(TransactionEntity.ScannerFingerprint),
        nameof(TransactionEntity.HopFirstResolvedAt),
        nameof(TransactionEntity.HopInterstitialOpenedAt),
        nameof(TransactionEntity.HopChannelLaunchedAt),
        nameof(TransactionEntity.HopRepeatResolvedAt),
        nameof(TransactionEntity.ClaimCompletionSurface),
        nameof(TransactionEntity.ClaimCompletionResultsJson),
        nameof(TransactionEntity.ClaimCompletionFormTokenHash)
    ];

    /// <summary>
    /// Stored name of the Pending state — the one state the claims completion writes go through in.
    /// </summary>
    private const string PendingStateName = nameof(TransactionState.Pending);

    /// <summary>
    /// Stored name of the Confirmed state — the one state the confirmed index holds.
    /// </summary>
    private const string ConfirmedStateName = nameof(TransactionState.Confirmed);

    /// <summary>
    /// Upper bound of ConfirmationFinalizationTimeoutSeconds. The value repeats the upper bound enforced by
    /// <c>TransactionEngineOptionsValidator</c>, and the two change together. The key of a confirmed
    /// transaction is sized by the bound, not by the current value, so a window raised at runtime does not
    /// end a key written under the shorter one before the cleanup pass reaches it.
    /// </summary>
    private const int MaxConfirmationFinalizationTimeoutSeconds = 120;

    /// <summary>
    /// Store options.
    /// </summary>
    private readonly RedisTransactionStoreOptions _options;

    /// <summary>
    /// Transaction Engine configuration (for CompletedRetentionSeconds — the retention window of a
    /// dead transaction, expired or terminal — and, with CleanupIntervalSeconds and
    /// CleanupErrorRetryDelaySeconds, for the pass margin by which the key of a non-terminal transaction
    /// outlives the moment the cleanup pass owes it a transition).
    /// </summary>
    private readonly IOptionsMonitor<TransactionEngineOptions> _engineOptions;

    /// <summary>
    /// Logical Redis database handle.
    /// </summary>
    private readonly IDatabase _db;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<RedisTransactionStore> _logger;

    /// <summary>
    /// Clock the relative Redis TTLs are computed from.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an instance of the Redis transaction store.
    /// </summary>
    /// <param name="connectionMultiplexer">Redis connection multiplexer.</param>
    /// <param name="options">Store options.</param>
    /// <param name="engineOptions">Transaction Engine configuration.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Time provider.</param>
    public RedisTransactionStore(
        IConnectionMultiplexer connectionMultiplexer,
        IOptions<RedisTransactionStoreOptions> options,
        IOptionsMonitor<TransactionEngineOptions> engineOptions,
        ILogger<RedisTransactionStore> logger,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _engineOptions = engineOptions;
        _db = connectionMultiplexer.GetDatabase();
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        // Atomically save the transaction and the idempotency index via a single Lua script.
        // Guarantees that under a data race either both keys are written or neither is.
        ArgumentNullException.ThrowIfNull(transaction);

        var entity = TransactionEntityMapper.ToEntity(transaction);
        var json = SerializeEntity(entity);
        var mainKey = GetTransactionKey(transaction.Id.ToString());
        var expirySecs = GetExpirySecs(entity.ExpiresAt);

        // A transaction whose TTL has already expired is not written to the store.
        // The guard is on ExpiresAt itself, not on the retained key: the pass margin below keeps a dead
        // transaction readable, it does not make an already-dead one worth creating.
        if (expirySecs <= 0)
        {
            throw new InvalidOperationException(
                $"Transaction with ID '{transaction.Id}' has already expired (ExpiresAt={entity.ExpiresAt:O}) and cannot be saved");
        }

        // The main key outlives ExpiresAt by the pass margin (same rule as ComputeEffectiveExpiryTimestamp),
        // the idempotency key does not — see the script's summary for why they differ.
        var mainKeyTtlSecs = expirySecs + GetPassMarginSeconds();

        var hasIdmp = entity.IdempotencyScope is not null && entity.IdempotencyKey is not null;

        // Pass only the keys in use: without idempotency — only mainKey.
        // This prevents CROSSSLOT errors in Redis Cluster (all KEYS must be in the same slot).
        var keys = hasIdmp
            ? new RedisKey[] { mainKey, GetIdempotencyKey(entity.IdempotencyScope!, entity.IdempotencyKey!) }
            : new RedisKey[] { mainKey };

        var result = (long)await _db.ScriptEvaluateAsync(
            AddTransactionLuaScript,
            keys,
            new RedisValue[] { json, mainKeyTtlSecs, expirySecs, transaction.Id.ToString() });

        switch (result)
        {
            case 0:
                _logger.LogWarning(
                    "Duplicate TransactionId in the Redis Lua script (ADD). TransactionId: {TransactionId}, Scope: {Scope}, KeyFingerprint: {KeyFingerprint}",
                    transaction.Id,
                    entity.IdempotencyScope,
                    IdempotencyKeyFingerprint.Compute(entity.IdempotencyScope, entity.IdempotencyKey));
                throw new InvalidOperationException(
                    $"Transaction with ID '{transaction.Id}' already exists in the store");
            case -1:
                _logger.LogWarning(
                    "Idempotency key conflict in the Redis Lua script. TransactionId: {TransactionId}, Scope: {Scope}, KeyFingerprint: {KeyFingerprint}",
                    transaction.Id,
                    entity.IdempotencyScope,
                    IdempotencyKeyFingerprint.Compute(entity.IdempotencyScope, entity.IdempotencyKey));
                throw new DuplicateIdempotencyKeyException(
                    entity.IdempotencyScope!,
                    entity.IdempotencyKey!);
        }

        _logger.LogDebug(
            "Transaction saved in Redis (Lua ADD). TransactionId: {TransactionId}, TTL: {TtlSeconds}s, key TTL: {KeyTtlSeconds}s",
            transaction.Id, expirySecs, mainKeyTtlSecs);

        // Register the transaction in the expiry sorted set index.
        // Used by GetExpiredAsync to publish TransactionExpiredEvent.
        // Doing this outside the Lua script is acceptable: on a partial failure Redis TTL will still delete the key.
        await _db.SortedSetAddAsync(
            GetExpiryIndexKey(),
            transaction.Id.ToString(),
            entity.ExpiresAt.ToUnixTimeSeconds());

        await IndexSignInSubjectAsync(transaction, mainKeyTtlSecs);
    }

    /// <inheritdoc />
    public async Task<Transaction?> GetByIdAsync(TransactionId id, CancellationToken cancellationToken = default)
    {
        // Get the transaction JSON by key and deserialize it
        var mainKey = GetTransactionKey(id.ToString());
        var entity = await GetEntityAsync(mainKey);

        return entity is not null
            ? TransactionEntityMapper.ToDomain(entity)
            : null;
    }

    /// <inheritdoc />
    public async Task<Transaction?> GetByIdempotencyKeyAsync(
        string scope,
        string key,
        CancellationToken cancellationToken = default)
    {
        // Look up the idempotency index key, then the main transaction key
        var idmpKey = GetIdempotencyKey(scope, key);
        var txIdValue = await _db.StringGetAsync(idmpKey);

        if (txIdValue.IsNullOrEmpty)
        {
            return null;
        }

        var txId = txIdValue.ToString();
        var mainKey = GetTransactionKey(txId);
        var entity = await GetEntityAsync(mainKey);

        if (entity is null)
        {
            // The index is stale (the transaction's TTL expired) — clean it up
            await _db.KeyDeleteAsync(idmpKey);
            return null;
        }

        return TransactionEntityMapper.ToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Transaction transaction,
        string expectedConcurrencyToken,
        CancellationToken cancellationToken = default)
    {
        // Atomic CAS via Lua: check the token and update the JSON, re-stating the key deadline.
        // ExpiresAt is immutable after creation; the deadline of the key is derived from it and from
        // the state being written (ComputeEffectiveExpiryTimestamp), so every write passes it as EXPIREAT.
        var entity = TransactionEntityMapper.ToEntity(transaction);
        var newJson = SerializeEntity(entity);
        var mainKey = GetTransactionKey(transaction.Id.ToString());

        var expiryTimestamp = ComputeEffectiveExpiryTimestamp(entity);

        // The conditionally written fields of newJson are replaced inside the script by the stored ones.
        RedisValue[] arguments = [expectedConcurrencyToken, newJson, expiryTimestamp, .. ConditionalDocumentFields];

        var result = await _db.ScriptEvaluateAsync(
            UpdateTransactionLuaScript,
            new RedisKey[] { mainKey },
            arguments);

        var updated = (long)result == 1L;

        if (updated)
        {
            await IndexStoredStateAsync(transaction.Id.ToString(), entity);

            await IndexSignInSubjectAsync(
                transaction,
                expiryTimestamp - _timeProvider.GetUtcNow().ToUnixTimeSeconds());
        }

        return updated;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(TransactionId id, CancellationToken cancellationToken = default)
    {
        // Delete the main key and the associated idempotency index
        var mainKey = GetTransactionKey(id.ToString());
        var entity = await GetEntityAsync(mainKey);

        var deleted = await _db.KeyDeleteAsync(mainKey);

        // Delete the idempotency index key (if known)
        if (entity is not null
            && entity.IdempotencyScope is not null
            && entity.IdempotencyKey is not null)
        {
            var idmpKey = GetIdempotencyKey(entity.IdempotencyScope, entity.IdempotencyKey);
            await _db.KeyDeleteAsync(idmpKey);
        }

        // Remove from the expiry and confirmed sorted set indexes
        await _db.SortedSetRemoveAsync(GetExpiryIndexKey(), id.ToString());
        await _db.SortedSetRemoveAsync(GetConfirmedIndexKey(), id.ToString());

        _logger.LogDebug(
            "Transaction deleted from Redis. TransactionId: {TransactionId}, KeyExisted: {KeyExisted}",
            id, deleted);

        // The sign-in subject index is not touched here: an id whose key is gone leaves the set on the
        // next GetPendingSignInsAsync of its subject, the same way as after a deletion by TTL.
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the set of the scope's fingerprint, loads the documents via MGET (batch) and keeps the
    /// Pending ones whose scope equals the requested one — the fingerprint narrows the read, the
    /// equality decides. The set is cleaned lazily, here: an id whose key is gone (deleted by TTL, which
    /// does not touch the set) leaves it in a single SREM. An id of a transaction that is stored but no
    /// longer Pending stays — its key dies by TTL and the id follows on a later read. A record this
    /// build cannot read is reported and skipped, and its id stays for a build that reads it.
    /// </remarks>
    public async Task<IReadOnlyList<Transaction>> GetPendingSignInsAsync(
        SignInSubjectScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var indexKey = GetSignInSubjectIndexKey(scope);
        var indexedIds = await _db.SetMembersAsync(indexKey);

        if (indexedIds.Length == 0)
        {
            return Array.Empty<Transaction>();
        }

        var mainKeys = indexedIds
            .Select(id => (RedisKey)GetTransactionKey(id.ToString()))
            .ToArray();
        var rawValues = await _db.StringGetAsync(mainKeys);

        var result = new List<Transaction>(indexedIds.Length);
        var toRemoveFromIndex = new List<RedisValue>();

        for (var i = 0; i < indexedIds.Length; i++)
        {
            // Only a missing key says the record is gone — same rule as in the expiry pass.
            if (rawValues[i].IsNull)
            {
                toRemoveFromIndex.Add(indexedIds[i]);
                continue;
            }

            var transaction = ReadIndexedSignIn(indexedIds[i], rawValues[i]);

            if (transaction is { State: TransactionState.Pending }
                && SignInSubjectScope.Of(transaction) == scope)
            {
                result.Add(transaction);
            }
        }

        if (toRemoveFromIndex.Count > 0)
        {
            await _db.SetRemoveAsync(indexKey, toRemoveFromIndex.ToArray());
        }

        return result.AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses the expiry sorted set index to efficiently find expired transactions.
    /// Loads entities via MGET (batch), then decides on every record on its own — the three outcomes
    /// are <see cref="IndexedRecordOutcome"/>. Removing an id from the index is the right of exactly two
    /// of them: the record is gone, or the record is understood and the pass can do nothing with it.
    /// "This build could not read the record" is not one of them: that is a property of the build, not
    /// of the record, and the removal is irreversible (only AddAsync ever puts an id back), so the id
    /// stays in the shared index for a build that reads it. The ids to remove leave in a single ZREM.
    /// </remarks>
    public async Task<IReadOnlyList<Transaction>> GetExpiredAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        // ZRANGEBYSCORE: all elements with score (ExpiresAt) <= now
        var expiryIndexKey = GetExpiryIndexKey();
        var expiredIds = await _db.SortedSetRangeByScoreAsync(
            expiryIndexKey,
            start: 0,
            stop: now.ToUnixTimeSeconds(),
            take: batchSize);

        if (expiredIds.Length == 0)
        {
            return Array.Empty<Transaction>();
        }

        // MGET: load all entities with a single batch request
        var mainKeys = expiredIds
            .Select(id => (RedisKey)GetTransactionKey(id.ToString()))
            .ToArray();
        var rawValues = await _db.StringGetAsync(mainKeys);

        var result = new List<Transaction>(expiredIds.Length);
        var toRemoveFromIndex = new List<RedisValue>();

        for (var i = 0; i < expiredIds.Length; i++)
        {
            switch (DecideOnIndexedRecord(expiredIds[i], rawValues[i], ExpirySweep))
            {
                case (IndexedRecordOutcome.Take, { } transaction):
                    result.Add(transaction);
                    break;

                case (IndexedRecordOutcome.DropFromIndex, _):
                    toRemoveFromIndex.Add(expiredIds[i]);
                    break;

                    // LeaveForAnotherBuild: the id stays in the index, already reported by the decision.
            }
        }

        // Batch-remove stale records from the index with a single ZREM call
        if (toRemoveFromIndex.Count > 0)
        {
            await _db.SortedSetRemoveAsync(expiryIndexKey, toRemoveFromIndex.ToArray());
        }

        return result.AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Redis manages the key lifecycle via TTL.
    /// This method always returns an empty list.
    /// </remarks>
    public Task<IReadOnlyList<Transaction>> GetStaleTerminalAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        // Redis TTL deletes stale keys natively — no explicit cleanup is needed
        return Task.FromResult<IReadOnlyList<Transaction>>(Array.Empty<Transaction>());
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses the confirmed sorted set index: every write that leaves a transaction Confirmed puts its id
    /// there, scored by the UpdatedAt of the written document in Unix milliseconds, and the write of a
    /// terminal state takes it out. Reads the ids scored strictly below <paramref name="confirmedBefore"/>,
    /// loads the entities via MGET (batch) and decides on every record by the rules of the expiry pass
    /// (<see cref="IndexedRecordOutcome"/>): the id leaves the index when the record is gone or is read in
    /// a known state other than Confirmed, and stays when this build cannot read the record. The stored
    /// document decides, not the score: a confirmed record whose UpdatedAt is not before
    /// <paramref name="confirmedBefore"/> (its score lagged behind a failed re-add) is not returned and
    /// keeps its place. The ids to remove leave in a single ZREM. A transaction confirmed by a build
    /// that did not write this index is not in it and lives to the deadline of its key.
    /// </remarks>
    public async Task<IReadOnlyList<Transaction>> GetStalledConfirmedAsync(
        DateTimeOffset confirmedBefore,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        // ZRANGEBYSCORE: elements with score (UpdatedAt, ms) < confirmedBefore. A score floors the moment
        // to the millisecond, so a score below the floor of confirmedBefore never names a later moment.
        var confirmedIndexKey = GetConfirmedIndexKey();
        var stalledIds = await _db.SortedSetRangeByScoreAsync(
            confirmedIndexKey,
            start: double.NegativeInfinity,
            stop: confirmedBefore.ToUnixTimeMilliseconds(),
            exclude: Exclude.Stop,
            take: batchSize);

        if (stalledIds.Length == 0)
        {
            return Array.Empty<Transaction>();
        }

        // MGET: load all entities with a single batch request
        var mainKeys = stalledIds
            .Select(id => (RedisKey)GetTransactionKey(id.ToString()))
            .ToArray();
        var rawValues = await _db.StringGetAsync(mainKeys);

        var result = new List<Transaction>(stalledIds.Length);
        var toRemoveFromIndex = new List<RedisValue>();

        for (var i = 0; i < stalledIds.Length; i++)
        {
            switch (DecideOnIndexedRecord(stalledIds[i], rawValues[i], FinalizationSweep))
            {
                case (IndexedRecordOutcome.Take, { } transaction) when transaction.UpdatedAt < confirmedBefore:
                    result.Add(transaction);
                    break;

                case (IndexedRecordOutcome.DropFromIndex, _):
                    toRemoveFromIndex.Add(stalledIds[i]);
                    break;

                    // Confirmed but not yet past the threshold, or LeaveForAnotherBuild: the id stays in the index.
            }
        }

        if (toRemoveFromIndex.Count > 0)
        {
            await _db.SortedSetRemoveAsync(confirmedIndexKey, toRemoveFromIndex.ToArray());
        }

        return result.AsReadOnly();
    }

    /// <inheritdoc />
    public Task<bool> TryMarkHopObservationAsync(TransactionId id, HopObservationMark mark, DateTimeOffset markedAt, CancellationToken cancellationToken = default)
    {
        HopObservationArguments.ThrowIfUndefined(mark);

        var field = mark switch
        {
            HopObservationMark.FirstResolve => nameof(TransactionEntity.HopFirstResolvedAt),
            HopObservationMark.InterstitialOpened => nameof(TransactionEntity.HopInterstitialOpenedAt),
            HopObservationMark.ChannelLaunched => nameof(TransactionEntity.HopChannelLaunchedAt),
            HopObservationMark.RepeatResolve => nameof(TransactionEntity.HopRepeatResolvedAt),
            _ => throw new UnreachableException()
        };

        // The round-trip form is what the entity deserializer reads back as a DateTimeOffset.
        return WriteObservationOnceAsync(id, field, markedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public Task<bool> TryAttachScannerContextAsync(TransactionId id, ScannerContextSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        HopObservationArguments.ThrowIfInvalid(snapshot);

        return WriteObservationOnceAsync(
            id,
            nameof(TransactionEntity.ScannerContextSnapshotJson),
            TransactionEntityMapper.SerializeScannerContext(snapshot));
    }

    /// <inheritdoc />
    public Task<bool> TryFillScannerFingerprintAsync(TransactionId id, string fingerprint, CancellationToken cancellationToken = default)
    {
        HopObservationArguments.ThrowIfInvalidFingerprint(fingerprint);

        return WriteObservationOnceAsync(
            id,
            nameof(TransactionEntity.ScannerFingerprint),
            fingerprint,
            requiredField: nameof(TransactionEntity.ScannerContextSnapshotJson));
    }

    /// <inheritdoc />
    public async Task<bool> TrySetWebStepSurfaceAsync(
        TransactionId id,
        WebStepSurface? expected,
        WebStepSurface surface,
        CancellationToken cancellationToken = default)
    {
        ClaimCompletionWrites.ThrowIfUndefined(expected, surface);

        if (!ClaimCompletionWrites.IsSurfaceMove(expected, surface))
        {
            return false;
        }

        var result = await _db.ScriptEvaluateAsync(
            WriteClaimCompletionFieldLuaScript,
            new RedisKey[] { GetTransactionKey(id.ToString()) },
            new RedisValue[]
            {
                nameof(TransactionEntity.ClaimCompletionSurface),
                surface.ToString(),
                expected?.ToString() ?? string.Empty,
                PendingStateName,
                nameof(TransactionEntity.ClaimCompletionJson)
            });

        return (long)result == 1L;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One run of <see cref="WriteClaimCompletionFieldLuaScript"/> on the fingerprint field while it is
    /// empty: the state, the completion mark and the emptiness are checked, and the field written, in one
    /// step.
    /// </remarks>
    public async Task<bool> TrySetClaimFormTokenHashAsync(
        TransactionId id,
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        ClaimCompletionWrites.ThrowIfInvalidFormTokenHash(tokenHash);

        var result = await _db.ScriptEvaluateAsync(
            WriteClaimCompletionFieldLuaScript,
            new RedisKey[] { GetTransactionKey(id.ToString()) },
            new RedisValue[]
            {
                nameof(TransactionEntity.ClaimCompletionFormTokenHash),
                tokenHash,
                string.Empty,
                PendingStateName,
                nameof(TransactionEntity.ClaimCompletionJson)
            });

        return (long)result == 1L;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The outcomes are one field of the document, so the write is a compare-and-swap over it: the
    /// document is read, the condition is checked on what was read, and the script writes the new list
    /// only while the state is still Pending and both the mark and the list are exactly the ones read.
    /// Losing the swap means another write changed the document in between; the loop reads again and
    /// decides anew. It ends: every lost swap is a write that did go through, and the writes that can
    /// change these fields are bounded — an outcome per missing claim, the state leaving Pending.
    /// </remarks>
    public async Task<bool> TryRecordClaimResultAsync(
        TransactionId id,
        ClaimCompletionResult result,
        CancellationToken cancellationToken = default)
    {
        ClaimCompletionWrites.ThrowIfInvalid(result);

        var mainKey = GetTransactionKey(id.ToString());

        while (true)
        {
            var entity = await GetEntityAsync(mainKey);
            if (entity is null)
            {
                return false;
            }

            var results = TransactionEntityMapper.DeserializeClaimCompletionResults(entity.ClaimCompletionResultsJson);

            if (!Enum.TryParse<TransactionState>(entity.State, out var state)
                || !ClaimCompletionWrites.CanRecord(
                    state,
                    TransactionEntityMapper.DeserializeClaimCompletionItems(entity.ClaimCompletionJson),
                    results,
                    result.Claim))
            {
                return false;
            }

            var newResultsJson = TransactionEntityMapper.SerializeClaimCompletionResults(
                ClaimCompletionWrites.Append(results, result));

            var written = await _db.ScriptEvaluateAsync(
                WriteClaimCompletionFieldLuaScript,
                new RedisKey[] { mainKey },
                new RedisValue[]
                {
                    nameof(TransactionEntity.ClaimCompletionResultsJson),
                    newResultsJson,
                    entity.ClaimCompletionResultsJson ?? string.Empty,
                    PendingStateName,
                    nameof(TransactionEntity.ClaimCompletionJson),
                    entity.ClaimCompletionJson
                });

            if ((long)written == 1L)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Writes one hop observation field of the transaction document once, through
    /// <see cref="WriteObservationOnceLuaScript"/>.
    /// </summary>
    /// <param name="id">Transaction identifier.</param>
    /// <param name="field">Document field to write.</param>
    /// <param name="value">Value to write.</param>
    /// <param name="requiredField">Document field that must already be filled, if any.</param>
    /// <returns>true — written by this call; false — already filled, required field empty, or no transaction.</returns>
    private async Task<bool> WriteObservationOnceAsync(
        TransactionId id,
        string field,
        string value,
        string? requiredField = null)
    {
        var arguments = requiredField is null
            ? new RedisValue[] { field, value }
            : new RedisValue[] { field, value, requiredField };

        var result = await _db.ScriptEvaluateAsync(
            WriteObservationOnceLuaScript,
            new RedisKey[] { GetTransactionKey(id.ToString()) },
            arguments);

        return (long)result == 1L;
    }

    /// <summary>
    /// Brings the expiry and confirmed indexes in line with the state just written. A confirmed
    /// transaction is (re)added to the confirmed index with the UpdatedAt of the written document, so the
    /// score follows the document the way the EF Core selection does; a terminal one leaves both indexes:
    /// no event of the cleanup pass is owed to it any more, and TTL deletes the key by its new deadline.
    /// Runs after the transaction itself is written and is not atomic with that write — the index keys
    /// and the transaction key sit in different Redis Cluster slots, so one script cannot hold them.
    /// </summary>
    /// <param name="transactionId">String transaction identifier.</param>
    /// <param name="entity">Entity that has just been written.</param>
    private async Task IndexStoredStateAsync(string transactionId, TransactionEntity entity)
    {
        if (entity.State == ConfirmedStateName)
        {
            // Not added: the finalization pass does not see the transaction, and it lives to the
            // deadline of its key.
            await WriteIndexAfterStoredTransitionAsync(
                () => _db.SortedSetAddAsync(GetConfirmedIndexKey(), transactionId, entity.UpdatedAt.ToUnixTimeMilliseconds()),
                FinalizationSweep,
                transactionId);
        }
        else if (IsTerminalState(entity.State))
        {
            // Not removed: each pass drops the id of a record it reads in a state it does not act on.
            // The two removals are independent and are sent together, so they cost one round trip.
            await Task.WhenAll(
                WriteIndexAfterStoredTransitionAsync(
                    () => _db.SortedSetRemoveAsync(GetExpiryIndexKey(), transactionId),
                    ExpirySweep,
                    transactionId),
                WriteIndexAfterStoredTransitionAsync(
                    () => _db.SortedSetRemoveAsync(GetConfirmedIndexKey(), transactionId),
                    FinalizationSweep,
                    transactionId));
        }
    }

    /// <summary>
    /// Runs one index write that follows a stored transition. A failure is logged and swallowed: the
    /// transition is already stored, and a throw here would report it to the caller as failed.
    /// </summary>
    /// <param name="write">The index write.</param>
    /// <param name="sweep">Pass whose index the write goes to.</param>
    /// <param name="transactionId">String transaction identifier.</param>
    private async Task WriteIndexAfterStoredTransitionAsync(Func<Task> write, IndexedSweep sweep, string transactionId)
    {
        try
        {
            await write();
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _logger.LogWarning(
                failure,
                "Writing the {Index} after a stored transition failed; the transition stands. TransactionId: {TransactionId}",
                sweep.IndexName,
                transactionId);
        }
    }

    /// <summary>
    /// Puts the transaction into the set of its sign-in subject, if it is a pending sign-in with a
    /// scope. Runs after the transaction itself is written and is not atomic with that write: if it
    /// does not run, the sign-in is simply not found by <see cref="GetPendingSignInsAsync"/> and lives
    /// to its deadline. Only Pending is indexed — it is the one state the read returns, so an id added
    /// in any other state would only wait in the set for its key to die.
    /// <para>
    /// A failure of the index write is logged and swallowed: the transaction is already stored, and a
    /// throw here would report that write to the caller as failed. The next write that leaves the
    /// transaction Pending adds the id again.
    /// </para>
    /// </summary>
    /// <param name="transaction">Transaction that has just been written.</param>
    /// <param name="keyTtlSeconds">Seconds the transaction key has left to live.</param>
    private async Task IndexSignInSubjectAsync(Transaction transaction, long keyTtlSeconds)
    {
        if (transaction.State is not TransactionState.Pending
            || keyTtlSeconds <= 0
            || SignInSubjectScope.Of(transaction) is not { } scope)
        {
            return;
        }

        try
        {
            await _db.ScriptEvaluateAsync(
                IndexSignInSubjectLuaScript,
                new RedisKey[] { GetSignInSubjectIndexKey(scope) },
                new RedisValue[] { transaction.Id.ToString(), keyTtlSeconds });
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // The index key is not logged: it is derived from the user's identifier in the channel.
            _logger.LogWarning(
                failure,
                "Adding a pending sign-in to the sign-in subject index failed; the sign-in is not superseded by a newer one and lives to its deadline. TransactionId: {TransactionId}",
                transaction.Id.ToString());
        }
    }

    /// <summary>
    /// Rehydrates one record found through the sign-in subject index. Nothing here throws: one
    /// unreadable record must not hide the other sign-ins of the subject from the caller.
    /// The raw value is never logged — a stored record holds identity snapshots and client context.
    /// </summary>
    /// <param name="transactionId">Identifier as it stands in the index.</param>
    /// <param name="rawValue">Raw value of the transaction key from the batch read (the key exists).</param>
    /// <returns>The transaction, or null when this build cannot read the record.</returns>
    private Transaction? ReadIndexedSignIn(RedisValue transactionId, RedisValue rawValue)
    {
        try
        {
            var entity = rawValue.Length() == 0
                ? null
                : JsonSerializer.Deserialize<TransactionEntity>(
                    rawValue.ToString(),
                    TransactionEntityMapper.SerializerOptions);

            if (entity is not null)
            {
                return TransactionEntityMapper.ToDomain(entity);
            }

            _logger.LogWarning(
                "A record of the sign-in subject index holds no transaction document and is skipped. TransactionId: {TransactionId}",
                transactionId.ToString());
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            _logger.LogWarning(
                failure,
                "A record of the sign-in subject index does not rehydrate on this build and is skipped. TransactionId: {TransactionId}",
                transactionId.ToString());
        }

        return null;
    }

    /// <summary>
    /// Decides what a cleanup pass may do with one record of its index, and rehydrates the transaction
    /// when the outcome is to take it. Both indexes go through here, so a record leaves either of them by
    /// the same rules. Every way this build can fail to read a stored
    /// record — an empty value, unreadable JSON, the JSON literal null, a state name it does not know,
    /// a field its mapper rejects — ends in the same outcome: reported and left where it is. Only the
    /// absence of the key itself says the record is gone. Nothing here throws:
    /// a throw on one unreadable record would take down the whole pass, and with it every transaction
    /// of the batch, on every interval until the key dies by TTL.
    /// </summary>
    /// <param name="transactionId">Identifier as it stands in the index.</param>
    /// <param name="rawValue">Raw value of the transaction key from the batch read.</param>
    /// <param name="sweep">Pass the index belongs to.</param>
    /// <returns>The outcome, with the rehydrated transaction when the outcome is Take.</returns>
    private (IndexedRecordOutcome Outcome, Transaction? Transaction) DecideOnIndexedRecord(
        RedisValue transactionId,
        RedisValue rawValue,
        IndexedSweep sweep)
    {
        // The key died by TTL before the pass got to it: there is no record left to act on, and the
        // index entry is all that remains of it. Only a missing key means that — a live key holding
        // an empty value is a record this build cannot read, not a record that is gone.
        if (rawValue.IsNull)
        {
            return (IndexedRecordOutcome.DropFromIndex, null);
        }

        if (rawValue.Length() == 0)
        {
            return LeaveForABuildThatReadsIt(transactionId, sweep, "the stored value is empty", state: null);
        }

        TransactionEntity? entity;

        try
        {
            entity = JsonSerializer.Deserialize<TransactionEntity>(
                rawValue.ToString(),
                TransactionEntityMapper.SerializerOptions);
        }
        catch (JsonException failure)
        {
            return LeaveForABuildThatReadsIt(transactionId, sweep, "the stored value is not readable JSON", state: null, failure);
        }

        if (entity is null)
        {
            // The key is alive and holds the JSON literal null: the record is there, this build just
            // cannot make a transaction of it.
            return LeaveForABuildThatReadsIt(transactionId, sweep, "the stored value is the JSON literal null", state: null);
        }

        // The state is read off the stored record before any rehydration, and it splits three ways.
        // Not a name of the enum, or a value the enum does not define (a bare number in the record
        // parses into an undefined member): this build does not know the state, and the record is not
        // its to drop.
        if (!Enum.TryParse<TransactionState>(entity.State, out var state) || !Enum.IsDefined(state))
        {
            return LeaveForABuildThatReadsIt(transactionId, sweep, "the stored state is not a state this build knows", entity.State);
        }

        // Known, and past the part of the lifecycle the pass acts on — for the expiry pass terminal or
        // confirmed (which states a deadline still ends is the transition table's to say, and every
        // store asks it the same way), for the finalization pass anything but Confirmed. Its key
        // outlives that moment by at least the retention window, and left in the index it would come back to
        // every pass of that window, first in score order, taking the batch slots of the transactions
        // the pass has to act on.
        if (!sweep.TakenStates.Contains(state))
        {
            return (IndexedRecordOutcome.DropFromIndex, null);
        }

        try
        {
            return (IndexedRecordOutcome.Take, TransactionEntityMapper.ToDomain(entity));
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // The state was known, some other field of the record is not: same class, same outcome.
            return LeaveForABuildThatReadsIt(transactionId, sweep, "the stored record does not rehydrate on this build", entity.State, failure);
        }
    }

    /// <summary>
    /// Reports a record this build could not read and leaves it in the shared index.
    /// The report is a warning on every pass: the record is a genuine anomaly, and the number of passes
    /// it can be seen by is bounded by the TTL of its key. The raw value is never logged — a stored
    /// record holds identity snapshots and client context.
    /// </summary>
    /// <param name="transactionId">Identifier as it stands in the index.</param>
    /// <param name="sweep">Pass whose index the record was read from.</param>
    /// <param name="reason">What about the record this build could not read.</param>
    /// <param name="state">Stored state string, when the record got far enough to have one.</param>
    /// <param name="failure">Failure that ended the reading, when there was one.</param>
    /// <returns>The LeaveForAnotherBuild outcome, without a transaction.</returns>
    private (IndexedRecordOutcome Outcome, Transaction? Transaction) LeaveForABuildThatReadsIt(
        RedisValue transactionId,
        IndexedSweep sweep,
        string reason,
        string? state,
        Exception? failure = null)
    {
        _logger.LogWarning(
            failure,
            "A cleanup pass cannot read a record of the {Index} and leaves it there for a build that can. TransactionId: {TransactionId}, Reason: {Reason}, State: {State}",
            sweep.IndexName,
            transactionId.ToString(),
            reason,
            state);

        return (IndexedRecordOutcome.LeaveForAnotherBuild, null);
    }

    /// <summary>
    /// Builds the key for the main transaction record.
    /// </summary>
    /// <param name="transactionId">String transaction identifier.</param>
    /// <returns>Redis key.</returns>
    private string GetTransactionKey(string transactionId)
    {
        return string.Concat(_options.KeyPrefix, transactionId);
    }

    /// <summary>
    /// Builds the key for the idempotency index.
    /// </summary>
    /// <param name="scope">Idempotency scope.</param>
    /// <param name="key">Idempotency key.</param>
    /// <returns>Redis index key.</returns>
    private string GetIdempotencyKey(string scope, string key)
    {
        return string.Concat(
            _options.KeyPrefix,
            IdempotencyIndexSegment,
            scope,
            KeySegmentSeparator,
            key);
    }

    /// <summary>
    /// Builds the key for the sign-in subject index: the store prefix, the index segment and the
    /// fingerprint of the scope. The fingerprint, not the scope fields, goes into the key — the
    /// channel user identifier does not appear in key names.
    /// </summary>
    /// <param name="scope">Sign-in subject scope.</param>
    /// <returns>Redis set key.</returns>
    private string GetSignInSubjectIndexKey(SignInSubjectScope scope)
    {
        return string.Concat(_options.KeyPrefix, SignInSubjectIndexSegment, scope.ToFingerprint());
    }

    /// <summary>
    /// Builds the key for the expiry sorted set index.
    /// </summary>
    /// <returns>Redis sorted set key.</returns>
    private string GetExpiryIndexKey()
    {
        return string.Concat(_options.KeyPrefix, ExpiryIndexSegment);
    }

    /// <summary>
    /// Builds the key for the confirmed sorted set index.
    /// </summary>
    /// <returns>Redis sorted set key.</returns>
    private string GetConfirmedIndexKey()
    {
        return string.Concat(_options.KeyPrefix, ConfirmedIndexSegment);
    }

    /// <summary>
    /// Computes the effective Unix timestamp for EXPIREAT: the moment the key is deleted, which is
    /// never the moment the transaction expires. A dead transaction — expired or terminal — stays
    /// readable for <c>CompletedRetentionSeconds</c> after its death is recorded; the key of a
    /// non-terminal one outlives the moment the cleanup pass owes it a transition by the pass margin
    /// <c>M = max(CleanupIntervalSeconds, CleanupErrorRetryDelaySeconds) + CompletedRetentionSeconds</c>
    /// (<see cref="GetPassMarginSeconds"/>).
    /// <list type="bullet">
    /// <item>Confirmed: <c>max(ExpiresAt + M, UpdatedAt + Fmax + M)</c>, where <c>Fmax</c> is the upper
    /// bound of ConfirmationFinalizationTimeoutSeconds (<see cref="MaxConfirmationFinalizationTimeoutSeconds"/>).
    /// The deadline no longer ends a confirmed transaction, the end of its finalization window does
    /// (SPEC-001 §8.3 item 3): the cleanup pass reads it back through the confirmed index and moves it to
    /// Failed, so the key outlives that end by the same margin. A confirmation late in the transaction's
    /// life would otherwise lose its key before the pass gets to it.</item>
    /// <item>Other non-terminal: <c>ExpiresAt + M</c>. The transaction is dead at <c>ExpiresAt</c>, but it
    /// is the cleanup pass that records that — reads it back through the expiry index, moves it to Expired
    /// and publishes TransactionExpiredEvent — and the pass runs on its own interval, so the key has to wait
    /// for it. Readers in that window get a non-terminal transaction whose <c>ExpiresAt</c> is in the past,
    /// exactly what the in-memory and EF Core stores return.</item>
    /// <item>Terminal: <c>max(ExpiresAt, UpdatedAt + CompletedRetentionSeconds)</c>, so consumers have
    /// time to read the final status.</item>
    /// </list>
    /// </summary>
    /// <param name="entity">Transaction entity.</param>
    /// <returns>Unix timestamp of the key deletion moment.</returns>
    private long ComputeEffectiveExpiryTimestamp(TransactionEntity entity)
    {
        var baseExpiry = entity.ExpiresAt.ToUnixTimeSeconds();

        if (entity.State == ConfirmedStateName)
        {
            var passMarginSecs = GetPassMarginSeconds();
            var finalizationExpiry = entity.UpdatedAt
                .AddSeconds(MaxConfirmationFinalizationTimeoutSeconds + passMarginSecs)
                .ToUnixTimeSeconds();

            return Math.Max(baseExpiry + passMarginSecs, finalizationExpiry);
        }

        if (!IsTerminalState(entity.State))
        {
            return baseExpiry + GetPassMarginSeconds();
        }

        var retentionExpiry = entity.UpdatedAt.AddSeconds(GetRetentionSeconds()).ToUnixTimeSeconds();

        return Math.Max(baseExpiry, retentionExpiry);
    }

    /// <summary>
    /// Reads the retention window of a dead transaction — completed, failed or expired alike — from
    /// the engine configuration: how long the record stays readable after its death is recorded. A
    /// terminal record counts it from the write of the terminal state; a record the cleanup pass still
    /// has to move counts it from the start of that pass, as the last part of the pass margin
    /// (<see cref="GetPassMarginSeconds"/>). One setting for both kinds of death: it is the same "keep it
    /// readable long enough for the outcome to be seen" window, and nothing in this store gives the two a
    /// different length.
    /// </summary>
    /// <returns>Retention in seconds.</returns>
    private int GetRetentionSeconds()
    {
        return _engineOptions.CurrentValue.CompletedRetentionSeconds;
    }

    /// <summary>
    /// Computes the pass margin: how long the key of a non-terminal transaction outlives the moment the
    /// cleanup pass owes it a transition — <c>max(CleanupIntervalSeconds, CleanupErrorRetryDelaySeconds) +
    /// CompletedRetentionSeconds</c>. The next pass starts at most <c>CleanupIntervalSeconds</c> after the
    /// previous one ends, and at most <c>CleanupErrorRetryDelaySeconds</c> after one cut short by an
    /// exception; the record outlives that start by the retention window, which also covers the length of
    /// the pass and the tail of its batch (SPEC-001 §8.3). The values are read at the time of the write, so
    /// an interval raised later by more than the retention window can end a key written before the change
    /// ahead of the pass.
    /// </summary>
    /// <returns>Pass margin in seconds.</returns>
    private int GetPassMarginSeconds()
    {
        var engineOptions = _engineOptions.CurrentValue;

        return Math.Max(engineOptions.CleanupIntervalSeconds, engineOptions.CleanupErrorRetryDelaySeconds)
            + engineOptions.CompletedRetentionSeconds;
    }

    /// <summary>
    /// Returns true if the given string state name is terminal.
    /// Terminal states: Completed, Failed, Expired.
    /// </summary>
    /// <param name="state">String state name.</param>
    /// <returns>true — the state is terminal.</returns>
    private static bool IsTerminalState(string state)
    {
        return state is nameof(TransactionState.Completed)
            or nameof(TransactionState.Failed)
            or nameof(TransactionState.Expired);
    }

    /// <summary>
    /// Computes the TTL in seconds (integer) for use in Redis Lua scripts (EX/EXPIREAT).
    /// </summary>
    /// <param name="expiresAt">Transaction expiration moment.</param>
    /// <returns>TTL in seconds (≥1), or 0 if the transaction has already expired.</returns>
    private long GetExpirySecs(DateTimeOffset expiresAt)
    {
        var ttlSecs = (expiresAt - _timeProvider.GetUtcNow()).TotalSeconds;
        return ttlSecs > 0 ? (long)Math.Ceiling(ttlSecs) : 0L;
    }

    /// <summary>
    /// Gets and deserializes a transaction entity from Redis by key.
    /// </summary>
    /// <param name="key">Redis key.</param>
    /// <returns>Transaction entity, or null if not found.</returns>
    private async Task<TransactionEntity?> GetEntityAsync(string key)
    {
        // Get the JSON from Redis and deserialize into TransactionEntity
        var value = await _db.StringGetAsync(key);

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize<TransactionEntity>(
            value.ToString(),
            TransactionEntityMapper.SerializerOptions);
    }

    /// <summary>
    /// Serializes a TransactionEntity into a JSON string for storage in Redis.
    /// </summary>
    /// <param name="entity">Transaction entity.</param>
    /// <returns>JSON string.</returns>
    private static string SerializeEntity(TransactionEntity entity)
    {
        return JsonSerializer.Serialize(entity, TransactionEntityMapper.SerializerOptions);
    }

    /// <summary>
    /// What a cleanup pass may do with one record it found in its index. Each index is shared by every
    /// instance working against the same Redis, so an id removed from it is acted on by no instance
    /// ever again — which is why "read it" and "drop it" are not the same decision.
    /// </summary>
    private enum IndexedRecordOutcome
    {
        /// <summary>
        /// The record is read and in a state the pass acts on (expiry: can still become Expired;
        /// finalization: still Confirmed): it goes to the result of the pass.
        /// </summary>
        Take,

        /// <summary>
        /// The pass has nothing left to do with the record — it is gone, or it is read and past the
        /// part of the lifecycle the pass acts on. Its id leaves the index.
        /// </summary>
        DropFromIndex,

        /// <summary>
        /// This build could not read the record. That says something about the build, not about the
        /// record, so the id stays in the index for a build that reads it.
        /// </summary>
        LeaveForAnotherBuild
    }

    /// <summary>
    /// A cleanup pass that reads its records through a sorted set index of this store.
    /// </summary>
    /// <param name="IndexName">Name of the index in log messages.</param>
    /// <param name="TakenStates">States of a record the pass acts on; a record read in any other known
    /// state leaves the index.</param>
    private sealed record IndexedSweep(string IndexName, IReadOnlySet<TransactionState> TakenStates);
}
