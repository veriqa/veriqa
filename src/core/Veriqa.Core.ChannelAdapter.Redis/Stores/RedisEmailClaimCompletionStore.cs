// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

using Microsoft.Extensions.Options;

using StackExchange.Redis;

using Veriqa.Core.ChannelAdapter.Email.ClaimCompletion;
using Veriqa.Core.ChannelAdapter.Email.Services;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Redis.Stores;

/// <summary>
/// Redis implementation of the store of the email completion (SPEC-041 R12, L50, E61): a code sent by one
/// replica is checked by another, and the mail counters are shared by every replica.
/// </summary>
/// <remarks>
/// <para>
/// The record of a transaction is one hash that expires with the transaction; each part of it — the code,
/// the link, the chosen method, the Push token, the code of the sign-in mail — is written by one <c>HSET</c>
/// of all its fields, so a part is replaced whole. The code of the sign-in mail has fields of its own, apart
/// from those of the code of the completion. A link is also indexed by the fingerprint of its token, with the lifetime of
/// the link, which is how the confirmation page finds its transaction.
/// </para>
/// <para>
/// The operations that decide something run as Lua scripts, each one indivisible step on the server: the
/// reservation of a mail judges every counter before it counts any, the spending of an attempt is a
/// compare-and-swap on the code that was read, and the use of a link sets its "used" field only once.
/// A counter is a plain integer key whose TTL is its window, set by the mail that opens the window.
/// </para>
/// </remarks>
internal sealed class RedisEmailClaimCompletionStore : IEmailClaimCompletionStore
{
    /// <summary>
    /// Key segment of the records of the transactions.
    /// </summary>
    private const string RecordSegment = "tx:";

    /// <summary>
    /// Key segment of the index "fingerprint of a link token → transaction".
    /// </summary>
    private const string LinkSegment = "link:";

    /// <summary>
    /// Key segment of the mail counters.
    /// </summary>
    private const string CounterSegment = "count:";

    /// <summary>
    /// Hash field: the chosen method, by name.
    /// </summary>
    private const string SelectedMethodField = "SelectedMethod";

    /// <summary>
    /// Hash field: fingerprint of the live code.
    /// </summary>
    private const string CodeHashField = "CodeHash";

    /// <summary>
    /// Hash field: address of the live code.
    /// </summary>
    private const string CodeAddressField = "CodeAddress";

    /// <summary>
    /// Hash field: expiry of the live code.
    /// </summary>
    private const string CodeExpiresAtField = "CodeExpiresAt";

    /// <summary>
    /// Hash field: attempts left of the live code.
    /// </summary>
    private const string CodeAttemptsField = "CodeAttempts";

    /// <summary>
    /// Hash field: fingerprint of the live code of the sign-in mail.
    /// </summary>
    private const string SignInCodeHashField = "SignInCodeHash";

    /// <summary>
    /// Hash field: action token of the sign-in mail the live code was sent in.
    /// </summary>
    private const string SignInCodeLetterField = "SignInCodeLetter";

    /// <summary>
    /// Hash field: expiry of the live code of the sign-in mail.
    /// </summary>
    private const string SignInCodeExpiresAtField = "SignInCodeExpiresAt";

    /// <summary>
    /// Hash field: attempts left of the live code of the sign-in mail.
    /// </summary>
    private const string SignInCodeAttemptsField = "SignInCodeAttempts";

    /// <summary>
    /// Hash field: fingerprint of the token of the live link.
    /// </summary>
    private const string LinkTokenHashField = "LinkTokenHash";

    /// <summary>
    /// Hash field: address of the live link.
    /// </summary>
    private const string LinkAddressField = "LinkAddress";

    /// <summary>
    /// Hash field: expiry of the live link.
    /// </summary>
    private const string LinkExpiresAtField = "LinkExpiresAt";

    /// <summary>
    /// Hash field: present — the live link was used.
    /// </summary>
    private const string LinkUsedField = "LinkUsed";

    /// <summary>
    /// Hash field: the correlation token of the Push method.
    /// </summary>
    private const string PushTokenField = "PushToken";

    /// <summary>
    /// Hash field: expiry of the Push correlation.
    /// </summary>
    private const string PushExpiresAtField = "PushExpiresAt";

    /// <summary>
    /// Hash field: the held address.
    /// </summary>
    private const string HeldAddressField = "HeldAddress";

    /// <summary>
    /// Hash field: present — the held address was confirmed.
    /// </summary>
    private const string HeldVerifiedField = "HeldVerified";

    /// <summary>
    /// Hash field: the method that obtained the held address, by name.
    /// </summary>
    private const string HeldMethodField = "HeldMethod";

