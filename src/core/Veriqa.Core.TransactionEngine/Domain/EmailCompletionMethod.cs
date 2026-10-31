// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// A method of obtaining the email during the claims completion (SPEC-041 C96) — a member of the ordered
/// list the configuration allows. The members are not the ways the user chooses from: <c>Code</c> and
/// <c>MagicLink</c> stand for one way, and the list decides only what its mail carries. The value the
/// collected email is recorded with is the matching
/// <see cref="ClaimCollectionMethod"/>; the translation between the two lives in
/// <see cref="EmailCompletionMethods"/> alone.
/// </summary>
/// <remarks>
/// The configuration states the members by name; the numeric values are the storage format.
/// </remarks>
public enum EmailCompletionMethod
{
    /// <summary>
    /// The user sends a prepared message from the mailbox; the address and its ownership come from that
    /// message (SPEC-041 L50). Available only on the page of the device that confirmed the sign-in.
    /// </summary>
    Push = 0,

    /// <summary>
    /// The user enters the address and then the code of the mail sent to it, on the same page (SPEC-041 R12).
    /// </summary>
    Code = 1,

    /// <summary>
    /// The user enters the address and confirms it on the page a link of the mail sent to it opens.
    /// </summary>
    MagicLink = 2,

    /// <summary>
    /// The user enters the address, and it is not confirmed: <c>email_verified</c> is <c>false</c>.
    /// </summary>
    Unverified = 3
}
