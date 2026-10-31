// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// Level of a requirement to a claim (SPEC-041 R79) — one enumeration for the configuration that
/// states the requirement and for the transaction that records it.
/// <para>
/// The numeric values are part of the storage format of a transaction and their order is the
/// seniority of the levels: when several sources state a level for the same claim, the greater value
/// wins (SPEC-041 L40).
/// </para>
/// </summary>
public enum ClaimRequirementLevel
{
    /// <summary>
    /// The claim is issued only when it is available without any action of the user — from the channel
    /// or from the store. The level every claim has when nothing states otherwise.
    /// </summary>
    IfAvailable = 0,

    /// <summary>
    /// The user is asked for the claim and may decline; the sign-in goes on either way.
    /// </summary>
    Optional = 1,

    /// <summary>
    /// The claim is mandatory: without it the sign-in does not complete.
    /// </summary>
    Required = 2
}