    /// <summary>
    /// Stored value of a set flag.
    /// </summary>
    private const string FlagSetValue = "1";

    /// <summary>
    /// Entity description used in lifetime errors.
    /// </summary>
    private const string EntityDescription = "Email completion record";

    /// <summary>
    /// Entity description used in lifetime errors of a link index.
    /// </summary>
    private const string LinkEntityDescription = "Email completion link";

    /// <summary>
    /// Lua script reserving one mail against several counters: every counter is judged before any is
    /// counted, so either each counts the mail or none does.
    /// KEYS — the counter keys.
    /// ARGV[2i-1] — limit of counter i; ARGV[2i] — window of counter i in milliseconds.
    /// Returns: 0 — reserved; i — counter i (1-based) has no room left.
    /// </summary>
    /// <remarks>
    /// The window is set only by the mail that creates the counter: a window is fixed from the first mail.
    /// </remarks>
    private const string ReserveLuaScript = @"
for i = 1, #KEYS do
    local count = tonumber(redis.call('GET', KEYS[i]) or '0')
    if count >= tonumber(ARGV[2 * i - 1]) then
        return i
    end
end
for i = 1, #KEYS do
    if redis.call('INCR', KEYS[i]) == 1 then
        redis.call('PEXPIRE', KEYS[i], tonumber(ARGV[2 * i]))
    end
end
return 0";

    /// <summary>
    /// Lua script spending one attempt of a live code — a compare-and-swap on the code that was read. One
    /// script for the code of the completion and the code of the sign-in mail: the fields of the code are
    /// arguments.
    /// KEYS[1] — record hash key.
    /// ARGV[1] — fingerprint of the code that was read; ARGV[2] — attempts it had; ARGV[3] — attempts to set;
    /// ARGV[4] — field of the fingerprint; ARGV[5] — field of the attempts.
    /// Returns: 1 — spent by this call; 0 — the live code is another one, or its attempts changed.
    /// </summary>
    private const string SpendAttemptLuaScript = @"
if redis.call('HGET', KEYS[1], ARGV[4]) ~= ARGV[1] then
    return 0
end
if redis.call('HGET', KEYS[1], ARGV[5]) ~= ARGV[2] then
    return 0
end
redis.call('HSET', KEYS[1], ARGV[5], ARGV[3])
return 1";

