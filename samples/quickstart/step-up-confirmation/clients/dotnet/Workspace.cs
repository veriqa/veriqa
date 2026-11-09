// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Sample.Quickstart.StepUpConfirmation;

/// <summary>A project of the workspace.</summary>
public sealed record Project(string Id, string Name, int Documents);

/// <summary>What the page reads about the workspace: the owner (null — none yet) and the projects.</summary>
public sealed record WorkspaceSnapshot(string? Owner, IReadOnlyList<Project> Projects);

/// <summary>
/// A deletion waiting for its confirmation. <paramref name="Verdict"/> is null while nobody has
/// answered, then one of <see cref="Verdicts"/>.
/// </summary>
public sealed record Deletion(string TransactionId, string ProjectId, string Outcome, string? Verdict);

/// <summary>How a deletion ended.</summary>
public static class Verdicts
{
    /// <summary>Confirmed by the owner: deleted.</summary>
    public const string Deleted = "deleted";

    /// <summary>No owner yet: deleted, and the person who confirmed became the owner.</summary>
    public const string DeletedOwnerBound = "deleted_owner_bound";

    /// <summary>Confirmed, but by somebody other than the owner: not deleted.</summary>
    public const string ConfirmedByAnotherPerson = "confirmed_by_another_person";

    /// <summary>Declined, expired or otherwise not confirmed: not deleted.</summary>
    public const string NotDeleted = "not_deleted";
}

/// <summary>
/// The in-memory workspace of the sample: three projects and the owner of the account. A real
/// application knows the owner's channel identity from account linking; to stay self-contained, the
/// sample binds the owner to the first person who confirms a deletion.
/// </summary>
public sealed class Workspace
{
    /// <summary>The identity type the step-up names in expected_identities, declared in veriqa.json.</summary>
    public const string OwnerIdentityType = "telegram_user_id";

    private readonly Lock _gate = new();
    private readonly List<Project> _projects =
    [
        new("q3-report", "Q3 report", 12),
        new("customer-onboarding", "Customer onboarding", 48),
        new("archive-2024", "Archive 2024", 305)
    ];

    private readonly Dictionary<string, Deletion> _deletions = new(StringComparer.Ordinal);
    private string? _owner;

    /// <summary>The owner's channel user id; null — none yet.</summary>
    public string? Owner
    {
        get
        {
            lock (_gate)
            {
                return _owner;
            }
        }
    }

    /// <summary>The workspace for the page.</summary>
    public WorkspaceSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new WorkspaceSnapshot(_owner, [.. _projects]);
        }
    }

    /// <summary>Forgets the owner: the next person to confirm a deletion becomes the owner.</summary>
    public void ResetOwner()
    {
        lock (_gate)
        {
            _owner = null;
        }
    }

    /// <summary>A project by id, or null.</summary>
    public Project? Find(string projectId)
    {
        lock (_gate)
        {
            return _projects.Find(project => project.Id == projectId);
        }
    }

    /// <summary>
    /// Remembers which project a transaction deletes — on the server: the page never says which project
    /// a confirmed transaction deletes.
    /// </summary>
    public void StartDeletion(string transactionId, string projectId)
    {
        lock (_gate)
        {
            _deletions[transactionId] = new Deletion(transactionId, projectId, ConfirmationResult.Pending, null);
        }
    }

    /// <summary>A deletion by transaction id, or null.</summary>
    public Deletion? FindDeletion(string transactionId)
    {
        lock (_gate)
        {
            return _deletions.GetValueOrDefault(transactionId);
        }
    }

    /// <summary>
    /// Records how a deletion ended; a deleting verdict removes the project, and
    /// <paramref name="newOwner"/>, when given, becomes the owner.
    /// </summary>
    public Deletion Conclude(string transactionId, string outcome, string verdict, string? newOwner = null)
    {
        lock (_gate)
        {
            var deletion = _deletions[transactionId] with { Outcome = outcome, Verdict = verdict };
            _deletions[transactionId] = deletion;
            if (newOwner is not null)
            {
                _owner = newOwner;
            }

            if (verdict is Verdicts.Deleted or Verdicts.DeletedOwnerBound)
            {
                _projects.RemoveAll(project => project.Id == deletion.ProjectId);
            }

            return deletion;
        }
    }
}
