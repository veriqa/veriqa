// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.CollectedClaims;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Sample.DotNet.Showcase.StreamingTv;

/// <summary>
/// In-memory implementation of <see cref="ICollectedClaimsStore"/> — the port behind "asked once, not
/// asked again": what a viewer entered on the completion form of Veriqa, or chose to skip, is kept
/// per person and read at their next sign-in. Veriqa ships an EF Core implementation of the port and
/// no in-memory one, so a sample that runs without a database brings this.
/// <para>
/// A key is (tenant, core subject). The core subject is the one Veriqa derives from the channel
/// identity, so the same person coming through another channel is another key and is asked again.
/// Per claim a key holds one record: a value, or a null value — the mark that the viewer skipped the
/// claim. Everything is lost on restart; a real deployment keeps the store in a database.
/// </para>
/// <para>
/// The records live in a static field, not in the instance: <c>UseCollectedClaimsStore</c> registers
/// the port as a scoped service and the engine opens a scope of its own for every operation, so an
/// instance — and anything it held in a field — is gone before the next sign-in begins.
/// </para>
/// </summary>
public sealed class InMemoryCollectedClaimsStore : ICollectedClaimsStore
{
    /// <summary>Guards <see cref="Keys"/> and everything inside its entries.</summary>
    private static readonly Lock Gate = new();

    /// <summary>
    /// The store itself. The tenant is spelled with <see cref="TenantKey.Segment"/>, so an unstated
    /// tenant is one key and not two (null and an empty string).
    /// </summary>
    private static readonly Dictionary<(string Tenant, string Subject), Entry> Keys = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<ClaimCompletionResult>> ReadForSignInAsync(
        string? tenantId,
        string subject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);

        lock (Gate)
        {
            if (!Keys.TryGetValue((TenantKey.Segment(tenantId), subject), out var entry))
            {
                return Task.FromResult<IReadOnlyList<ClaimCompletionResult>>([]);
            }

            // Reading for a sign-in is what keeps a key alive: automatic removal counts from here.
            entry.LastSignInAt = now;

            return Task.FromResult<IReadOnlyList<ClaimCompletionResult>>([.. entry.Claims.Values]);
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(
        string? tenantId,
        string subject,
        IReadOnlyList<ClaimCompletionResult> records,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentNullException.ThrowIfNull(records);

        // Every record is checked before any is written: a refused call leaves the key as it was.
        foreach (var record in records)
        {
            ThrowIfInvalid(record);
        }

        lock (Gate)
        {
            var entry = EntryOf(tenantId, subject, now);
            entry.LastSignInAt = now;

            // One record per claim: a later one replaces the earlier, a value and a skip alike.
            foreach (var record in records)
            {
                entry.Claims[record.Claim] = record;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveIssuedSubjectAsync(
        string? tenantId,
        string subject,
        string applicationId,
        string issuedSubject,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentException.ThrowIfNullOrEmpty(applicationId);
        ArgumentException.ThrowIfNullOrEmpty(issuedSubject);

        lock (Gate)
        {
            // A key created here starts its unused period now; an existing one keeps its last sign-in.
            EntryOf(tenantId, subject, now).IssuedSubjects.Add((applicationId, issuedSubject));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> FindSubjectsAsync(
        string? tenantId,
        string applicationId,
        string issuedSubject,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationId);
        ArgumentException.ThrowIfNullOrEmpty(issuedSubject);

        var tenant = TenantKey.Segment(tenantId);

        lock (Gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. Keys
                .Where(key => key.Key.Tenant == tenant && key.Value.IssuedSubjects.Contains((applicationId, issuedSubject)))
                .Select(key => key.Key.Subject)]);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(
        string? tenantId,
        string subject,
        DateTimeOffset? unusedBefore = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);

        var key = (TenantKey.Segment(tenantId), subject);

        lock (Gate)
        {
            // The check of the last sign-in and the deletion sit under one lock: a sign-in cannot
            // slip between them and lose the records it has just read.
            var deletable = Keys.TryGetValue(key, out var entry)
                && (unusedBefore is not { } threshold || entry.LastSignInAt < threshold);

            return Task.FromResult(deletable && Keys.Remove(key));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string?>> ListTenantsAsync(CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            return Task.FromResult<IReadOnlyList<string?>>([.. Keys.Keys
                .Select(key => key.Tenant)
                .Distinct(StringComparer.Ordinal)
                .Select(tenant => tenant == TenantKey.Default ? null : tenant)]);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListUnusedSubjectsAsync(
        string? tenantId,
        DateTimeOffset before,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var tenant = TenantKey.Segment(tenantId);

        lock (Gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. Keys
                .Where(key => key.Key.Tenant == tenant && key.Value.LastSignInAt < before)
                .OrderBy(key => key.Value.LastSignInAt)
                .Take(limit)
                .Select(key => key.Key.Subject)]);
        }
    }

    /// <summary>
    /// The entry of a key, created when the key does not exist yet. Called under <see cref="Gate"/>.
    /// </summary>
    /// <param name="tenantId">Tenant of the key; null — the implicit tenant.</param>
    /// <param name="subject">Core subject of the key.</param>
    /// <param name="now">Last sign-in time a new entry starts with.</param>
    private static Entry EntryOf(string? tenantId, string subject, DateTimeOffset now)
    {
        var key = (TenantKey.Segment(tenantId), subject);

        if (!Keys.TryGetValue(key, out var entry))
        {
            entry = new Entry { LastSignInAt = now };
            Keys[key] = entry;
        }

        return entry;
    }

    /// <summary>
    /// Refuses a record the contract of the port does not admit: one that names no claim, one that
    /// carries a value without a method or a method without a value, and a skip marked verified.
    /// </summary>
    /// <param name="record">Record to check.</param>
    /// <exception cref="ArgumentException">The record is not a value and not a skip.</exception>
    private static void ThrowIfInvalid(ClaimCompletionResult record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.Claim))
        {
            throw new ArgumentException("The record names no claim.", nameof(record));
        }

        if ((record.Value is null) != (record.Method is null) || (record.Value is null && record.Verified))
        {
            throw new ArgumentException(
                "A collected value carries both a value and a method; a skip carries neither and is not verified.",
                nameof(record));
        }
    }

    /// <summary>What the store keeps for one key.</summary>
    private sealed class Entry
    {
        /// <summary>Last sign-in with this key — the moment automatic removal counts from.</summary>
        public DateTimeOffset LastSignInAt { get; set; }

        /// <summary>
        /// One record per claim. Claim names are compared the way Veriqa compares them everywhere
        /// (<see cref="VeriqaClaimTypes.NameComparer"/>).
        /// </summary>
        public Dictionary<string, ClaimCompletionResult> Claims { get; } = new(VeriqaClaimTypes.NameComparer);

        /// <summary>
        /// Every (application, subject) pair issued for this key: deletion by the subject an
        /// application knows has to find the key.
        /// </summary>
        public HashSet<(string ApplicationId, string IssuedSubject)> IssuedSubjects { get; } = [];
    }
}