    /// <summary>
    /// Lua script using the live link once.
    /// KEYS[1] — link index key (its existence means the link has not expired); KEYS[2] — record hash key.
    /// ARGV[1] — fingerprint of the token.
    /// Returns: the address of the link — used by this call; nil — not the live link, or used before.
    /// </summary>
    private const string UseLinkLuaScript = @"
if redis.call('EXISTS', KEYS[1]) == 0 then
    return false
end
if redis.call('HGET', KEYS[2], 'LinkTokenHash') ~= ARGV[1] then
    return false
end
if redis.call('HSETNX', KEYS[2], 'LinkUsed', '1') == 0 then
    return false
end
return redis.call('HGET', KEYS[2], 'LinkAddress')";

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
    /// Creates the Redis store of the email completion.
    /// </summary>
    /// <param name="connectionMultiplexer">Redis connection multiplexer.</param>
    /// <param name="options">Channel store options.</param>
    /// <param name="timeProvider">Time provider.</param>
    public RedisEmailClaimCompletionStore(
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
    public async Task<EmailClaimCompletionRecord?> GetAsync(string transactionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = await _db.HashGetAllAsync(RecordKey(transactionId));
        if (entries.Length == 0)
        {
            return null;
        }

        var fields = entries.ToDictionary(entry => entry.Name.ToString(), entry => entry.Value, StringComparer.Ordinal);

        return new EmailClaimCompletionRecord(
            Enum.TryParse<EmailCompletionMethod>(fields.GetValueOrDefault(SelectedMethodField).ToString(), out var method)
                ? (EmailCompletionMethod?)method
                : null,
            ReadCode(fields),
            ReadLink(fields),
            ReadPushToken(fields),
            ReadSignInCode(fields),
            ReadHeld(fields));
    }

    /// <inheritdoc />
    public async Task<int?> TryReserveMailAsync(IReadOnlyList<EmailClaimMailCounter> counters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(counters);
        cancellationToken.ThrowIfCancellationRequested();

        var arguments = new List<RedisValue>(counters.Count * 2);
        foreach (var counter in counters)
        {
            arguments.Add(counter.Limit);
            arguments.Add((long)Math.Ceiling(counter.Window.TotalMilliseconds));
        }

        var result = (long)await _db.ScriptEvaluateAsync(
            ReserveLuaScript,
            [.. counters.Select(counter => (RedisKey)CounterKey(counter.Key))],
            [.. arguments]);

        return result == 0L ? null : (int?)((int)result - 1);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ReadMailCountsAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();

        var values = await _db.StringGetAsync(keys.Select(key => (RedisKey)CounterKey(key)).ToArray());

        return [.. values.Select(value => value.IsNullOrEmpty ? 0 : (int)value)];
    }

    /// <inheritdoc />
    public Task SetCodeAsync(
        string transactionId,
        EmailClaimCode code,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);

        return WriteAsync(
            transactionId,
            recordExpiresAt,
            [
                new HashEntry(CodeHashField, code.Hash),
                new HashEntry(CodeAddressField, code.Address),
                new HashEntry(CodeExpiresAtField, RedisChannelStoreTimestamps.Format(code.ExpiresAt)),
                new HashEntry(CodeAttemptsField, code.AttemptsLeft),
            ],
            removedFields: [],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TrySpendCodeAttemptAsync(
        string transactionId,
        string codeHash,
        int attemptsSeen,
        bool matched,
        CancellationToken cancellationToken) =>
        SpendAttemptAsync(transactionId, CodeHashField, CodeAttemptsField, codeHash, attemptsSeen, matched, cancellationToken);

    /// <inheritdoc />
    public Task SetSignInCodeAsync(
        string transactionId,
        EmailSignInCode code,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);

        return WriteAsync(
            transactionId,
            recordExpiresAt,
            [
                new HashEntry(SignInCodeHashField, code.Hash),
                new HashEntry(SignInCodeLetterField, code.LetterToken),
                new HashEntry(SignInCodeExpiresAtField, RedisChannelStoreTimestamps.Format(code.ExpiresAt)),
                new HashEntry(SignInCodeAttemptsField, code.AttemptsLeft),
            ],
            removedFields: [],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TrySpendSignInCodeAttemptAsync(
        string transactionId,
        string codeHash,
        int attemptsSeen,
        bool matched,
        CancellationToken cancellationToken) =>
        SpendAttemptAsync(
            transactionId, SignInCodeHashField, SignInCodeAttemptsField, codeHash, attemptsSeen, matched, cancellationToken);

    /// <inheritdoc />
    public async Task SetLinkAsync(
        string transactionId,
        EmailClaimLink link,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        // The index is written first: a record that already names the new link can be found by its token.
        // The index of the token before it is left to its own TTL — that token no longer matches the record.
        // SET + PEXPIREAT in one MULTI/EXEC: the deadline is absolute, so a delayed command does not extend it.
        var linkKey = LinkKey(link.TokenHash);
        var linkDeadline = RedisRecordLifetime.DeadlineOf(link.ExpiresAt, _timeProvider.GetUtcNow(), LinkEntityDescription);
        var linkTransaction = _db.CreateTransaction();
        var linkSetTask = linkTransaction.StringSetAsync(linkKey, transactionId);
        var linkExpireTask = linkTransaction.KeyExpireAsync(linkKey, linkDeadline.UtcDateTime);

        await linkTransaction.ExecuteAsync();
        await Task.WhenAll(linkSetTask, linkExpireTask);

        await WriteAsync(
            transactionId,
            recordExpiresAt,
            [
                new HashEntry(LinkTokenHashField, link.TokenHash),
                new HashEntry(LinkAddressField, link.Address),
                new HashEntry(LinkExpiresAtField, RedisChannelStoreTimestamps.Format(link.ExpiresAt)),
            ],
            removedFields: [LinkUsedField],
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EmailClaimLinkUse?> FindLiveLinkAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var transactionId = await _db.StringGetAsync(LinkKey(tokenHash));
        if (transactionId.IsNullOrEmpty
            || await GetAsync(transactionId.ToString(), cancellationToken) is not { Link: { } link }
            || !string.Equals(link.TokenHash, tokenHash, StringComparison.Ordinal)
            || !link.IsLive(now))
        {
            return null;
        }

        return new EmailClaimLinkUse(transactionId.ToString(), link.Address);
    }

    /// <inheritdoc />
    public async Task<EmailClaimLinkUse?> TryUseLinkAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The record key is derived from the transaction the index names, so it is read before the script:
        // a script must declare every key it touches. The index never changes its transaction.
        var transactionId = await _db.StringGetAsync(LinkKey(tokenHash));
        if (transactionId.IsNullOrEmpty)
        {
            return null;
        }

        var address = await _db.ScriptEvaluateAsync(
            UseLinkLuaScript,
            [LinkKey(tokenHash), RecordKey(transactionId.ToString())],
            [tokenHash]);

        return address.IsNull ? null : new EmailClaimLinkUse(transactionId.ToString(), address.ToString());
    }

    /// <inheritdoc />
    public Task SetSelectedMethodAsync(
        string transactionId,
        EmailCompletionMethod method,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken) =>
        WriteAsync(
            transactionId,
            recordExpiresAt,
            [new HashEntry(SelectedMethodField, method.ToString())],
            removedFields: [],
            cancellationToken);

    /// <inheritdoc />
    public Task SetPushTokenAsync(
        string transactionId,
        EmailClaimPushToken token,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        return WriteAsync(
            transactionId,
            recordExpiresAt,
            [
                new HashEntry(PushTokenField, token.Token),
                new HashEntry(PushExpiresAtField, RedisChannelStoreTimestamps.Format(token.ExpiresAt)),
            ],
            removedFields: [],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task SetHeldAddressAsync(
        string transactionId,
        EmailClaimHeldAddress? held,
        DateTimeOffset recordExpiresAt,
        CancellationToken cancellationToken) =>
        held is null
            ? WriteAsync(
                transactionId,
                recordExpiresAt,
                entries: [],
                removedFields: [HeldAddressField, HeldVerifiedField, HeldMethodField],
                cancellationToken)
            : WriteAsync(
                transactionId,
                recordExpiresAt,
                held.Verified
                    ? [
                        new HashEntry(HeldAddressField, held.Address),
                        new HashEntry(HeldMethodField, held.Method.ToString()),
                        new HashEntry(HeldVerifiedField, FlagSetValue),
                    ]
                    : [
                        new HashEntry(HeldAddressField, held.Address),
                        new HashEntry(HeldMethodField, held.Method.ToString()),
                    ],
                removedFields: held.Verified ? [] : [HeldVerifiedField],
                cancellationToken);

    /// <summary>
    /// Spends one attempt of a live code by the compare-and-swap script, over the fields of that code.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="hashField">Field of the fingerprint of the code.</param>
    /// <param name="attemptsField">Field of the attempts left of the code.</param>
    /// <param name="codeHash">Fingerprint of the code that was read.</param>
    /// <param name="attemptsSeen">Attempts the code had when it was read.</param>
    /// <param name="matched">Whether the entry matched: a match spends every attempt left.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — the attempt was spent by this call; <c>false</c> — the code changed meanwhile.</returns>
    private async Task<bool> SpendAttemptAsync(
        string transactionId,
        string hashField,
        string attemptsField,
        string codeHash,
        int attemptsSeen,
        bool matched,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _db.ScriptEvaluateAsync(
            SpendAttemptLuaScript,
            [RecordKey(transactionId)],
            [codeHash, FormatCount(attemptsSeen), FormatCount(matched ? 0 : attemptsSeen - 1), hashField, attemptsField]);

        return (long)result == 1L;
    }

    /// <summary>
    /// Writes one part of the record of a transaction in one MULTI/EXEC: its fields, the fields it drops,
    /// and the expiry of the record, which is the expiry of the transaction.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <param name="recordExpiresAt">Moment the transaction expires.</param>
    /// <param name="entries">Fields of the part.</param>
    /// <param name="removedFields">Fields the new part does not have.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    private async Task WriteAsync(
        string transactionId,
        DateTimeOffset recordExpiresAt,
        HashEntry[] entries,
        RedisValue[] removedFields,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = RecordKey(transactionId);
        var deadline = RedisRecordLifetime.DeadlineOf(recordExpiresAt, _timeProvider.GetUtcNow(), EntityDescription);

        var transaction = _db.CreateTransaction();
        var tasks = new List<Task>(3);

        if (removedFields.Length > 0)
        {
            tasks.Add(transaction.HashDeleteAsync(key, removedFields));
        }

        if (entries.Length > 0)
        {
            tasks.Add(transaction.HashSetAsync(key, entries));
        }

        tasks.Add(transaction.KeyExpireAsync(key, deadline.UtcDateTime));

        await transaction.ExecuteAsync();
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Reads the code part of a record; a part missing a field is no code at all.
    /// </summary>
    /// <param name="fields">Fields of the record.</param>
    /// <returns>The code, or null.</returns>
    private static EmailClaimCode? ReadCode(Dictionary<string, RedisValue> fields) =>
        fields.TryGetValue(CodeHashField, out var hash)
        && fields.TryGetValue(CodeAddressField, out var address)
        && RedisChannelStoreTimestamps.Parse(fields.GetValueOrDefault(CodeExpiresAtField)) is { } expiresAt
        && fields.TryGetValue(CodeAttemptsField, out var attempts)
        && attempts.TryParse(out int attemptsLeft)
            ? new EmailClaimCode(hash.ToString(), address.ToString(), expiresAt, attemptsLeft)
            : null;

    /// <summary>
    /// Reads the part of the code of the sign-in mail; a part missing a field is no code at all.
    /// </summary>
    /// <param name="fields">Fields of the record.</param>
    /// <returns>The code, or null.</returns>
    private static EmailSignInCode? ReadSignInCode(Dictionary<string, RedisValue> fields) =>
        fields.TryGetValue(SignInCodeHashField, out var hash)
        && fields.TryGetValue(SignInCodeLetterField, out var letter)
        && RedisChannelStoreTimestamps.Parse(fields.GetValueOrDefault(SignInCodeExpiresAtField)) is { } expiresAt
        && fields.TryGetValue(SignInCodeAttemptsField, out var attempts)
        && attempts.TryParse(out int attemptsLeft)
            ? new EmailSignInCode(hash.ToString(), letter.ToString(), expiresAt, attemptsLeft)
            : null;

    /// <summary>
    /// Reads the link part of a record; a part missing a field is no link at all.
    /// </summary>
    /// <param name="fields">Fields of the record.</param>
    /// <returns>The link, or null.</returns>
    private static EmailClaimLink? ReadLink(Dictionary<string, RedisValue> fields) =>
        fields.TryGetValue(LinkTokenHashField, out var tokenHash)
        && fields.TryGetValue(LinkAddressField, out var address)
        && RedisChannelStoreTimestamps.Parse(fields.GetValueOrDefault(LinkExpiresAtField)) is { } expiresAt
            ? new EmailClaimLink(
                tokenHash.ToString(),
                address.ToString(),
                expiresAt,
                Used: fields.GetValueOrDefault(LinkUsedField) == FlagSetValue)
            : null;

    /// <summary>
    /// Reads the Push part of a record; a part missing a field is no token at all.
    /// </summary>
    /// <param name="fields">Fields of the record.</param>
    /// <returns>The token, or null.</returns>
    private static EmailClaimPushToken? ReadPushToken(Dictionary<string, RedisValue> fields) =>
        fields.TryGetValue(PushTokenField, out var token)
        && RedisChannelStoreTimestamps.Parse(fields.GetValueOrDefault(PushExpiresAtField)) is { } expiresAt
            ? new EmailClaimPushToken(token.ToString(), expiresAt)
            : null;

    /// <summary>
    /// Reads the held address of a record; a part missing a field is no address at all.
    /// </summary>
    /// <param name="fields">Fields of the record.</param>
    /// <returns>The held address, or null.</returns>
    private static EmailClaimHeldAddress? ReadHeld(Dictionary<string, RedisValue> fields) =>
        fields.TryGetValue(HeldAddressField, out var address)
        && Enum.TryParse<EmailCompletionMethod>(fields.GetValueOrDefault(HeldMethodField).ToString(), out var method)
            ? new EmailClaimHeldAddress(address.ToString(), Verified: fields.GetValueOrDefault(HeldVerifiedField) == FlagSetValue, method)
            : null;

    /// <summary>
    /// A count as the scripts compare it — the decimal form Redis keeps an integer field in.
    /// </summary>
    /// <param name="count">The count.</param>
    /// <returns>Its stored form.</returns>
    private static string FormatCount(int count) => count.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds the Redis key of the record of a transaction.
    /// </summary>
    /// <param name="transactionId">Transaction.</param>
    /// <returns>Redis key.</returns>
    private string RecordKey(string transactionId) =>
        string.Concat(_options.EmailClaimCompletionKeyPrefix, RecordSegment, transactionId);

    /// <summary>
    /// Builds the Redis key of the index entry of a link token.
    /// </summary>
    /// <param name="tokenHash">Fingerprint of the token.</param>
    /// <returns>Redis key.</returns>
    private string LinkKey(string tokenHash) =>
        string.Concat(_options.EmailClaimCompletionKeyPrefix, LinkSegment, tokenHash);

    /// <summary>
    /// Builds the Redis key of a mail counter.
    /// </summary>
    /// <param name="counterKey">Opaque key of the counter.</param>
    /// <returns>Redis key.</returns>
    private string CounterKey(string counterKey) =>
        string.Concat(_options.EmailClaimCompletionKeyPrefix, CounterSegment, counterKey);
}
