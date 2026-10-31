// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.Contracts.Audit;

/// <summary>
/// Stable action codes of the audited transaction lifecycle events (SPEC-011 C29).
/// </summary>
/// <remarks>
/// The code equals the name of the bus event type without the <c>Event</c> suffix. The codes are
/// listed explicitly rather than derived by trimming a type name: the journal's vocabulary is a
/// contract of its own, and renaming an event type must not silently rewrite history's codes.
/// Channel security events carry the code declared by the channel itself — no second dictionary
/// of names is introduced for them here.
/// </remarks>
public static class AuditActionCodes
{
    /// <summary>
    /// Transaction created.
    /// </summary>
    public const string TransactionCreated = "TransactionCreated";

    /// <summary>
    /// Transaction activated.
    /// </summary>
    public const string TransactionActivated = "TransactionActivated";

    /// <summary>
    /// Channel identity attached to the transaction.
    /// </summary>
    public const string TransactionChannelIdentityAttached = "TransactionChannelIdentityAttached";

    /// <summary>
    /// Transaction confirmed by the user.
    /// </summary>
    public const string TransactionConfirmed = "TransactionConfirmed";

    /// <summary>
    /// Transaction completed successfully.
    /// </summary>
    public const string TransactionCompleted = "TransactionCompleted";

    /// <summary>
    /// Transaction ended with an error.
    /// </summary>
    public const string TransactionFailed = "TransactionFailed";

    /// <summary>
    /// Transaction expired by TTL.
    /// </summary>
    public const string TransactionExpired = "TransactionExpired";

    /// <summary>
    /// The collected claims of a subject deleted (SPEC-041 L85): by the server endpoint, by a call of the
    /// host, or by the automatic removal of the keys unused for the retention period. Not a bus event: the
    /// deletion writes the record itself, with the basis in <see cref="AuditMetadata.DeletionBasis"/>.
    /// </summary>
    public const string CollectedClaimsDeleted = "CollectedClaimsDeleted";
}
