// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.TransactionEngine.Store.EfCore;

/// <summary>
/// EF Core implementation of the transaction store (provider-agnostic).
/// Works through IDbContextFactory for correct operation in a singleton context.
/// Supports optimistic concurrency via ConcurrencyToken.
/// </summary>
internal sealed class EfCoreTransactionStore : ITransactionStore
{
    // String state names for filtering in database queries.
    // Where the set of states is written out at the call site, it is compared via an explicit
    // disjunction (e.State == ...) rather than collection.Contains(e.State). That pattern is retained
    // from when the MySQL provider (Oracle MySql.EntityFrameworkCore, 10.0.x) was supported: it did
    // not assign a type mapping to the elements of a parameterized string IN list and threw
    // "Expression '@states' in the SQL tree does not have a type mapping assigned" in the background
    // cleanup loop (TransactionCleanupService, TASK-039). MySQL has since been removed (GPL-provider
    // cleanup); Npgsql maps both forms correctly, so the disjunction is kept as-is — reverting it
    // yields no functional benefit.
    // The expiry filter is the one query that does NOT write its states out: it takes them from the
    // domain (ExpirableStateNames below), and a disjunction over a set it does not own would mean
    // copying that set here. Contains over the array is the form that keeps the single source, and
    // it stays a server-side IN — the array is a query parameter, not a collection the query walks.

    /// <summary>
    /// String name of the terminal state Completed.
    /// </summary>
    private static readonly string CompletedStateName = nameof(TransactionState.Completed);

    /// <summary>
    /// String name of the terminal state Expired.
    /// </summary>
    private static readonly string ExpiredStateName = nameof(TransactionState.Expired);

    /// <summary>
    /// String name of the terminal state Failed.
    /// </summary>
    private static readonly string FailedStateName = nameof(TransactionState.Failed);

    /// <summary>
    /// String name of the Confirmed state, used for finding stalled transactions.
    /// </summary>
    private static readonly string ConfirmedStateName = nameof(TransactionState.Confirmed);

    /// <summary>
    /// String name of the Pending state — the one state the claims completion writes go through in.
    /// </summary>
    private static readonly string PendingStateName = nameof(TransactionState.Pending);

    /// <summary>
    /// Stored names of the states the passing of a deadline ends a transaction from, read off
    /// <see cref="TransactionStateMachine.ExpirableStates"/> rather than listed here: a second list
    /// would go on selecting by the old rule after the transition table changed, and the whole point
    /// of the filter is that the two agree. The names are the ones the mapper writes into the column
    /// (<c>TransactionState.ToString()</c>), which is what the query compares against.
    /// </summary>
    private static readonly string[] ExpirableStateNames = TransactionStateMachine.ExpirableStates
        .Select(state => state.ToString())
        .ToArray();

    /// <summary>
    /// SQLSTATE class "integrity constraint violation" per the ANSI standard (prefix 23),
    /// for example PostgreSQL 23505 (unique_violation).
    /// </summary>
    private const string IntegrityConstraintViolationSqlStateClass = "23";

    /// <summary>
    /// Exact SQLSTATE of a unique constraint violation (unique_violation), as PostgreSQL reports it.
    /// </summary>
    private const string UniqueViolationSqlState = "23505";

    /// <summary>
    /// DbContext factory for creating a context per operation.
    /// </summary>
    private readonly IDbContextFactory<TransactionDbContext> _contextFactory;

    /// <summary>
    /// Logger for diagnosing store operations.
    /// </summary>
    private readonly ILogger<EfCoreTransactionStore> _logger;

