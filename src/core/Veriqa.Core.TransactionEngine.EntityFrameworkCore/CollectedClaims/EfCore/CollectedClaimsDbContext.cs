// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.EntityFrameworkCore;

namespace Veriqa.Core.TransactionEngine.CollectedClaims.EfCore;

/// <summary>
/// DbContext of the collected claims store (SPEC-041 C37).
/// Public as an anchor for migrations assemblies and design-time factories (<c>[DbContext]</c>,
/// <c>IDesignTimeDbContextFactory&lt;T&gt;</c>) of any relational provider — not as a data access API: the
/// entity sets stay internal, and the store is reached through <c>ICollectedClaimsStore</c>.
/// Used at runtime only via IDbContextFactory. The model is provider-agnostic: the provider, the migrations
/// assembly and the migration history table are set by the host when calling
/// <c>UseEfCoreCollectedClaimsStore</c>; the history table is
/// <see cref="CollectedClaimsMigrationDefaults.MigrationsHistoryTable"/>, because the context may share a
/// database with the transaction store.
/// </summary>
public sealed class CollectedClaimsDbContext : DbContext
{
    /// <summary>
    /// Maximum length of the tenant, application and claim name columns. The columns take part in keys and
    /// indexes, and a bounded length keeps them indexable on every provider (an unbounded string maps to a
    /// type some providers refuse in an index key).
    /// </summary>
    private const int IdentifierColumnMaxLength = 256;

    /// <summary>
    /// Maximum length of the subject columns. Longer than the other identifiers: a core subject is a channel
    /// type joined to a channel user identifier, and the latter may be a full email address.
    /// </summary>
    private const int SubjectColumnMaxLength = 512;

    /// <summary>
    /// Keys of the store.
    /// </summary>
    internal DbSet<CollectedClaimsSubjectEntity> CollectedClaimSubjects { get; init; } = null!;

    /// <summary>
    /// Records of the keys.
    /// </summary>
    internal DbSet<CollectedClaimEntity> CollectedClaims { get; init; } = null!;

    /// <summary>
    /// Subjects the applications received for the keys.
    /// </summary>
    internal DbSet<CollectedClaimsIssuedSubjectEntity> CollectedClaimIssuedSubjects { get; init; } = null!;

    /// <summary>
    /// Creates a context instance with the given options.
    /// </summary>
    /// <param name="options">DbContext options.</param>
    public CollectedClaimsDbContext(DbContextOptions<CollectedClaimsDbContext> options)
        : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CollectedClaimsSubjectEntity>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.TenantSegment).HasMaxLength(IdentifierColumnMaxLength);
            entity.Property(e => e.Subject).HasMaxLength(SubjectColumnMaxLength);

            // One key per (tenant, subject): an upsert that lost a race meets this index instead of adding a
            // second key.
            entity.HasIndex(e => new { e.TenantSegment, e.Subject }).IsUnique();

            // Automatic removal selects the keys of a tenant unused since a moment.
            entity.HasIndex(e => new { e.TenantSegment, e.LastSignInAt });

            // Records and issued subjects are required dependents: the database deletes them with their key
            // (cascade), so deleting a key is one statement.
            entity.HasMany(e => e.Claims)
                .WithOne()
                .HasForeignKey(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.IssuedSubjects)
                .WithOne()
                .HasForeignKey(e => e.SubjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CollectedClaimEntity>(entity =>
        {
            // At most one record per claim of a key.
            entity.HasKey(e => new { e.SubjectId, e.Claim });

            entity.Property(e => e.Claim).HasMaxLength(IdentifierColumnMaxLength);
        });

        modelBuilder.Entity<CollectedClaimsIssuedSubjectEntity>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ApplicationId).HasMaxLength(IdentifierColumnMaxLength);
            entity.Property(e => e.IssuedSubject).HasMaxLength(SubjectColumnMaxLength);

            // One entry per (key, application, issued subject).
            entity.HasIndex(e => new { e.SubjectId, e.ApplicationId, e.IssuedSubject }).IsUnique();

            // Deletion by the subject an application knows looks the keys up by these two.
            entity.HasIndex(e => new { e.ApplicationId, e.IssuedSubject });
        });
    }
}
