// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.EntityFrameworkCore;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Store;

namespace Veriqa.Core.TransactionEngine.CollectedClaims.EfCore;

/// <summary>
/// EF Core implementation of <see cref="ICollectedClaimsStore"/> (SPEC-041 C37). Each operation creates its
/// own context through <see cref="IDbContextFactory{TContext}"/>, so the store holds no state between calls
/// and is safe under any lifetime.
/// </summary>
/// <remarks>
/// Moments are written and compared in UTC: a relational provider may refuse a non-zero offset (Npgsql does
/// for <c>timestamptz</c>), and the contract speaks of UTC moments anyway.
/// </remarks>
internal sealed class EfCoreCollectedClaimsStore : ICollectedClaimsStore
{
    /// <summary>
    /// Attempts of a read-then-write operation. The second one covers a concurrent writer that created or
    /// deleted the same key between the read and the save of the first: the retry reads the key again and
    /// applies the same change to what it finds.
    /// </summary>
    private const int MaxWriteAttempts = 2;

    /// <summary>
    /// DbContext factory.
    /// </summary>
    private readonly IDbContextFactory<CollectedClaimsDbContext> _contextFactory;

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="contextFactory">DbContext factory.</param>
    public EfCoreCollectedClaimsStore(IDbContextFactory<CollectedClaimsDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClaimCompletionResult>> ReadForSignInAsync(
        string? tenantId,
        string subject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // The last sign-in time is set first and by one conditional statement: a key the statement did not
        // find has no records to read, and a key it did find is kept by automatic removal from this moment
        // on, even if that removal selected it a moment earlier.
        ArgumentException.ThrowIfNullOrEmpty(subject);

        var segment = TenantKey.Segment(tenantId);
        var signInAt = now.ToUniversalTime();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var touched = await context.CollectedClaimSubjects
            .Where(key => key.TenantSegment == segment && key.Subject == subject)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(key => key.LastSignInAt, signInAt),
                cancellationToken);

        if (touched == 0)
        {
            return [];
        }

        return await context.CollectedClaimSubjects
            .Where(key => key.TenantSegment == segment && key.Subject == subject)
            .SelectMany(key => key.Claims)
            .Select(record => new ClaimCompletionResult(
                record.Claim,
                record.Value,
                record.Verified,
                record.Method,
                record.RecordedAt))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        string? tenantId,
        string subject,
        IReadOnlyList<ClaimCompletionResult> records,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Upsert of the key and of one record per claim: an existing record of the claim is overwritten in
        // place, whether it held a value or a skip. Claim names are matched ignoring case, as claim names
        // are compared everywhere else — and as the primary key compares them on a case-insensitive
        // collation.
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentNullException.ThrowIfNull(records);

        foreach (var record in records)
        {
            ClaimCompletionWrites.ThrowIfInvalid(record);
        }

        var segment = TenantKey.Segment(tenantId);
        var signInAt = now.ToUniversalTime();

        await WriteWithRetryAsync(
            async (context, token) =>
            {
                var key = await context.CollectedClaimSubjects
                    .Include(stored => stored.Claims)
                    .SingleOrDefaultAsync(
                        stored => stored.TenantSegment == segment && stored.Subject == subject,
                        token);

                if (key is null)
                {
                    key = new CollectedClaimsSubjectEntity { TenantSegment = segment, Subject = subject };
                    context.CollectedClaimSubjects.Add(key);
                }

                key.LastSignInAt = signInAt;

                foreach (var record in records)
                {
                    var stored = key.Claims.FirstOrDefault(
                        claim => string.Equals(claim.Claim, record.Claim, StringComparison.OrdinalIgnoreCase));

                    if (stored is null)
                    {
                        stored = new CollectedClaimEntity { Claim = record.Claim };
                        key.Claims.Add(stored);
                    }

                    stored.Value = record.Value;
                    stored.Verified = record.Verified;
                    stored.Method = record.Method;
                    stored.RecordedAt = record.RecordedAt.ToUniversalTime();
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveIssuedSubjectAsync(
        string? tenantId,
        string subject,
        string applicationId,
        string issuedSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Upsert of the key and of the (application, issued subject) entry. Every subject an application was
        // ever issued is kept: deletion by any of them has to find the key.
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(applicationId);
        ArgumentException.ThrowIfNullOrEmpty(issuedSubject);

        var segment = TenantKey.Segment(tenantId);
        var createdAt = now.ToUniversalTime();

        await WriteWithRetryAsync(
            async (context, token) =>
            {
                // Only the entry itself is loaded, and it is matched by the database: whether it is already
                // known is then decided by the same comparison as the key lookup and the unique index on
                // (key, application, issued subject), whatever collation the provider gives the columns — a
                // comparison of its own here could miss an entry the index holds as equal.
                var key = await context.CollectedClaimSubjects
                    .Include(stored => stored.IssuedSubjects.Where(entry =>
                        entry.ApplicationId == applicationId && entry.IssuedSubject == issuedSubject))
                    .SingleOrDefaultAsync(
                        stored => stored.TenantSegment == segment && stored.Subject == subject,
                        token);

                // A new key starts its unused period now; an existing key keeps its last sign-in time.
                if (key is null)
                {
                    key = new CollectedClaimsSubjectEntity
                    {
                        TenantSegment = segment,
                        Subject = subject,
                        LastSignInAt = createdAt
                    };
                    context.CollectedClaimSubjects.Add(key);
                }

                if (key.IssuedSubjects.Count == 0)
                {
                    key.IssuedSubjects.Add(new CollectedClaimsIssuedSubjectEntity
                    {
                        ApplicationId = applicationId,
                        IssuedSubject = issuedSubject
                    });
                }
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FindSubjectsAsync(
        string? tenantId,
        string applicationId,
        string issuedSubject,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationId);
        ArgumentException.ThrowIfNullOrEmpty(issuedSubject);

        var segment = TenantKey.Segment(tenantId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.CollectedClaimSubjects
            .Where(key => key.TenantSegment == segment
                && key.IssuedSubjects.Any(entry =>
                    entry.ApplicationId == applicationId && entry.IssuedSubject == issuedSubject))
            .Select(key => key.Subject)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string? tenantId,
        string subject,
        DateTimeOffset? unusedBefore = null,
        CancellationToken cancellationToken = default)
    {
        // One DELETE with the whole condition: the check of the last sign-in time and the deletion cannot be
        // split by a sign-in. The records and the issued subjects go with the key by the cascade of the
        // database.
        ArgumentException.ThrowIfNullOrEmpty(subject);

        var segment = TenantKey.Segment(tenantId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var keys = context.CollectedClaimSubjects
            .Where(key => key.TenantSegment == segment && key.Subject == subject);

        if (unusedBefore is { } before)
        {
            var threshold = before.ToUniversalTime();
            keys = keys.Where(key => key.LastSignInAt < threshold);
        }

        var deleted = await keys.ExecuteDeleteAsync(cancellationToken);

        return deleted > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string?>> ListTenantsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var segments = await context.CollectedClaimSubjects
            .Select(key => key.TenantSegment)
            .Distinct()
            .ToListAsync(cancellationToken);

        return segments
            .Select(segment => string.Equals(segment, TenantKey.Default, StringComparison.Ordinal) ? null : segment)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListUnusedSubjectsAsync(
        string? tenantId,
        DateTimeOffset before,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var segment = TenantKey.Segment(tenantId);
        var threshold = before.ToUniversalTime();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.CollectedClaimSubjects
            .Where(key => key.TenantSegment == segment && key.LastSignInAt < threshold)
            .OrderBy(key => key.LastSignInAt)
            .Take(limit)
            .Select(key => key.Subject)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Runs a read-then-write change on a fresh context and saves it; when the save fails — a concurrent
    /// writer created the same key (the unique index) or deleted the one read — runs the change once more on
    /// another fresh context, which reads the key as it is now. A failure of the last attempt propagates.
    /// </summary>
    /// <param name="change">Reads the key and applies the change to the tracked entities; saves nothing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task representing the completion of the operation.</returns>
    private async Task WriteWithRetryAsync(
        Func<CollectedClaimsDbContext, CancellationToken, Task> change,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            await change(context, cancellationToken);

            try
            {
                await context.SaveChangesAsync(cancellationToken);

                return;
            }
            catch (DbUpdateException) when (attempt < MaxWriteAttempts)
            {
                // The key changed under this attempt: the next one reads it again.
            }
        }
    }
}