    /// <summary>
    /// Creates an instance of the EF Core transaction store.
    /// </summary>
    /// <param name="contextFactory">DbContext factory.</param>
    /// <param name="logger">Logger.</param>
    public EfCoreTransactionStore(
        IDbContextFactory<TransactionDbContext> contextFactory,
        ILogger<EfCoreTransactionStore> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A rejected insert is classified by re-reading the database, so duplicates are recognized on
    /// any relational provider: an idempotency pair held by a transaction with another identifier
    /// yields <see cref="DuplicateIdempotencyKeyException"/>, an existing identifier —
    /// <see cref="InvalidOperationException"/>, even when the stored row carries the same pair. When the conflicting row is gone by the re-read
    /// (deleted by cleanup in between), a provider reporting SQLSTATE 23505 (PostgreSQL) still yields
    /// <see cref="DuplicateIdempotencyKeyException"/>; a provider reporting no SQLSTATE (SQL Server in
    /// today's delivery) yields the original exception. When the re-read itself fails, the original
    /// exception is rethrown and the read failure is logged; a cancellation of the re-read propagates.
    /// </remarks>
    public async Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        // Add a new transaction to the database; on a duplicate key — InvalidOperationException
        ArgumentNullException.ThrowIfNull(transaction);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = TransactionEntityMapper.ToEntity(transaction);
        context.Transactions.Add(entity);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug(
                "Transaction saved in EF Core. TransactionId: {TransactionId}, State: {State}",
                transaction.Id, transaction.State);
        }
        catch (DbUpdateException ex) when (IsPossibleIntegrityConstraintViolation(ex))
        {
            Exception? duplicate;
            try
            {
                duplicate = await ClassifyRejectedInsertAsync(transaction, ex, cancellationToken);
            }
            catch (Exception readException) when (readException is not OperationCanceledException)
            {
                // The re-read failed as well (a broken connection, a command timeout, a lock). Its
                // exception describes the read, not the insert, so it must not replace the insert
                // failure: it is logged, and the original exception is rethrown below.
                _logger.LogWarning(
                    readException,
                    "Re-read after a rejected transaction insert failed in EF Core; rethrowing the insert failure. TransactionId: {TransactionId}",
                    transaction.Id);
                duplicate = null;
            }

            if (duplicate is not null)
            {
                throw duplicate;
            }

            // Another integrity violation (NOT NULL, CHECK, FK), another database error of a provider
            // without SQLSTATE, an unprovable race, or a failed re-read — rethrow the original
            // exception, preserving the actual cause for diagnostics instead of wrapping it in a
            // neutral phrasing.
            throw;
        }
    }

    /// <summary>
    /// Classifies a rejected insert by re-reading the database state. Returns the duplicate exception
    /// to throw, or null when the rejection is not a provable duplicate and the original exception
    /// must be rethrown. Exceptions of the re-read itself propagate to the caller.
    /// </summary>
    /// <param name="transaction">The transaction whose insert was rejected.</param>
    /// <param name="insertFailure">The exception of the rejected insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The duplicate exception, or null.</returns>
    private async Task<Exception?> ClassifyRejectedInsertAsync(
        Transaction transaction,
        DbUpdateException insertFailure,
        CancellationToken cancellationToken)
    {
        // Classify the violation by the actual database state, not by the provider's message
        // text or error number (both depend on the provider, its locale and version). By the time
        // of the error, the concurrent row is already committed (the uniqueness check waits for
        // the competitor's commit), so a re-read reliably distinguishes an idempotency conflict
        // from a PK duplicate — on any relational provider.
        if (transaction.IdempotencyScope is not null && transaction.IdempotencyKey is not null)
        {
            var existingByKey = await GetByIdempotencyKeyAsync(
                transaction.IdempotencyScope,
                transaction.IdempotencyKey,
                cancellationToken);

            // The pair is a conflict only when another transaction holds it: re-adding the same
            // transaction finds itself by its own pair, and that is an identifier duplicate,
            // classified below.
            if (existingByKey is not null && existingByKey.Id != transaction.Id)
            {
                _logger.LogWarning(
                    "Idempotency key uniqueness violation in EF Core. TransactionId: {TransactionId}, Scope: {Scope}, KeyFingerprint: {KeyFingerprint}",
                    transaction.Id,
                    transaction.IdempotencyScope,
                    IdempotencyKeyFingerprint.Compute(transaction.IdempotencyScope, transaction.IdempotencyKey));

                // Idempotency unique index violation: a race during concurrent creation
                return new DuplicateIdempotencyKeyException(
                    transaction.IdempotencyScope,
                    transaction.IdempotencyKey,
                    insertFailure);
            }
        }

        var existingById = await GetByIdAsync(transaction.Id, cancellationToken);
        if (existingById is not null)
        {
            // Primary key violation — an exact TransactionId duplicate
            return new InvalidOperationException(
                $"Transaction with ID '{transaction.Id}' already exists in the store", insertFailure);
        }

        // The conflicting row was not found on the re-read. If the provider reports this as
        // definitely a uniqueness violation (SQLSTATE 23505) and idempotency keys are set — the
        // concurrent row was deleted by cleanup in the gap between the insert failure and the
        // re-read (a narrow race): this is an idempotency conflict, so a typed exception is returned
        // so the calling code follows the standard concurrency path instead of getting an
        // unhandled 500. A provider that reports no SQLSTATE (SQL Server in today's delivery)
        // cannot prove the race, so the original exception is rethrown by the caller.
        if (transaction.IdempotencyScope is not null
            && transaction.IdempotencyKey is not null
            && IsUniqueViolation(insertFailure))
        {
            _logger.LogWarning(
                "Idempotency key uniqueness violation in EF Core: the conflicting row was deleted before the re-read. TransactionId: {TransactionId}, Scope: {Scope}, KeyFingerprint: {KeyFingerprint}",
                transaction.Id,
                transaction.IdempotencyScope,
                IdempotencyKeyFingerprint.Compute(transaction.IdempotencyScope, transaction.IdempotencyKey));

            return new DuplicateIdempotencyKeyException(
                transaction.IdempotencyScope,
                transaction.IdempotencyKey,
                insertFailure);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<Transaction?> GetByIdAsync(TransactionId id, CancellationToken cancellationToken = default)
    {
        // Look up the transaction by primary key
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await context.Transactions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id.ToString(), cancellationToken);

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
        // Query by the composite index (IdempotencyScope, IdempotencyKey)
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await context.Transactions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.IdempotencyScope == scope && e.IdempotencyKey == key,
                cancellationToken);

        return entity is not null
            ? TransactionEntityMapper.ToDomain(entity)
            : null;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Transaction transaction,
        string expectedConcurrencyToken,
        CancellationToken cancellationToken = default)
    {
        // Load the entity, check the token, update, and save with EF concurrency
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await context.Transactions
            .FirstOrDefaultAsync(e => e.Id == transaction.Id.ToString(), cancellationToken);

        if (entity is null)
        {
            return false;
        }

        // Early check: if the token no longer matches — a concurrency conflict
        if (!string.Equals(entity.ConcurrencyToken, expectedConcurrencyToken, StringComparison.Ordinal))
        {
            return false;
        }

        // Update the mutable fields of the entity
        TransactionEntityMapper.UpdateEntity(entity, transaction);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug(
                "Transaction updated in EF Core. TransactionId: {TransactionId}, State: {State}",
                transaction.Id, transaction.State);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // EF Core detected a concurrent change via the UPDATE's WHERE condition
            _logger.LogWarning(
                "Concurrency conflict while updating the transaction in EF Core. TransactionId: {TransactionId}",
                transaction.Id);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(TransactionId id, CancellationToken cancellationToken = default)
    {
        // Delete the transaction by identifier (no error if not found)
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await context.Transactions
            .FirstOrDefaultAsync(e => e.Id == id.ToString(), cancellationToken);

        if (entity is null)
        {
            _logger.LogDebug(
                "Transaction not found on delete from EF Core (idempotent no-op). TransactionId: {TransactionId}",
                id);
            return;
        }

        context.Transactions.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogDebug(
            "Transaction deleted from EF Core. TransactionId: {TransactionId}",
            id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Transaction>> GetExpiredAsync(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        // Look for the transactions the passing of their deadline ends — the states the transition
        // table expires from, and no wider. "Not terminal" would also select a confirmed transaction,
        // which is past the part its deadline guards: the sweep cannot move it, and it would take the
        // slot of one the sweep has to expire.
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var expirableStates = ExpirableStateNames;

        var entities = await context.Transactions
            .AsNoTracking()
            .Where(e => expirableStates.Contains(e.State) && e.ExpiresAt <= now)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        return entities
            .Select(TransactionEntityMapper.ToDomain)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Transaction>> GetStaleTerminalAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        // Look for terminal transactions older than the given threshold.
        // States are compared via an explicit disjunction (not an IN list) — see the comment
        // at the state constants (a pattern retained from the since-removed MySQL provider).
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var completedState = CompletedStateName;
        var expiredState = ExpiredStateName;
        var failedState = FailedStateName;

        var entities = await context.Transactions
            .AsNoTracking()
            .Where(e => (e.State == completedState || e.State == expiredState || e.State == failedState)
                && e.UpdatedAt < olderThan)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        return entities
            .Select(TransactionEntityMapper.ToDomain)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Transaction>> GetStalledConfirmedAsync(
        DateTimeOffset confirmedBefore,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        // Look for Confirmed transactions stalled longer than allowed
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var confirmedState = ConfirmedStateName;

        var entities = await context.Transactions
            .AsNoTracking()
            .Where(e => e.State == confirmedState && e.UpdatedAt < confirmedBefore)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        return entities
            .Select(TransactionEntityMapper.ToDomain)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    /// One query by the fingerprint column and its index. A row written before the column existed
    /// carries no fingerprint and is not returned: such a sign-in lives until its deadline.
    /// </remarks>
    public async Task<IReadOnlyList<Transaction>> GetPendingSignInsAsync(
        SignInSubjectScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var pendingState = PendingStateName;
        var fingerprint = scope.ToFingerprint();

        var entities = await context.Transactions
            .AsNoTracking()
            .Where(e => e.State == pendingState && e.SignInSubjectFingerprint == fingerprint)
            .ToListAsync(cancellationToken);

        return entities
            .Select(TransactionEntityMapper.ToDomain)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    /// One conditional <c>UPDATE … WHERE Id = … AND &lt;column&gt; IS NULL</c>: the database decides
    /// which of two concurrent writers gets the row, and the statement touches neither
    /// <c>ConcurrencyToken</c> nor <c>UpdatedAt</c>, so a concurrent <see cref="UpdateAsync"/> keeps its
    /// token. Needs a provider that translates <c>ExecuteUpdate</c> (EF InMemory does not).
    /// </remarks>
    public async Task<bool> TryMarkHopObservationAsync(
        TransactionId id,
        HopObservationMark mark,
        DateTimeOffset markedAt,
        CancellationToken cancellationToken = default)
    {
        HopObservationArguments.ThrowIfUndefined(mark);

        var column = HopObservationColumn(mark);
        var key = id.ToString();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var written = await context.Transactions
            .Where(e => e.Id == key)
            .Where(IsNull(column))
            .ExecuteUpdateAsync(setters => setters.SetProperty(column, markedAt), cancellationToken);

        return written == 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One conditional <c>UPDATE</c> on the snapshot column while it is empty — the obligations and the
    /// provider requirement of <see cref="TryMarkHopObservationAsync"/> apply.
    /// </remarks>
    public async Task<bool> TryAttachScannerContextAsync(
        TransactionId id,
        ScannerContextSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        HopObservationArguments.ThrowIfInvalid(snapshot);

        var json = TransactionEntityMapper.SerializeScannerContext(snapshot);
        var key = id.ToString();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var written = await context.Transactions
            .Where(e => e.Id == key && e.ScannerContextSnapshotJson == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(e => e.ScannerContextSnapshotJson, json),
                cancellationToken);

        return written == 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One conditional <c>UPDATE</c> on the fingerprint column while it is empty and a snapshot is
    /// attached — the obligations and the provider requirement of
    /// <see cref="TryMarkHopObservationAsync"/> apply.
    /// </remarks>
    public async Task<bool> TryFillScannerFingerprintAsync(
        TransactionId id,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        HopObservationArguments.ThrowIfInvalidFingerprint(fingerprint);

        var key = id.ToString();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var written = await context.Transactions
            .Where(e => e.Id == key
                && e.ScannerFingerprint == null
                && e.ScannerContextSnapshotJson != null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(e => e.ScannerFingerprint, fingerprint),
                cancellationToken);

        return written == 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One conditional <c>UPDATE … WHERE Id = … AND State = 'Pending' AND ClaimCompletionJson IS NOT
    /// NULL AND ClaimCompletionSurface = expected</c>: the state, the mark and the current value are
    /// checked by the database in the same statement that writes. The statement touches neither
    /// <c>ConcurrencyToken</c> nor <c>UpdatedAt</c>, and <see cref="UpdateAsync"/> never includes the
    /// column. Needs a provider that translates <c>ExecuteUpdate</c> (EF InMemory does not).
    /// </remarks>
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

        var key = id.ToString();
        var pendingState = PendingStateName;
        var expectedName = expected?.ToString();
        var surfaceName = surface.ToString();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // A null expectedName compares as IS NULL: EF Core's C# null semantics apply to the parameter.
        var written = await context.Transactions
            .Where(e => e.Id == key
                && e.State == pendingState
                && e.ClaimCompletionJson != null
                && e.ClaimCompletionSurface == expectedName)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(e => e.ClaimCompletionSurface, surfaceName),
                cancellationToken);

        return written == 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One conditional <c>UPDATE … WHERE Id = … AND State = 'Pending' AND ClaimCompletionJson IS NOT
    /// NULL AND ClaimCompletionFormTokenHash IS NULL</c> — the obligations and the provider requirement of
    /// <see cref="TrySetWebStepSurfaceAsync"/> apply.
    /// </remarks>
    public async Task<bool> TrySetClaimFormTokenHashAsync(
        TransactionId id,
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        ClaimCompletionWrites.ThrowIfInvalidFormTokenHash(tokenHash);

        var key = id.ToString();
        var pendingState = PendingStateName;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var written = await context.Transactions
            .Where(e => e.Id == key
                && e.State == pendingState
                && e.ClaimCompletionJson != null
                && e.ClaimCompletionFormTokenHash == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(e => e.ClaimCompletionFormTokenHash, tokenHash),
                cancellationToken);

        return written == 1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The outcomes are one JSON column, so the write is a compare-and-swap over it: the row is read,
    /// the condition is checked on what was read, and one conditional <c>UPDATE</c> writes the new list
    /// only while the state is still Pending and both the mark and the list are exactly the ones read.
    /// Losing the swap means another write changed the row in between; the loop reads again and decides
    /// anew. It ends: every lost swap is a write that did go through, and the writes that can change
    /// these columns are bounded — an outcome per missing claim, the state leaving Pending.
    /// The statement touches neither <c>ConcurrencyToken</c> nor <c>UpdatedAt</c>, and
    /// <see cref="UpdateAsync"/> never includes the column. Needs a provider that translates
    /// <c>ExecuteUpdate</c> (EF InMemory does not).
    /// </remarks>
    public async Task<bool> TryRecordClaimResultAsync(
        TransactionId id,
        ClaimCompletionResult result,
        CancellationToken cancellationToken = default)
    {
        ClaimCompletionWrites.ThrowIfInvalid(result);

        var key = id.ToString();
        var pendingState = PendingStateName;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        while (true)
        {
            var row = await context.Transactions
                .AsNoTracking()
                .Where(e => e.Id == key)
                .Select(e => new { e.State, e.ClaimCompletionJson, e.ClaimCompletionResultsJson })
                .FirstOrDefaultAsync(cancellationToken);

            if (row is null)
            {
                return false;
            }

            var results = TransactionEntityMapper.DeserializeClaimCompletionResults(row.ClaimCompletionResultsJson);

            if (!Enum.TryParse<TransactionState>(row.State, out var state)
                || !ClaimCompletionWrites.CanRecord(
                    state,
                    TransactionEntityMapper.DeserializeClaimCompletionItems(row.ClaimCompletionJson),
                    results,
                    result.Claim))
            {
                return false;
            }

            var markJson = row.ClaimCompletionJson;
            var readResultsJson = row.ClaimCompletionResultsJson;
            var newResultsJson = TransactionEntityMapper.SerializeClaimCompletionResults(
                ClaimCompletionWrites.Append(results, result));

            // A null readResultsJson compares as IS NULL: EF Core's C# null semantics apply to the parameter.
            var written = await context.Transactions
                .Where(e => e.Id == key
                    && e.State == pendingState
                    && e.ClaimCompletionJson == markJson
                    && e.ClaimCompletionResultsJson == readResultsJson)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(e => e.ClaimCompletionResultsJson, newResultsJson),
                    cancellationToken);

            if (written == 1)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Column a hop observation mark is written into.
    /// </summary>
    /// <param name="mark">Mark already checked by <see cref="HopObservationArguments.ThrowIfUndefined"/>.</param>
    /// <returns>Selector of the entity column of the mark.</returns>
    private static Expression<Func<TransactionEntity, DateTimeOffset?>> HopObservationColumn(HopObservationMark mark)
        => mark switch
        {
            HopObservationMark.FirstResolve => e => e.HopFirstResolvedAt,
            HopObservationMark.InterstitialOpened => e => e.HopInterstitialOpenedAt,
            HopObservationMark.ChannelLaunched => e => e.HopChannelLaunchedAt,
            HopObservationMark.RepeatResolve => e => e.HopRepeatResolvedAt,
            _ => throw new UnreachableException()
        };

    /// <summary>
    /// Filter "the column is empty" over a column selector, so the column is named once per mark and
    /// the filter and the setter of the conditional write cannot drift apart.
    /// </summary>
    /// <param name="column">Column selector.</param>
    /// <returns>Predicate <c>column IS NULL</c>.</returns>
    private static Expression<Func<TransactionEntity, bool>> IsNull(Expression<Func<TransactionEntity, DateTimeOffset?>> column)
        => Expression.Lambda<Func<TransactionEntity, bool>>(
            Expression.Equal(column.Body, Expression.Constant(null, typeof(DateTimeOffset?))),
            column.Parameters);

    /// <summary>
    /// Determines whether a DbUpdateException may be an integrity constraint violation and is worth
    /// classifying by re-reading the database state in the AddAsync handler.
    /// Provider-agnostic: a DbException is in the chain, the provider does not report it as transient
    /// (<c>DbException.IsTransient</c> — a transient error is not a constraint violation), and either
    /// its SQLSTATE is of class 23 (ANSI integrity constraint violation), or it carries no SQLSTATE at
    /// all. <c>DbException.SqlState</c> is optional: SQL Server (Microsoft.Data.SqlClient) leaves it
    /// unset for every error, so there the re-read is the only classifier; Npgsql fills it only for
    /// errors reported by the server (<c>PostgresException</c>), while a client-side error such as a
    /// broken connection (<c>NpgsqlException</c>) carries none. So SQLSTATE keeps only server-reported
    /// non-integrity errors out of the re-read; a client-side error the provider does not mark as
    /// transient still reaches it (on SQL Server in today's delivery that is every error, since
    /// Microsoft.Data.SqlClient does not override <c>IsTransient</c>), and a failure of the re-read
    /// leaves the original exception in place (see AddAsync).
    /// </summary>
    /// <param name="ex">EF Core update exception.</param>
    /// <returns>true if the cause may be an integrity constraint violation.</returns>
    private static bool IsPossibleIntegrityConstraintViolation(DbUpdateException ex)
    {
        return FindDbException(ex) is { IsTransient: false } dbException
               && (dbException.SqlState is null
                   || dbException.SqlState.StartsWith(IntegrityConstraintViolationSqlStateClass, StringComparison.Ordinal));
    }

    /// <summary>
    /// Determines whether the provider reports the exception as exactly a unique constraint
    /// violation (SQLSTATE 23505). A provider without SQLSTATE, or one reporting a generic integrity
    /// state, makes the method return false, and the AddAsync handler rethrows the original exception
    /// instead of guessing.
    /// </summary>
    /// <param name="ex">EF Core update exception.</param>
    /// <returns>true if the cause is specifically a uniqueness violation.</returns>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return FindDbException(ex) is { SqlState: { } sqlState }
               && string.Equals(sqlState, UniqueViolationSqlState, StringComparison.Ordinal);
    }

    /// <summary>
    /// Finds the first DbException in the chain of inner exceptions.
    /// </summary>
    /// <param name="ex">Root exception.</param>
    /// <returns>DbException, or null if the chain contains none.</returns>
    private static DbException? FindDbException(Exception ex)
    {
        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is DbException dbException)
            {
                return dbException;
            }
        }

        return null;
    }
}
